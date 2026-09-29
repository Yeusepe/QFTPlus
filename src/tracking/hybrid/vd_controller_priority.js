if (Process.arch !== 'arm64') throw Error('Controller priority requires ARM64');
const runtime = Process.getModuleByName('libvrapiimpl.so');
const code = runtime.enumerateRanges('r-x');
function unsupported() { throw Error('Unsupported controller routing code; no hook applied'); }
function find(pattern) {
    const hits = code.flatMap(range => Memory.scanSync(range.base, range.size, pattern));
    if (hits.length !== 1) unsupported();
    return hits[0].address;
}
const call = find('00 00 00 94 5b 16 10 36 e8 43 46 39 : 00 00 00 fc ff ff ff ff ff ff ff ff');
find('00 80 4b 39 c0 03 5f d6');
find('e8 43 46 39 09 3f 80 52 0a a7 80 52 e1 03 59 ad 13 02 80 52 1f 01 00 72 08 73 80 52 28 11 88 9a');
const distance = ((call.readU32() << 6) >> 6) * 4;
const target = distance < 0 ? call.sub(-distance) : call.add(distance);
[0xa9bd7bfd, 0xa90157f6, 0xa9024ff4, 0x910003fd].forEach((word, i) => {
    if (target.add(4 * i).readU32() !== word) unsupported();
});

const caller = call.add(4);
let listener = null, timer = null, deadline = 0;
const selected = [0, 0], queries = [0, 0];
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
                    this.side = args[1].toUInt32() - 0x20000002;
                    this.output = args[2];
                    this.routing = this.returnAddress.equals(caller);
                },
                onLeave(result) {
                    if (!this.routing || this.side < 0 || this.side > 1 ||
                        Date.now() >= deadline || result.toInt32() !== 0) return;
                    queries[this.side]++;
                    if (this.output.readU8() === 0) {
                        this.output.writeU8(1);
                        selected[this.side]++;
                    }
                }
            });
        } catch (error) { stop(); throw error; }
    },
    renew, stop,
    status() { return {running: listener !== null && Date.now() < deadline, selected, queries}; }
};
