if (Process.arch !== 'arm64') throw Error('Controller priority requires ARM64');
const runtime = Process.getModuleByName('libvrapiimpl.so');
const code = runtime.enumerateRanges('r-x');
send({event: 'controller-runtime', path: runtime.path, size: runtime.size,
    executableRanges: code.length});
function unsupported(detail) { throw Error('Unsupported controller routing code; no hook applied (' + detail + ')'); }
function scan(pattern) {
    return code.flatMap(range => Memory.scanSync(range.base, range.size, pattern));
}
const number = '-?(?:0x[0-9a-f]+|[0-9]+)';
function integer(value) { return value[0] === '-' ? -Number(value.slice(1)) : Number(value); }
function match(i, mnemonic, operands) {
    return i.mnemonic === mnemonic ? i.opStr.match(new RegExp('^' + operands + '$')) : null;
}
const decoded = new Map();
function decode(address) {
    const key = address.toString();
    if (decoded.has(key)) return decoded.get(key);
    if ((address.toUInt32() & 3) !== 0 || !code.some(r => address.compare(r.base) >= 0 &&
        address.add(4).compare(r.base.add(r.size)) <= 0)) throw Error('Instruction outside executable code');
    const instruction = Instruction.parse(address);
    decoded.set(key, instruction);
    return instruction;
}
const stack = '(w[0-9]+), \\[sp, #(' + number + ')\\]';
function byteSlot(address, slot) {
    const m = match(decode(address), 'ldrb', stack);
    return m && integer(m[2]) === slot ? m[1] : null;
}
function storedSlot(address, register) {
    const m = match(decode(address), 'str', stack);
    return m && m[1] === register ? integer(m[2]) : null;
}
function branch(i, mnemonic, prefix='') {
    const m = match(i, mnemonic, prefix + '#(0x[0-9a-f]+)');
    return m ? ptr(m[1]) : null;
}

function queryAbi(target) {
    const instructions = [];
    for (let i=0; i<96; i++) {
        const instruction = decode(target.add(i*4));
        instructions.push(instruction);
        if (instruction.mnemonic === 'ret') break;
    }
    if (instructions.at(-1).mnemonic !== 'ret') return false;
    const saves = {};
    for (const i of instructions.slice(0, 8)) {
        const m = match(i, 'mov', '([xw][0-9]+), ([xw][012])');
        if (m) saves[m[2]] = m[1];
    }
    if (!/^x(?:19|2[0-8])$/.test(saves.x0) || !/^x(?:19|2[0-8])$/.test(saves.x2) ||
        !/^w(?:19|2[0-8])$/.test(saves.w1)) return false;
    if (new Set([saves.x0.slice(1), saves.x2.slice(1), saves.w1.slice(1)]).size !== 3) return false;
    for (let n=8; n+8<instructions.length; n++) {
        const block = instructions.slice(n, n+9);
        if (!match(block[0], 'ldr', 'x0, \\[' + saves.x0 + ', #(' + number + ')\\]') ||
            !match(block[1], 'cbz', 'x0, #(0x[0-9a-f]+)')) continue;
        const table = match(block[2], 'ldr', '(x[0-9]+), \\[x0\\]');
        if (!table) continue;
        const method = match(block[3], 'ldr', '(x[0-9]+), \\[' + table[1] + ', #(' + number + ')\\]');
        if (!method || !match(block[4], 'mov', 'w1, ' + saves.w1) ||
            !match(block[5], 'mov', 'x2, ' + saves.x2) || !match(block[6], 'blr', method[1])) continue;
        const result = match(block[7], 'mov', '(w[0-9]+), w0');
        if (result && instructions.slice(n+9).some(i => match(i, 'mov', 'w0, ' + result[1]))) return true;
    }
    return false;
}

function routesInput(address, slot, flags) {
    const held = byteSlot(address, slot);
    if (!held) return false;
    const values = new Map([[flags.slice(1), 'sideFlags'], [held.slice(1), 'held']]);
    let condition = null;
    for (let n=1; n<=24; n++) {
        const i = decode(address.add(n*4));
        let m = match(i, 'mov', '([xw][0-9]+), #(' + number + ')');
        if (m) { values.set(m[1].slice(1), integer(m[2])); continue; }
        m = match(i, 'tst', '(w[0-9]+), #(' + number + ')');
        if (m) {
            condition = values.get(m[1].slice(1)) === 'held' && integer(m[2]) === 1 ? 'held' :
                values.get(m[1].slice(1)) === 'sideFlags' && integer(m[2]) === 4 ? 'side' : null;
            if (!condition) return false;
            continue;
        }
        m = match(i, 'csel', '(x[0-9]+), (x[0-9]+), (x[0-9]+), ne');
        if (m) {
            const yes = values.get(m[2].slice(1)), no = values.get(m[3].slice(1));
            if (!condition || yes === undefined || no === undefined) return false;
            values.set(m[1].slice(1), {condition, yes, no});
            continue;
        }
        m = match(i, 'add', '(x[0-9]+), (x[0-9]+), (x[0-9]+)');
        if (m) {
            const value = values.get(m[3].slice(1));
            if (value?.condition !== 'side' || value.yes?.condition !== 'held' || value.no?.condition !== 'held') return false;
            const offsets = [value.yes.yes, value.yes.no, value.no.yes, value.no.no];
            return !values.has(m[2].slice(1)) && offsets.every(v => Number.isInteger(v) && v>0) && new Set(offsets).size === 4;
        }
        if (/^(str|stur|stp)$/.test(i.mnemonic)) {
            if (i.opStr.includes('!') || /\], /.test(i.opStr)) return false;
            continue;
        }
        if (/^(ldr|ldur|ldp)$/.test(i.mnemonic)) {
            if (i.opStr.includes('!') || /\], /.test(i.opStr)) return false;
            const destinations = i.opStr.split('[')[0].match(/[xw][0-9]+/g) || [];
            if (destinations.includes(flags)) return false;
            for (const r of destinations) values.delete(r.slice(1));
            continue;
        }
        return false;
    }
    return false;
}

