if (Process.arch !== 'arm64') throw Error('Controller priority requires ARM64');
const runtime = Process.getModuleByName('libvrapiimpl.so');
const signatures = [
    [0x4fa284, [0xa9bd7bfd, 0xa90157f6, 0xa9024ff4, 0x910003fd]],
    [0x7c5a2c, [0x97f4d216, 0x3610165b, 0x394643e8]],
    [0x4f6260, [0x394b8000, 0xd65f03c0]],
    [0x7c84b4, [0x394643e8, 0x52803f09, 0x5280a70a, 0xad5903e1,
        0x52800213, 0x7200011f, 0x52807308, 0x9a881128]],
];
for (const [offset, words] of signatures)
    for (let i = 0; i < words.length; i++)
        if (runtime.base.add(offset + 4 * i).readU32() !== words[i])
            throw Error('Unsupported controller routing code; no hook applied');

const caller = runtime.base.add(0x7c5a30);
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
            listener = Interceptor.attach(runtime.base.add(0x4fa284), {
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
