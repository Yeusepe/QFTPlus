const mono = Process.getModuleByName('libmonosgen-2.0.so');
function api(name, ret, args) {
    return new NativeFunction(mono.getExportByName(name), ret, args);
}
const domain = api('mono_get_root_domain', 'pointer', [])();
api('mono_thread_attach', 'pointer', ['pointer'])(domain);
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

const setMulti = new NativeFunction(addresses.setMultiModal, 'void', ['pointer','int']);
const setSharedMulti = new NativeFunction(compile(getMethod(sharedClass,utf8('set_UseMultiModal'),1)),
    'void',['pointer','int']);
const convert = new NativeFunction(addresses.convertFingerState, 'int', ['pointer','int','pointer']);
const gcPin = api('mono_gchandle_new', 'uint', ['pointer','int']);
const gcFree = api('mono_gchandle_free', 'void', ['uint']);
let listener = null, handListener = null, lastSend = 0, frames = 0;
let state = 'idle', originalShared = null, originalHmd = null, sharedPin = null;
let hmdObject=null, hmdPin=null, deadline=null, restoreFallback=null;
const invoke=api('mono_runtime_invoke','pointer',['pointer','pointer','pointer','pointer']);
function invokeBool(klass, name, object, value) {
    const argument=Memory.alloc(4);argument.writeS32(value);
    const argv=Memory.alloc(Process.pointerSize);argv.writePointer(argument);
    const exception=Memory.alloc(Process.pointerSize);exception.writePointer(ptr(0));
    invoke(getMethod(klass,utf8(name),1),object,argv,exception);
    if(!exception.readPointer().isNull())throw new Error('Managed restore failed: '+name);
}
function requestRestore() {
    if(state!=='running' && state!=='starting')return;
    state='restoring';
    restoreFallback=setTimeout(()=>{
        if(state!=='restoring')return;
        invokeBool(sharedClass,'set_UseMultiModal',shared,originalShared);
        if(hmdObject!==null)invokeBool(klass,'set_UseMultiModalInput',hmdObject,originalHmd);
        finish();
    },2000);
}
function renew(seconds) {
    if(seconds<1 || seconds>60)throw new Error('Invalid lease');
    if(deadline!==null)clearTimeout(deadline);
    deadline=setTimeout(requestRestore,seconds*1000);
}
function finish() {
    if(deadline!==null){clearTimeout(deadline);deadline=null;}
    if(restoreFallback!==null){clearTimeout(restoreFallback);restoreFallback=null;}
    if (handListener) { handListener.detach(); handListener=null; }
    if (listener) { listener.detach(); listener=null; }
    if (sharedPin !== null) { gcFree(sharedPin); sharedPin=null; }
    if (hmdPin !== null) { gcFree(hmdPin); hmdPin=null; }
    state='stopped';
    send({event:'stopped',frames,sharedMulti:shared.add(sharedMultiOffset).readU8()});
}
rpc.exports = {
    apply(seconds) {
        if (state !== 'idle' || seconds < 1 || seconds > 60) throw new Error('Invalid apply request');
        sharedPin = gcPin(shared, 1);
        originalShared = shared.add(sharedMultiOffset).readU8();
        state='starting';
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
                    hmdObject=args[0];hmdPin=gcPin(hmdObject,1);
                    originalHmd=args[0].add(multiOffset).readU8();
                    setSharedMulti(shared,1);
                    setMulti(args[0],1);
                    state='running';
                    send({event:'applied',originalShared,originalHmd});
                } else if (state === 'restoring') {
                    setSharedMulti(shared,originalShared);
                    if (originalHmd !== null) setMulti(args[0],originalHmd);
                    finish();
                }
            }
        });
        renew(seconds);
    },
    renew,
    stop() {
        requestRestore();
        return state;
    },
    status() { return {state,frames}; },
    observe(seconds) {
        if (listener) throw new Error('Already observing');
        const controllerType = new NativeFunction(method('GetControllerType',1),'int',['pointer','int']);
        const activeType = new NativeFunction(method('get_ActiveControllerType',0),'int',['pointer']);
        listener = Interceptor.attach(addresses.update, {
            onEnter(args) { this.self = args[0]; this.output = args[1]; },
            onLeave() {
                frames++;
                const now = Date.now();
                if (now-lastSend >= 1000) {
                    lastSend = now;
                    send({event:'sample', frames, multi:this.self.add(multiOffset).readU8(),
                        sharedMulti:shared.add(sharedMultiOffset).readU8(),
                        activeType:activeType(this.self),controllerTypes:[controllerType(this.self,0),controllerType(this.self,1)]});
                }
            }
        });
        setTimeout(() => { listener.detach(); listener=null; send({event:'stopped',frames}); },seconds*1000);
    }
};