function reachableFrom(join) {
    const pending = [join], visited = new Set(), end = join.add(24576);
    while (pending.length) {
        const address = pending.pop(), key = address.toString();
        if (visited.has(key) || address.compare(join)<0 || address.compare(end)>=0) continue;
        const i = decode(address);
        visited.add(key);
        if (i.mnemonic === 'ret' || i.mnemonic === 'br') continue;
        if (/^(b|b\..+|cbz|cbnz|tbz|tbnz)$/.test(i.mnemonic)) {
            const destination = i.opStr.match(/#(0x[0-9a-f]+)$/);
            if (!destination) continue;
            pending.push(ptr(destination[1]));
            if (i.mnemonic === 'b') continue;
        }
        pending.push(address.add(4));
    }
    return visited;
}

function resolve(store) {
    const call = store.add(4), test = decode(call.add(4));
    const selection = match(test, 'tbz', '(w[0-9]+), #2, #(0x[0-9a-f]+)');
    const output = match(decode(store), 'strb', 'wzr, \\[sp, #(' + number + ')\\]');
    if (!selection || !output) return null;
    const flags = selection[1], slot = integer(output[1]);
    const setup = Array.from({length:4}, (_, n) => decode(store.sub(16-n*4)));
    const checks = [
        i => match(i, 'ldr', 'w1, \\[sp, #(' + number + ')\\]'),
        i => match(i, 'ldr', flags + ', \\[sp, #(' + number + ')\\]'),
        i => { const m=match(i, 'add', 'x2, sp, #(' + number + ')'); return m && integer(m[1])===slot; },
        i => match(i, 'mov', 'x0, x(?:19|2[0-8])'),
    ];
    if (flags === 'w1' || !checks.every(check => setup.filter(check).length === 1)) return null;
    const left = byteSlot(call.add(8), slot), rightBranch = ptr(selection[2]);
    if (!left) return null;
    const leftSlot = storedSlot(call.add(12), left);
    const join = branch(decode(call.add(16)), 'b');
    const rightJoin = branch(decode(rightBranch), 'tbz', flags + ', #3, ');
    const right = byteSlot(rightBranch.add(4), slot);
    const rightSlot = right && storedSlot(rightBranch.add(8), right);
    if (!join || !rightJoin || !join.equals(rightJoin) || !join.equals(rightBranch.add(12)) ||
        leftSlot === null || rightSlot === null || !right || leftSlot === rightSlot ||
        join.compare(call) <= 0 || join.sub(call).toUInt32() > 4096) return null;
    const target = branch(decode(call), 'bl');
    if (!target || !queryAbi(target)) return null;
    const reachable = reachableFrom(join);
    const routes = byteLoads.filter(hit => reachable.has(hit.address.toString()) && routesInput(hit.address, slot, flags));
    if (!routes.length) return null;
    return {call, target, routes:routes.length};
}

const seeds = scan('ff 03 00 39 00 00 00 94 00 00 10 36 e0 03 40 39 : ff 03 c0 ff 00 00 00 fc 00 00 f8 ff e0 03 c0 ff');
const byteLoads = scan('e0 03 40 39 : e0 03 c0 ff');
const candidates = [];
for (const seed of seeds) {
    try { const candidate=resolve(seed.address); if (candidate) candidates.push(candidate); }
    catch (_) { }
}
if (candidates.length !== 1) unsupported('structural controller query: expected 1 match, found ' + candidates.length +
    ' from ' + seeds.length + ' candidates');
const {call, target, routes} = candidates[0];
send({event:'controller-runtime', resolver:'structural', call:call.sub(runtime.base).toString(),
    target:target.sub(runtime.base).toString(), routes});

const caller = call.add(4);
let listener = null, timer = null, deadline = 0;
const selected = new Map(), queries = new Map();
const bump = (counts, id) => counts.set(id, (counts.get(id) || 0) + 1);
const byId = counts => Object.fromEntries([...counts].map(([id, n]) => ['0x' + id.toString(16), n]));
function stop() {
    deadline = 0;
    if (timer !== null) { clearTimeout(timer); timer = null; }
    if (listener !== null) { listener.detach(); listener = null; }
}
function renew(seconds) {
    if (!Number.isFinite(seconds) || seconds < 1 || seconds > 60)
        throw Error('Invalid controller priority lease');
    if (timer !== null) clearTimeout(timer);
    deadline = Date.now() + seconds * 1000;
    timer = setTimeout(stop, seconds * 1000);
}
rpc.exports = {
    start(seconds) {
        if (listener !== null) throw Error('Controller priority already running');
        renew(seconds);
        try {
            listener = Interceptor.attach(target, {
                onEnter(args) {
                    this.id = args[1].toUInt32();
                    this.output = args[2];
                    this.routing = this.returnAddress.equals(caller);
                },
                onLeave(result) {
                    if (!this.routing || Date.now() >= deadline || result.toInt32() !== 0) return;
                    bump(queries, this.id);
                    if (this.output.readU8() === 0) {
                        this.output.writeU8(1);
                        bump(selected, this.id);
                    }
                }
            });
        } catch (error) { stop(); throw error; }
    },
    renew, stop,
    status() { return {running: listener !== null && Date.now() < deadline, selected: byId(selected), queries: byId(queries)}; }
};
