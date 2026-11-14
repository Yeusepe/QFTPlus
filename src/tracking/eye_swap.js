'use strict';
const cleanupToken = {};
let hooks=[], executing={}, captured, engine, library, closeLibrary, replaceModel, replayModel;
let self, backend, processed, spec, stock, independent, binary, graph, changed=false, stopped=false, replaying=false;
let retain, release, dictionaryRetained=false, closing=false;
let eyeSequence=0;
let prepareHook, preparing=false, preparationThread, preparedGraph, nativeLog, logTag;
let guard, guardStart, guardBeat, guardStatus, guardClose, guardTimer;
function emit(payload) {try{send(payload);}catch (_) {}}
function bytes(p,n) { return new Uint8Array(p.readByteArray(n)); }
function outputValues(p) { return [4,6,6,1,1].flatMap((n,i)=>Array.from(new Float32Array(p.add(i*56+40).readPointer().readByteArray(n*4)))); }
function signature(p,expected) {
    const value=Array.from(bytes(p,expected.length/2)).map(v=>v.toString(16).padStart(2,'0')).join('');
    if(value!==expected)throw new Error('instruction_signature');
}
function swap(target,rollback) {
    const report=Memory.alloc(40);report.writeByteArray(new Uint8Array(40));
    const started=Date.now();
    preparing=true;preparationThread=Process.getCurrentThreadId();preparedGraph=undefined;
    let code;
    try {code=replaceModel(engine.base,backend,processed,spec,target,108564,binary,266054,report,rollback);}
    finally {preparing=false;}
    if(preparedGraph!==undefined)graph=preparedGraph;
    try {send({event:'swap',code,milliseconds:Date.now()-started,stage:report.add(32).readS32(),failedStage:report.add(36).readS32()});} catch (_) {}
    return code;
}
function replay(id) {
    replaying=true;
    let code;
    try {code=replayModel(backend,Process.getModuleByName('libhexagon.so').getExportByName('hexagon_nnExecute'),captured.domain,id,captured.inputs,captured.outputs,captured.options);}
    finally {replaying=false;}
    const values=outputValues(captured.outputs);
    if(code!==0||!values.every(Number.isFinite))throw new Error('replay_failed');
    return values;
}
function dispose() {
    if(closing)return;
    closing=true;
    if(guardTimer){clearInterval(guardTimer);guardTimer=null;}
    hooks.forEach(h=>h.detach());hooks=[];
    try {
        if(guard&&guardStatus(guard)===1)changed=false;
        if(changed) {
            const code=swap(stock,stock);
            if(code<0)throw new Error('restore_failed');
            changed=false;
            let verified=false;
            if(preparedGraph!==undefined) {
                const values=replay(graph);
                if(!values.every((v,i)=>v===captured.expected[i]))throw new Error('restore_output_mismatch');
                verified=true;
            }
            nativeLog(4,logTag,Memory.allocUtf8String(JSON.stringify({event:'restored',graph,sameFrameVerified:verified})));
        }
        if(guard){guardClose(guard);guard=null;}
        if(prepareHook){prepareHook.detach();prepareHook=null;}
        if(dictionaryRetained){release(processed);release(spec);dictionaryRetained=false;}
        if(self){release(self);self=null;}
        if(library){closeLibrary(library);library=null;}
    } finally {
        closing=false;
    }
}
// Frida runs weak callbacks on unload, including an unexpected host disconnect.
Script.bindWeak(cleanupToken,()=>{try{dispose();}catch(error){emit({event:'error',reason:String(error)});}});
function stop() {
    stopped=true;
    try {dispose();emit({event:'restored'});}
    catch(error){emit({event:'error',reason:String(error)});}
}
recv('qft-stop',stop);
function heartbeat() {
    if(!stopped) {
        if(guard&&guardBeat(guard)!==0)fatal(new Error('owner_lease_expired'));
        else emit({event:'alive'});
    }
    recv('qft-heartbeat',heartbeat);
}
recv('qft-heartbeat',heartbeat);
function fatal(error) {if(!stopped){emit({event:'error',reason:String(error)});stop();}}
async function initialize() {
    try {
        engine=Process.getModuleByName('libtrackingengines.so');
        nativeLog=new NativeFunction(Process.getModuleByName('liblog.so').getExportByName('__android_log_write'),'int',['int','pointer','pointer']);
        logTag=Memory.allocUtf8String('QFTEyes');
        signature(engine.base.add(0x24aeb30),'ff4304d1fd7b0ba9fc6f0ca9fa670da9');
        signature(engine.base.add(0x24b84a0),'ff4303d1fd7b07a9fc6f08a9fa6709a9');
        retain=new NativeFunction(engine.base.add(0x769750),'uint64',['uint64','pointer']);
        release=new NativeFunction(engine.base.add(0xc271a0),'void',['pointer']);
        const dlopen=new NativeFunction(Module.getGlobalExportByName('dlopen'),'pointer',['pointer','int']);
        closeLibrary=new NativeFunction(Module.getGlobalExportByName('dlclose'),'int',['pointer']);
        library=dlopen(Memory.allocUtf8String(qftEyeConfig.library),2);
        if(library.isNull())throw new Error('library_load');
        const module=Process.getModuleByName(qftEyeConfig.library.split('/').pop());
        replaceModel=new NativeFunction(module.getExportByName('qft_eye_replace'),'int',
            ['pointer','pointer','pointer','pointer','pointer','ulong','pointer','ulong','pointer','pointer']);
        replayModel=new NativeFunction(module.getExportByName('qft_eye_replay'),'int',
            ['pointer','pointer','pointer','uint','pointer','pointer','pointer']);
        guardStart=new NativeFunction(module.getExportByName('qft_eye_guard_start'),'pointer',
            ['pointer','pointer','pointer','pointer','pointer','ulong','pointer','ulong','int','pointer']);
        guardBeat=new NativeFunction(module.getExportByName('qft_eye_guard_beat'),'int',['pointer']);
        guardStatus=new NativeFunction(module.getExportByName('qft_eye_guard_status'),'int',['pointer']);
        guardClose=new NativeFunction(module.getExportByName('qft_eye_guard_close'),'void',['pointer']);
        const edge='"id": 52, "name": "211_reshape", "op": "OP_Reshape", "padding": "NN_PAD_NA", "input": [[50, 0], [51, 0]]';
        if(qftEyeConfig.json.length!==108564||qftEyeConfig.json.split(edge).length!==2)throw new Error('graph_contract');
        stock=Memory.allocUtf8String(qftEyeConfig.json);
        independent=Memory.allocUtf8String(qftEyeConfig.json.replace(edge,edge.replace('[[50,','[[18,')));
        const weights=Uint8Array.from(qftEyeConfig.binary.match(/../g).map(v=>parseInt(v,16)));
        if(weights.length!==266054)throw new Error('weights_contract');
        binary=Memory.alloc(weights.length+1);binary.writeByteArray(weights);binary.add(weights.length).writeU8(0);
        prepareHook=Interceptor.attach(Process.getModuleByName('libhexagon.so').getExportByName('hexagon_nnPrepare'),{
            onEnter(args){this.ours=preparing&&Process.getCurrentThreadId()===preparationThread;this.graph=args[1].toUInt32();},
            onLeave(result){if(this.ours&&result.toInt32()===0)preparedGraph=this.graph;}
        });
        hooks.push(Interceptor.attach(engine.base.add(0x24b9280),{
            onEnter(args){this.thread=Process.getCurrentThreadId();executing[this.thread]=args[1].add(8).readPointer().sub(48).readPointer();},
            onLeave(){delete executing[this.thread];}
        }));
        hooks.push(Interceptor.attach(Process.getModuleByName('libhexagon.so').getExportByName('hexagon_nnExecute'),{
            onEnter(args) {
                if(replaying||args[3].toInt32()!==1||args[5].toInt32()!==5)return;
                if(args[2].readU32()!==1||args[2].add(4).readU32()!==2||args[2].add(8).readU32()!==256||args[2].add(12).readU32()!==256)return;
                const current=executing[Process.getCurrentThreadId()];
                if(!current)return;
                if(self&&!self.equals(current)){setImmediate(()=>fatal(new Error('eye_instance_changed')));return;}
                graph=args[1].toUInt32();
                eyeSequence++;
                if(captured)return;
                self=current;retain(1,self.add(8));
                backend=self.add(0x50).readPointer().readPointer();
                if(!backend.readPointer().equals(engine.base.add(0x2cd6838)))throw new Error('backend_type');
                const binarySize=backend.add(0x80).readU64().toNumber();
                if(binarySize<266054||binarySize>270150||
                    Checksum.compute('sha256',backend.add(0x88).readPointer().readByteArray(266054))!==Checksum.compute('sha256',weights.buffer))throw new Error('active_weights_contract');
                const inputs=Memory.alloc(56),outputs=Memory.alloc(280),options=Memory.alloc(16);
                inputs.writeByteArray(bytes(args[2],56));outputs.writeByteArray(bytes(args[4],280));options.writeByteArray(bytes(args[6],16));
                const length=inputs.add(48).readU32();
                if(length<131072||length>132096)throw new Error('input_contract');
                const inputData=Memory.alloc(length);inputData.writeByteArray(bytes(inputs.add(40).readPointer(),length));inputs.add(40).writePointer(inputData);
                const outputData=[];
                for(let i=0;i<5;i++) {
                    const size=outputs.add(i*56+48).readU32();
                    if(size<4||size>4096)throw new Error('output_contract');
                    const data=Memory.alloc(size);data.writeByteArray(new Uint8Array(size));outputs.add(i*56+40).writePointer(data);outputData.push(data);
                }
                captured={domain:args[0],inputs,outputs,options,inputData,outputData};this.original=args[4];
            },
            onLeave(){if(this.original)captured.expected=outputValues(this.original);}
        }));
        const deadline=Date.now()+10000;
        while(!captured?.expected&&!stopped&&Date.now()<deadline)await new Promise(resolve=>setTimeout(resolve,25));
        if(stopped)return;
        if(!captured?.expected)throw new Error('eye_frames_missing');
        const anchor=backend.and(ptr('0x00ffffffffffffff'));
        const ranges=Process.enumerateRanges('rw-').filter(r=>!r.file&&r.size<=32*1024*1024)
            .sort((a,b)=>Math.abs(parseInt(a.base.toString(),16)-parseInt(anchor.toString(),16))-Math.abs(parseInt(b.base.toString(),16)-parseInt(anchor.toString(),16)));
        let total=0;
        const matches=Memory.findPointers(ranges.filter(r=>{total+=r.size;return total<=256*1024*1024;}),[self]);
        const slots=matches.filter(m=>{
            const p=m.address;
            return p.sub(24).readU32()===15&&p.sub(8).readU32()===15&&p.add(8).readU32()===20&&p.add(24).readU32()===15&&p.add(40).readU32()===20;
        });
        if(slots.length!==1)throw new Error('module_identity');
        processed=slots[0].address.sub(32).readPointer();spec=slots[0].address.sub(16).readPointer();
        if(processed.add(40).readU64().toNumber()!==0||spec.add(40).readU64().toNumber()!==1||!processed.readPointer().equals(spec.readPointer()))throw new Error('module_contract');
        retain(1,processed.add(8));retain(1,spec.add(8));dictionaryRetained=true;
        const before=replay(graph);
        if(!before.every((v,i)=>v===captured.expected[i]))throw new Error('baseline_mismatch');
        guard=guardStart(engine.base,self,processed,spec,stock,108564,binary,266054,qftEyeConfig.owner,Memory.allocUtf8String(qftEyeConfig.status));
        if(guard.isNull()){guard=null;throw new Error('restoration_guard_missing');}
        guardTimer=setInterval(()=>{if(guard&&!stopped&&guardStatus(guard)!==0)fatal(new Error('owner_lease_expired'));},250);
        changed=true;
        const sequenceBefore=eyeSequence;
        const code=swap(independent,stock);
        if(code!==0){if(code===1)changed=false;throw new Error('activation_failed');}
        const resumed=Date.now()+2000;
        while(eyeSequence<=sequenceBefore&&!stopped&&Date.now()<resumed)await new Promise(resolve=>setTimeout(resolve,25));
        if(stopped)return;
        if(eyeSequence<=sequenceBefore)throw new Error('frames_did_not_resume');
        replay(graph);
        send({event:'ready',pid:Process.id,graph});
    } catch(error){fatal(error);}
}
setImmediate(initialize);
