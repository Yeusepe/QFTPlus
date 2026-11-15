'use strict';
const lifetime = {};
const profile = qftEyeConfig.profile;
const filters = new Map();
let hooks = [], desired = qftEyeConfig.samples, stopped = false, engine;
let heartbeat = Date.now(), lease;
let restoreFailed = false;
function checkSamples(value) {
    if (!Number.isInteger(value) || value < 1 || value > 25) throw new Error('invalid_samples');
    return value;
}
function matches(p) { return p.readPointer().equals(engine.base.add(profile.filterVtable)); }
function restore(p, state) {
    if (!matches(p)) return;
    if (p.add(0x48).readU32() === state.applied) p.add(0x48).writeU32(state.fallback);
    if (p.add(0x4c).readU32() === state.applied) p.add(0x4c).writeU32(state.support);
}
function dispose() {
    if (stopped) { if (restoreFailed) throw new Error('restore_failed'); return; }
    stopped = true;
    if (lease !== undefined) { clearInterval(lease); lease = undefined; }
    let failed = false;
    for (const [address, state] of filters) {
        try { restore(ptr(address), state); } catch (_) { failed = true; }
    }
    filters.clear();
    hooks.forEach(h => h.detach()); hooks = [];
    restoreFailed = failed;
    if (failed) throw new Error('restore_failed');
}
function stop() {
    try { dispose(); send({event:'restored'}); }
    catch (error) { send({event:'error', reason:String(error)}); }
}
Script.bindWeak(lifetime, () => { try { dispose(); } catch (_) {} });
recv('qft-stop', stop);
function renew() {
    recv('qft-heartbeat', () => { heartbeat = Date.now(); if (!stopped) { send({event:'alive'}); renew(); } });
}
function control() {
    recv('qft-averaging', message => {
        try { desired = checkSamples(message.payload); }
        catch (error) { send({event:'error', reason:String(error)}); stop(); return; }
        if (!stopped) control();
    });
}
setImmediate(() => {
    try {
        checkSamples(desired);
        engine = Process.getModuleByName('libtrackingengines.so');
        if(!profile||!Array.isArray(qftEyeConfig.bindings)||qftEyeConfig.bindings.length!==3)throw new Error('engine_profile');
        for(const binding of qftEyeConfig.bindings) {
            if(!Number.isSafeInteger(binding.offset)||binding.offset<=0||!Number.isSafeInteger(binding.length)||binding.length<=0||binding.offset+binding.length>engine.size||
                Checksum.compute('sha256',engine.base.add(binding.offset).readByteArray(binding.length))!==binding.sha256)throw new Error('loaded_code_changed');
        }
        const address = engine.base.add(profile.functions.filter_process);
        const signature = Array.from(new Uint8Array(address.readByteArray(16))).map(v=>v.toString(16).padStart(2,'0')).join('');
        if (signature !== 'fd7bbda9f65701a9f44f02a9fd030091') throw new Error('instruction_signature');
        hooks.push(Interceptor.attach(address, {
            onEnter(args) {
                if (stopped) return;
                try {
                    const p = args[0];
                    if (!matches(p)) throw new Error('filter_identity');
                    const key = p.toString();
                    let state = filters.get(key);
                    if (!state) {
                        state = {fallback:checkSamples(p.add(0x48).readU32()), support:checkSamples(p.add(0x4c).readU32()), applied:undefined};
                        filters.set(key, state);
                    }
                    const fallback = p.add(0x48).readU32(), support = p.add(0x4c).readU32();
                    if (state.applied !== undefined && (fallback !== state.applied || support !== state.applied)) throw new Error('filter_changed');
                    if (state.applied !== desired) {
                        state.applied = desired;
                        p.add(0x48).writeU32(desired); p.add(0x4c).writeU32(desired);
                        send({event:'applied', samples:desired});
                    }
                } catch (error) { send({event:'error',reason:String(error)}); stop(); }
            }
        }));
        hooks.push(Interceptor.attach(engine.base.add(profile.functions.filter_destroy), {
            onEnter(args) {
                const key = args[0].toString(), state = filters.get(key);
                if (state) {
                    try { restore(args[0], state); }
                    finally { filters.delete(key); }
                }
            }
        }));
        control();
        renew();
        lease = setInterval(() => { if (Date.now() - heartbeat > 5000) stop(); }, 500);
        send({event:'ready', samples:desired});
    } catch (error) { send({event:'error',reason:String(error)}); stop(); }
});
