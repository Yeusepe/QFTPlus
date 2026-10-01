const mono = Process.getModuleByName('libmonosgen-2.0.so');
function api(name, ret, args) {
    return new NativeFunction(mono.getExportByName(name), ret, args);
}
const domain = api('mono_get_root_domain', 'pointer', [])();
const threadAttach = api('mono_thread_attach', 'pointer', ['pointer']);
const threadDetach = api('mono_thread_detach', 'void', ['pointer']);
function managed(work) {
    const thread = threadAttach(domain);
    try { return work(); } finally { threadDetach(thread); }
}
const nameNew = api('mono_assembly_name_new', 'pointer', ['pointer']);
const nameFree = api('mono_assembly_name_free', 'void', ['pointer']);
const assemblyLoaded = api('mono_assembly_loaded', 'pointer', ['pointer']);
const getImage = api('mono_assembly_get_image', 'pointer', ['pointer']);
const getClass = api('mono_class_from_name', 'pointer', ['pointer', 'pointer', 'pointer']);
const getMethod = api('mono_class_get_method_from_name', 'pointer', ['pointer', 'pointer', 'int']);
const compile = api('mono_compile_method', 'pointer', ['pointer']);
const getField = api('mono_class_get_field_from_name', 'pointer', ['pointer', 'pointer']);
const fieldOffset = api('mono_field_get_offset', 'int', ['pointer']);
const utf8 = text => Memory.allocUtf8String(text);
function loadedClass(assemblyName, namespace, name) {
    const an = nameNew(utf8(assemblyName));
    const assembly = assemblyLoaded(an);
    nameFree(an);
    if (assembly.isNull()) throw new Error(assemblyName + ' is not loaded');
    const klass = getClass(getImage(assembly), utf8(namespace), utf8(name));
    if (klass.isNull()) throw new Error('Missing class ' + name);
    return klass;
}
const {klass, addresses, multiOffset, sharedClass, shared, sharedMultiOffset, convert} = managed(() => {
    const klass = loadedClass('Xenko.VR', 'Xenko.VR', 'OpenXRHMD');
    if (klass.isNull()) throw new Error('OpenXRHMD metadata missing');
    function method(name, argc) {
        const metadata = getMethod(klass, utf8(name), argc);
        if (metadata.isNull()) throw new Error('Missing method ' + name);
        const address = compile(metadata);
        if (address.isNull()) throw new Error('No code for ' + name);
        return address;
    }
    const addresses = {
        update: method('Update', 1),
        getHandState: method('GetHandState', 1),
        convertFingerState: method('ConvertFingerState', 2),
        setMultiModal: method('set_UseMultiModalInput', 1),
    };
    const multiField = getField(klass, utf8('_useMultiModalInput'));
    if (multiField.isNull()) throw new Error('Multimodal field missing');
    const multiOffset = fieldOffset(multiField);
    const sharedClass = loadedClass('VirtualDesktop.Mobile.Shared', 'VirtualDesktop.Mobile', 'SharedUserSettings');
    const parent = api('mono_class_get_parent', 'pointer', ['pointer']);
    const className = api('mono_class_get_name', 'pointer', ['pointer']);
    let baseClass = sharedClass;
    while (!baseClass.isNull() && className(baseClass).readUtf8String() !== 'SettingsBase`1') baseClass=parent(baseClass);
    if (baseClass.isNull()) throw new Error('SettingsBase not found');
    const defaultField = getField(baseClass, utf8('<Default>k__BackingField'));
    if (defaultField.isNull()) throw new Error('Settings singleton field missing');
    const vtable = api('mono_class_vtable', 'pointer', ['pointer','pointer'])(domain, baseClass);
    if (vtable.isNull()) throw new Error('Settings vtable null');
    const out = Memory.alloc(Process.pointerSize);
    api('mono_field_static_get_value', 'void', ['pointer','pointer','pointer'])(vtable,defaultField,out);
    const shared = out.readPointer();
    if (shared.isNull()) throw new Error('Settings singleton is null');
    const sharedMulti = getField(sharedClass, utf8('_useMultiModal'));
    if (sharedMulti.isNull()) throw new Error('Shared multimodal field missing');
    const sharedMultiOffset = fieldOffset(sharedMulti);
    send({event:'settings', object:shared.toString(), offset:sharedMultiOffset, value:shared.add(sharedMultiOffset).readU8()});
    send({event: 'resolved', multiOffset, addresses: Object.fromEntries(
        Object.entries(addresses).map(([k,v]) => [k, {address:v.toString(),
            module:Process.findModuleByAddress(v)?.name, code:hexdump(v,{length:24,header:false,ansi:false})}]))});

    const convert = new NativeFunction(addresses.convertFingerState, 'int', ['pointer','int','pointer']);
    return {klass, addresses, multiOffset, sharedClass, shared, sharedMultiOffset, convert};
});
const gcPin = api('mono_gchandle_new', 'uint', ['pointer','int']);
const gcFree = api('mono_gchandle_free', 'void', ['uint']);
let listener = null, handListener = null, lastSend = 0, frames = 0;
let state = 'idle', originalShared = null, originalHmd = null, sharedPin = null;
let hmdObject=null, hmdPin=null, deadline=null, restoreFallback=null;
let reason=null, cleanupError=null, restoring=false;
const invoke=api('mono_runtime_invoke','pointer',['pointer','pointer','pointer','pointer']);
function invokeBool(klass, name, object, value) {
    const method=getMethod(klass,utf8(name),1);
    if(method.isNull())throw new Error('Missing method '+name);
    const argument=Memory.alloc(4);argument.writeS32(value);
    const argv=Memory.alloc(Process.pointerSize);argv.writePointer(argument);
    const exception=Memory.alloc(Process.pointerSize);exception.writePointer(ptr(0));
    invoke(method,object,argv,exception);
    if(!exception.readPointer().isNull())throw new Error('Managed restore failed: '+name);
}
function requestRestore() {
    if(state==='idle' || state==='stopped' || state==='restoring')return;
    state='restoring';
    cleanupError=null;
    if(deadline!==null){clearTimeout(deadline);deadline=null;}
    restoreFallback=setTimeout(()=>{
        if(state!=='restoring')return;
        try { managed(()=>restore(true)); }
        catch(error) { cleanupError=String(error); }
        if(state!=='stopped') {
            state='restore-failed';
            send({event:'restore-failed',cleanupError});
        }
    },2000);
}
function restore(fallback=false) {
    if(restoring)return;
    restoring=true;
    try {
        const failures=[];
        for(const [type,name,object,value,offset] of [
            [klass,'set_UseMultiModalInput',hmdObject,originalHmd,multiOffset],
            [sharedClass,'set_UseMultiModal',shared,originalShared,sharedMultiOffset]]) {
            if(value===null)continue;
            try {
                if(fallback && object===hmdObject)object.add(offset).writeU8(value);
                else invokeBool(type,name,object,value);
                if(object.add(offset).readU8()!==value)throw Error('Restore verification failed: '+name);
            } catch(error) { failures.push(String(error)); }
        }
        cleanupError=failures.length ? failures.join('; ') : null;
        if(cleanupError===null)finish();
    } catch(error) { cleanupError=String(error); }
    finally { restoring=false; }
}
function renew(seconds) {
    if(!Number.isFinite(seconds) || seconds<1 || seconds>60 ||
        (state!=='starting' && state!=='running'))throw new Error('Invalid lease');
    if(deadline!==null)clearTimeout(deadline);
    deadline=setTimeout(requestRestore,seconds*1000);
}
function finish() {
    if (handListener) { handListener.detach(); handListener=null; }
    if (listener) { listener.detach(); listener=null; }
    const sharedMulti=shared.add(sharedMultiOffset).readU8();
    if (sharedPin !== null) { gcFree(sharedPin); sharedPin=null; }
    if (hmdPin !== null) { gcFree(hmdPin); hmdPin=null; }
    if(deadline!==null){clearTimeout(deadline);deadline=null;}
    if(restoreFallback!==null){clearTimeout(restoreFallback);restoreFallback=null;}
    state='stopped';
    send({event:'stopped',frames,sharedMulti});
}
rpc.exports = {
    apply(seconds) {
        if (state !== 'idle' || !Number.isFinite(seconds) || seconds < 1 || seconds > 60) throw new Error('Invalid apply request');
        sharedPin = managed(() => gcPin(shared, 1));
        originalShared = shared.add(sharedMultiOffset).readU8();
        state='starting';
        renew(seconds);
        try {
            handListener = Interceptor.attach(addresses.getHandState, {
                onEnter(args) { this.self=args[0]; this.output=args[1]; },
                onLeave() {
                    if (state !== 'running') return;
                    const before=[this.output.readU8(),this.output.add(1).readU8()];
                    const active=[convert(this.self,0,this.output.add(4)),convert(this.self,1,this.output.add(1460))];
                    this.output.writeU8(active[0] ? 1 : 0);
                    this.output.add(1).writeU8(active[1] ? 1 : 0);
                    frames++;
                    const now=Date.now();
                    if (now-lastSend >= 1000) {
                        lastSend=now;
                        send({event:'hands',frames,before,active});
                    }
                }
            });
            listener = Interceptor.attach(addresses.update, {
                onEnter(args) {
                    if (state === 'starting') {
                        state='applying';
                        try {
                            hmdObject=args[0];hmdPin=gcPin(hmdObject,1);
                            originalHmd=hmdObject.add(multiOffset).readU8();
                            invokeBool(sharedClass,'set_UseMultiModal',shared,1);
                            if(state!=='applying')return;
                            invokeBool(klass,'set_UseMultiModalInput',hmdObject,1);
                            if(state!=='applying')return;
                            state='running';
                            send({event:'applied',originalShared,originalHmd});
                        } catch(error) {
                            reason=String(error);
                            requestRestore();
                            send({event:'apply-failed',reason});
                        }
                    } else if (state === 'restoring' && cleanupError===null) {
                        restore();
                    }
                }
            });
        } catch(error) { requestRestore(); throw error; }
    },
    renew,
    stop() {
        requestRestore();
        return state;
    },
    dispose() {
        requestRestore();
        return new Promise((resolve,reject)=>{
            function check() {
                if(state==='idle' || state==='stopped')resolve();
                else if(state==='restore-failed')reject(new Error(cleanupError));
                else setTimeout(check,50);
            }
            check();
        });
    },
    status() { return {state,frames,reason,cleanupError}; }
};
