'use strict';
const cleanupToken = {};
const profile = qftEyeConfig.profile;
let hooks=[], graphHooks=[], nativeHooks, executing={}, captured, engine, library, closeLibrary, setModel, replayModel;
let self, backend, contract, weightsBefore, graph, changed=false, stopped=false, replaying=false;
let retain, release, closing=false;
let eyeSequence=0;
let preparing=false, preparationThread;
let guard, guardStart, guardBeat, guardStatus, guardClose, guardTimer;
function emit(payload) {try{send(payload);}catch (_) {}}
function bytes(p,n) { return new Uint8Array(p.readByteArray(n)); }
function outputValues(p) { return [4,6,6,1,1].flatMap((n,i)=>Array.from(new Float32Array(p.add(i*56+40).readPointer().readByteArray(n*4)))); }
function modelContract(model) {
    const fail=()=>{throw new Error('model_contract');};
    const text=p=>{
        const long=p.readU8()&1,n=long?p.add(8).readU64().toNumber():p.readU8()>>1;
        if(!Number.isSafeInteger(n)||n>128)fail();
        return (long?p.add(16).readPointer():p.add(1)).readUtf8String(n);
    };
    const vector=(p,stride,maximum)=>{
        const first=p.readPointer(),last=p.add(8).readPointer(),capacity=p.add(16).readPointer();
        const length=last.sub(first).toUInt32();
        if(last.compare(first)<0||capacity.compare(last)<0||last.compare(first.add(stride*maximum))>0||length%stride)fail();
        return Array.from({length:length/stride},(_,i)=>first.add(i*stride));
    };
    if(!model.readPointer().equals(engine.base.add(profile.modelVtable))||model.add(0x1f0).readU8()!==1)fail();
    const nodes=vector(model.add(0xd8),104,4096).map(p=>({
        id:p.readU32(),op:text(p.add(8)),padding:text(p.add(32)),edge:p.add(56).readPointer(),
        input:vector(p.add(56),8,64).map(q=>[q.readU32(),q.add(4).readU32()]),
        output:vector(p.add(80),40,64).map(q=>{
            const rank=q.readU32();if(rank<1||rank>8)fail();
            const shape=Array.from({length:rank},(_,i)=>q.add(4+i*4).readU32()),size=q.add(36).readU32();
            const elements=shape.reduce((n,v)=>n*v,1);
            if(!Number.isSafeInteger(elements)||elements<=0||size<=0||size%elements)fail();
            return {shape,elem_size:size/elements};
        })
    }));
    const byId=new Map(nodes.map(n=>[n.id,n]));
    if(byId.size!==nodes.length)fail();
    const shape=(n,dimensions)=>n?.output.length===1&&n.output[0].elem_size===4&&JSON.stringify(n.output[0].shape)===JSON.stringify(dimensions);
    const node=(edge,op,count,dimensions)=>{
        if(!edge||edge[1]!==0)fail();
        const n=byId.get(edge[0]);
        if(!n||n.op!==op||n.padding!=='NN_PAD_NA'||n.input.length!==count||(dimensions&&!shape(n,dimensions)))fail();
        return n;
    };
    const output=nodes.filter(n=>n.op==='OUTPUT');
    if(output.length!==1||output[0].input.length!==5)fail();
    const shapes=[[1,4],[1,6],[1,6],[1,1],[1,1]];
    output[0].input.forEach((edge,i)=>{if(edge[1]!==0||!shape(byId.get(edge[0]),shapes[i]))fail();});
    const reshape=node(output[0].input[0],'Reshape',2,[1,4]);
    const blend=node(reshape.input[0],'Add_f',2,[2,2]);
    const local=node(blend.input[0],'FC_f',3,[2,2]);
    const mul=node(blend.input[1],'Mul_f',2,[2,2]);
    const sigmoid=node(mul.input[0],'Sigmoid_f',1,[2,2]);
    const difference=node(mul.input[1],'Sub_f',2,[2,2]);
    if(difference.input[1][0]!==local.id||difference.input[1][1]!==0)fail();
    const other=node(difference.input[0],'FC_f',3,[2,2]);
    const gate=node(sigmoid.input[0],'FC_f',3,[2,2]);
    const features=node(local.input[0],'Caffe2_Dequantize',3,null);
    const featureShape=features.output[0]?.shape;
    if(!shape(features,featureShape)||featureShape.length!==2||featureShape[0]!==2||featureShape[1]<1||featureShape[1]>4096)fail();
    const flipped=node(other.input[0],'Concat_f',3,featureShape);
    const joined=node(gate.input[0],'Concat_f',3,[2,featureShape[1]*2]);
    if(joined.input[1][0]!==features.id||joined.input[2][0]!==flipped.id||joined.input[1][1]||joined.input[2][1])fail();
    const slices=flipped.input.slice(1).map(edge=>node(edge,'Caffe2_Dequantize',3,[1,featureShape[1]]))
        .map(n=>node(n.input[0],'QuantizedSlice_8',6,null));
    if(slices[0].id===slices[1].id||slices.some(n=>n.input[0][0]!==features.input[0][0]||n.input[0][1]!==0))fail();
    if(new Set([reshape.id,blend.id,local.id,mul.id,sigmoid.id,difference.id,other.id,gate.id,features.id,flipped.id,joined.id]).size!==11)fail();
    return {model,edge:reshape.edge,original:blend.id,independent:local.id,nodes};
}

function weightsDigest() {
    const length=backend.add(0x80).readU64().toNumber(),data=backend.add(0x88).readPointer();
    if(!Number.isSafeInteger(length)||length<1||length>16*1024*1024||!contract.model.add(0x180).readPointer().equals(data))throw Error('model_contract');
    return Checksum.compute('sha256',data.readByteArray(length));
}
function swap(enabled) {
    const started=Date.now();
    preparing=true;preparationThread=Process.getCurrentThreadId();
    let code;
    try {code=setModel(guard,enabled?1:0);}
    finally {preparing=false;}
    if(code>=0)graph=contract.model.add(0x1c0).readU32();
    emit({event:'swap',code,milliseconds:Date.now()-started});
    if(code>=0&&weightsDigest()!==weightsBefore)throw Error('live_weights_changed');
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
        if(changed) {
            const code=swap(false);
            if(code<0)throw new Error('restore_failed');
            graph=contract.model.add(0x1c0).readU32();
            const values=replay(graph);
            if(!values.every((v,i)=>v===captured.expected[i]))throw new Error('restore_output_mismatch');
            changed=false;
            emit({event:'restoration_verified',graph,sameFrameVerified:true});
        }
        if(guard){if(guardClose(guard)!==0)throw Error('restore_failed');guard=null;}
        graphHooks.forEach(h=>h.detach());graphHooks=[];
        if(nativeHooks){nativeHooks.dispose();nativeHooks=null;}
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
function finishFailure(error) {
    try {dispose();}
    catch(cleanup){emit({event:'error',reason:String(cleanup),cause:String(error),safe:false});return;}
    emit({event:'error',reason:String(error),safe:true});emit({event:'restored'});
}
function fatal(error) {if(!stopped){stopped=true;finishFailure(error);}}
async function initialize() {
    try {
        engine=Process.getModuleByName('libtrackingengines.so');
        if(!profile||!Array.isArray(profile.code)||profile.code.length!==14||!Array.isArray(qftEyeConfig.bindings)||qftEyeConfig.bindings.length!==11)throw new Error('engine_profile');
        for(const binding of qftEyeConfig.bindings) {
            if(!Number.isSafeInteger(binding.offset)||binding.offset<=0||!Number.isSafeInteger(binding.length)||binding.length<=0||binding.offset+binding.length>engine.size||
                Checksum.compute('sha256',engine.base.add(binding.offset).readByteArray(binding.length))!==binding.sha256)throw new Error('loaded_code_changed');
        }
        retain=new NativeFunction(engine.base.add(profile.functions.retain),'uint64',['uint64','pointer']);
        release=new NativeFunction(engine.base.add(profile.functions.release),'void',['pointer']);
        const dlopen=new NativeFunction(Module.getGlobalExportByName('dlopen'),'pointer',['pointer','int']);
        closeLibrary=new NativeFunction(Module.getGlobalExportByName('dlclose'),'int',['pointer']);
        library=dlopen(Memory.allocUtf8String(qftEyeConfig.library),2);
        if(library.isNull())throw new Error('library_load');
        const dlsym=new NativeFunction(Module.getGlobalExportByName('dlsym'),'pointer',['pointer','pointer']);
        const exported=name=>{
            const address=dlsym(library,Memory.allocUtf8String(name));
            if(address.isNull())throw new Error('native_export');
            return address;
        };
        const configure=new NativeFunction(exported('qft_eye_configure'),'int',['pointer','ulong','pointer','ulong']);
        const values=[...profile.code,profile.backendVtable,profile.filterVtable,...profile.trace,profile.axis,profile.modelVtable];
        const offsets=Memory.alloc(values.length*8);
        values.forEach((value,i)=>offsets.add(i*8).writeU64(value));
        if(configure(engine.base,engine.size,offsets,values.length*8)!==1)throw new Error('native_profile');
        setModel=new NativeFunction(exported('qft_eye_set'),'int',['pointer','int']);
        replayModel=new NativeFunction(exported('qft_eye_replay'),'int',['pointer','pointer','pointer','uint','pointer','pointer','pointer']);
        guardStart=new NativeFunction(exported('qft_eye_guard_start'),'pointer',['pointer','pointer','pointer','uint','uint','int','pointer']);
        guardBeat=new NativeFunction(exported('qft_eye_guard_beat'),'int',['pointer']);
        guardStatus=new NativeFunction(exported('qft_eye_guard_status'),'int',['pointer']);
        guardClose=new NativeFunction(exported('qft_eye_guard_close'),'int',['pointer']);
        nativeHooks=new CModule(`
            #include <gum/guminterceptor.h>
            extern int qft_eye_task_options(const void*,void*);
            extern void qft_eye_graph_event(int,unsigned int,int);
            void task_enter(GumInvocationContext* context) {
                void* original=gum_invocation_context_get_nth_argument(context,6);
                void* copy=gum_invocation_context_get_listener_invocation_data(context,32);
                if(qft_eye_task_options(original,copy))gum_invocation_context_replace_nth_argument(context,6,copy);
            }
            void graph_enter(GumInvocationContext* context) {
                gpointer* saved=gum_invocation_context_get_listener_invocation_data(context,sizeof(gpointer));
                *saved=gum_invocation_context_get_nth_argument(context,1);
            }
            void graph_leave(GumInvocationContext* context) {
                gpointer* saved=gum_invocation_context_get_listener_invocation_data(context,sizeof(gpointer));
                int event=(int)(gsize)gum_invocation_context_get_listener_function_data(context);
                int result=(int)(gsize)gum_invocation_context_get_return_value(context);
                unsigned int graph=event==0&&!result?*(unsigned int*)*saved:(unsigned int)(gsize)*saved;
                qft_eye_graph_event(event,graph,result);
            }
        `,{qft_eye_task_options:exported('qft_eye_task_options'),qft_eye_graph_event:exported('qft_eye_graph_event')});
        graphHooks.push(Interceptor.attach(engine.base.add(profile.functions.task_dispatch),{onEnter:nativeHooks.task_enter}));
        ['hexagon_nnInit','hexagon_nnDestroy','hexagon_nnPrepare'].forEach((name,event)=>{
            graphHooks.push(Interceptor.attach(Process.getModuleByName('libhexagon.so').getExportByName(name),{
                onEnter:nativeHooks.graph_enter,onLeave:nativeHooks.graph_leave
            },ptr(event)));
        });
        hooks.push(Interceptor.attach(engine.base.add(profile.functions.execute),{
            onEnter(args){this.thread=Process.getCurrentThreadId();executing[this.thread]=args[1].add(8).readPointer().sub(48).readPointer();},
            onLeave(){delete executing[this.thread];}
        }));
        hooks.push(Interceptor.attach(Process.getModuleByName('libhexagon.so').getExportByName('hexagon_nnExecute'),{
            onEnter(args) {
                if(stopped||replaying||args[3].toInt32()!==1||args[5].toInt32()!==5)return;
                if(args[2].readU32()!==1||args[2].add(4).readU32()!==2||args[2].add(8).readU32()!==256||args[2].add(12).readU32()!==256)return;
                const current=executing[Process.getCurrentThreadId()];
                if(!current)return;
                if(self&&!self.equals(current)){setImmediate(()=>fatal(new Error('eye_instance_changed')));return;}
                graph=args[1].toUInt32();
                eyeSequence++;
                if(captured)return;
                try {
                self=current;retain(1,self.add(8));
                backend=self.add(0x50).readPointer().readPointer();
                if(!backend.readPointer().equals(engine.base.add(profile.backendVtable)))throw new Error('backend_type');
                contract=modelContract(backend.add(16).readPointer());
                weightsBefore=weightsDigest();
                const inputs=Memory.alloc(56),outputs=Memory.alloc(280),options=Memory.alloc(16);
                inputs.writeByteArray(bytes(args[2],56));outputs.writeByteArray(bytes(args[4],280));options.writeByteArray(bytes(args[6],16));
                const length=inputs.add(48).readU32();
                if(length<131072||length>132096)throw new Error('input_contract');
                const inputData=Memory.alloc(length);inputData.writeByteArray(bytes(inputs.add(40).readPointer(),length));inputs.add(40).writePointer(inputData);
                const outputData=[];
                for(let i=0;i<5;i++) {
                    const size=outputs.add(i*56+48).readU32();
                    if(size<[4,6,6,1,1][i]*4||size>4096)throw new Error('output_contract');
                    const data=Memory.alloc(size);data.writeByteArray(new Uint8Array(size));outputs.add(i*56+40).writePointer(data);outputData.push(data);
                }
                captured={domain:args[0],inputs,outputs,options,inputData,outputData};this.original=args[4];
                } catch(error){stopped=true;setImmediate(()=>finishFailure(error));}
            },
            onLeave(){if(this.original)captured.expected=outputValues(this.original);}
        }));
        const deadline=Date.now()+10000;
        while(!captured?.expected&&!stopped&&Date.now()<deadline)await new Promise(resolve=>setTimeout(resolve,25));
        if(stopped)return;
        if(!captured?.expected)throw new Error('eye_frames_missing');
        const before=replay(graph);
        if(!before.every((v,i)=>v===captured.expected[i]))throw new Error('baseline_mismatch');
        guard=guardStart(self,contract.model,contract.edge,contract.original,contract.independent,qftEyeConfig.owner,Memory.allocUtf8String(qftEyeConfig.status));
        if(guard.isNull()){guard=null;throw new Error('restoration_guard_missing');}
        guardTimer=setInterval(()=>{if(guard&&!stopped&&guardStatus(guard)!==0)fatal(new Error('owner_lease_expired'));},250);
        changed=true;
        const sequenceBefore=eyeSequence;
        const code=swap(true);
        if(code!==0)throw new Error('activation_failed');
        const resumed=Date.now()+2000;
        while(eyeSequence<=sequenceBefore&&!stopped&&Date.now()<resumed)await new Promise(resolve=>setTimeout(resolve,25));
        if(stopped)return;
        if(eyeSequence<=sequenceBefore)throw new Error('frames_did_not_resume');
        const after=replay(graph);
        if(!after.slice(4).every((v,i)=>v===captured.expected[i+4]))throw Error('unchanged_outputs_mismatch');
        send({event:'ready',pid:Process.id,graph});
    } catch(error){fatal(error);}
}
setImmediate(initialize);
