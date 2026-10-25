let driver=null,driverGone=false,stopped=false,reason=null,cleanupError=null;
let layout=null;
function resolveLayout(module) {
    function unsupported(detail) { throw Error('QFT_PC_LAYOUT_UNSUPPORTED: '+detail); }
    send({event:'driver-runtime',path:module.path,size:module.size,architecture:Process.arch});
    if(Process.arch!=='x64' || Process.pointerSize!==8)unsupported('architecture');
    const code=module.enumerateRanges('r-x'), data=module.enumerateRanges('rw-');
    function contains(ranges,address,size) {
        return ranges.some(r=>address.compare(r.base)>=0 && address.add(size).compare(r.base.add(r.size))<=0);
    }
    function one(name,pattern,ranges=code) {
        const matches=ranges.flatMap(r=>Memory.scanSync(r.base,r.size,pattern));
        if(matches.length!==1)unsupported(name+': expected 1 match, found '+matches.length);
        return matches[0].address;
    }
    function slot(address,size=8) {
        if(!contains(data,address,size) || !address.and(7).isNull())unsupported('global outside writable driver data');
        return address;
    }
    function relative(address,displacement,length) { return address.add(length+address.add(displacement).readS32()); }
    const nt=module.base.add(module.base.add(0x3c).readU32());
    if(module.base.readU16()!==0x5a4d || nt.readU32()!==0x4550 || nt.add(0x18).readU16()!==0x20b)
        unsupported('PE headers');
    const unwind=module.base.add(nt.add(0xa0).readU32()), unwindSize=nt.add(0xa4).readU32();
    if(unwindSize===0 || unwindSize%12!==0 || unwindSize>module.size ||
        unwind.compare(module.base)<0 || unwind.add(unwindSize).compare(module.base.add(module.size))>0)
        unsupported('unwind table');
    function containing(address) {
        const rva=address.sub(module.base).toUInt32();
        for(let at=0;at<unwindSize;at+=12) {
            const entry=unwind.add(at),start=entry.readU32(),end=entry.add(4).readU32();
            if(rva>=start && rva<end && contains(code,module.base.add(start),end-start))
                return {base:module.base.add(start),size:end-start};
        }
        unsupported('function boundary');
    }
    // Match the routing operations, then read their operands; RVAs and member offsets may move.
    const routing=one('native skeleton routing','8b c3 48 8b 8c c2 ?? ?? ?? ?? 48 85 c9 0f 84 ?? ?? ?? ?? 4c 3b b4 c2 ?? ?? ?? ?? 0f 85 ?? ?? ?? ?? e8 ?? ?? ?? ?? 84 c0 0f 85 ?? ?? ?? ??');
    const nativeFunction=containing(routing);
    const imageBase=one('routing image base','48 8d 15 ?? ?? ?? ?? 4c 8d 25 ?? ?? ?? ??',
        [{base:nativeFunction.base,size:routing.sub(nativeFunction.base).toUInt32()}]);
    if(!relative(imageBase,3,7).equals(module.base))unsupported('routing array base');
    const controllers=slot(module.base.add(routing.add(6).readU32()),16);
    const captured=slot(module.base.add(routing.add(23).readU32()),16);
    const skip=relative(routing.add(13),2,6);
    if(!relative(routing.add(27),2,6).equals(skip))unsupported('skeleton loop branches');
    const loop=one('two-controller loop','ff c3 83 fb 02 0f 82 ?? ?? ?? ??',[nativeFunction]);
    if(!skip.equals(loop) || !relative(loop.add(5),2,6).equals(routing))unsupported('skeleton loop bounds');
    const predicateAddress=relative(routing.add(33),1,5),predicate=containing(predicateAddress);
    if(!predicateAddress.equals(predicate.base))unsupported('selection function entry');
    const selection=one('optical selection','48 8d 54 24 ?? 48 8b 49 ?? e8 ?? ?? ?? ?? 83 7b ?? 01 0f b6 94 24 ?? ?? ?? ?? 0f b6 84 24 ?? ?? ?? ?? 0f 44 d0 84 d2 75 ?? 38 53 ??', [predicate]);
    const role=selection.add(16).readU8(),multi=selection.add(43).readU8(),dataOffset=selection.add(8).readU8();
    const optical=selection.add(30).readU32()-selection.add(4).readU8();
    if(selection.add(22).readU32()!==optical+selection.add(4).readU8()+1)unsupported('optical side selection');
    const snapshot=containing(relative(selection.add(9),1,5));
    const copy=one('input snapshot','48 8d 96 ?? ?? ?? ?? 41 b8 ?? ?? ?? ?? 48 8b cf e8 ?? ?? ?? ??',[snapshot]);
    const snapshotOffset=copy.add(3).readU32();
    if(optical<0 || optical+2>copy.add(9).readU32())unsupported('optical snapshot bounds');
    const trackedCode=one('physical tracking state','48 63 c2 48 6b d0 ?? 48 8b 01 0f b6 8c 02 ?? ?? ?? ?? 80 e1 03 80 f9 01 0f 94 c0 c3');
    const stride=trackedCode.add(6).readU8(),tracked=trackedCode.add(14).readU32();
    const tracking=one('controller tracking selection','80 7b ?? 00 74 ?? 48 8b 4b ?? 33 d2 83 7b ?? 01 0f 95 c2 e8 ?? ?? ?? ??');
    if(tracking.add(2).readU8()!==multi || tracking.add(9).readU8()!==dataOffset || tracking.add(14).readU8()!==role ||
        !relative(tracking.add(19),1,5).equals(trackedCode))unsupported('physical tracking layout disagreement');
    const handCode=one('virtual hand creation','44 8b 4b ?? 4c 8b 43 ?? 33 d2 48 8b c8 e8 ?? ?? ?? ?? 90 48 89 43 ?? 48 8b 05 ?? ?? ?? ?? 48 85 c0 75 1f 48 8b 0d ?? ?? ?? ?? 48 8b 01 4c 8d 44 24 ?? 48 8b 15 ?? ?? ?? ?? ff 10 48 89 05 ?? ?? ?? ??');
    if(handCode.add(3).readU8()!==role || handCode.add(7).readU8()!==dataOffset)unsupported('controller layout disagreement');
    const hand=handCode.add(22).readU8(),host=slot(relative(handCode.add(23),3,7));
    const context=slot(relative(handCode.add(35),3,7));
    if(!relative(handCode.add(59),3,7).equals(host))unsupported('host initialization');
    const poseFunction=containing(handCode);
    const deviceCode=one('controller device index','44 39 63 ?? 0f 85 ?? ?? ?? ?? 88 83 ?? ?? ?? ?? 80 7b ?? 00',[poseFunction]);
    const device=deviceCode.add(3).readU8();
    const skeletonCode=one('optical skeleton submission','c7 44 24 20 1f 00 00 00 4c 8d 8d ?? ?? ?? ?? 45 33 c0 48 8b 97 ?? ?? ?? ?? 48 8b 0d ?? ?? ?? ?? ff 15 ?? ?? ?? ?? c7 44 24 20 1f 00 00 00');
    const skeleton=skeletonCode.add(21).readU32(),opticalInput=slot(relative(skeletonCode.add(25),3,7));
    const original=slot(relative(skeletonCode.add(32),2,6));
    const second=one('second skeleton motion range','4c 8d 8d ?? ?? ?? ?? 41 b8 01 00 00 00 48 8b 97 ?? ?? ?? ?? 48 8b 0d ?? ?? ?? ?? ff 15 ?? ?? ?? ??',
        [{base:skeletonCode.add(46),size:33}]);
    if(second.add(3).readU32()!==skeletonCode.add(11).readU32() || second.add(16).readU32()!==skeleton ||
        !relative(second.add(20),3,7).equals(opticalInput) || !relative(second.add(27),2,6).equals(original))
        unsupported('skeleton motion ranges');
    const tail=one('native skeleton forwarding','4c 8b 15 ?? ?? ?? ?? 8b 85 ?? ?? ?? ?? 89 44 24 20 4d 8b cf 44 8b 44 24 ?? 49 8b d6 49 8b cd 41 ff d2',[nativeFunction]);
    if(!relative(tail,3,7).equals(original))unsupported('skeleton trampoline disagreement');
    const args=one('native skeleton arguments','4d 8b f9 44 89 44 24 ?? 4c 8b f2 4c 8b e9',[nativeFunction]);
    if(args.add(7).readU8()!==tail.add(24).readU8())unsupported('skeleton range argument');
    const offsets={role,multi,data:dataOffset,hand,device,skeleton,snapshot:snapshotOffset,optical,tracked,stride};
    if(Object.values(offsets).some(v=>v<=0 || v>0x10000) ||
        [role,multi,dataOffset,hand,device,stride,selection.add(4).readU8()].some(v=>v>=0x80) || multi===role ||
        new Set([dataOffset,hand,device,skeleton]).size!==4 || [dataOffset,hand,skeleton].some(v=>v%8!==0))
        unsupported('member offsets');
    const globals={controllers,captured,host,context,opticalInput,original};
    if(new Set(Object.values(globals).map(p=>p.toString())).size!==6)unsupported('aliased globals');
    send({event:'driver-layout',path:module.path,globals:Object.fromEntries(Object.entries(globals).map(([k,p])=>[k,p.sub(module.base).toString()])),offsets});
    return {...globals,...offsets,nativeUpdate:nativeFunction.base};
}
const observations=[0,1].map(side=>({side,state:'waiting-driver',samples:0,
    trackedCounts:[0,0,0,0],routes:0,posesSuppressed:0}));
let ticks=0,poses=0;
let inputUpdate=null;
const skeletonListeners=[];
function nativeAnchor(bones) {
    if(bones.isNull())return null;
    const anchor=new Float32Array(bones.readByteArray(64));
    if(!anchor.every(Number.isFinite))return null;
    for(const offset of [4,12]) {
        const length=Math.hypot(...anchor.subarray(offset,offset+4));
        if(Math.abs(length-1)>0.01)return null;
    }
    return anchor;
}
function rememberAnchor(h,handle,range,anchor) {
    if(anchor!==null && (range===0 || range===1))h.anchors[range]={handle,anchor};
}
function anchored(h,handle) {
    return h.anchors.every(value=>value!==null && value.handle.equals(handle));
}
function composeBone(parent,child) {
    const [x,y,z,,w,qx,qy,qz]=parent, [cx,cy,cz,,cw,cqx,cqy,cqz]=child;
    const tx=2*(qy*cz-qz*cy),ty=2*(qz*cx-qx*cz),tz=2*(qx*cy-qy*cx);
    return [x+cx+w*tx+qy*tz-qz*ty,y+cy+w*ty+qz*tx-qx*tz,z+cz+w*tz+qx*ty-qy*tx,1,
        w*cw-qx*cqx-qy*cqy-qz*cqz,w*cqx+qx*cw+qy*cqz-qz*cqy,
        w*cqy-qx*cqz+qy*cw+qz*cqx,w*cqz+qx*cqy-qy*cqx+qz*cw];
}
function anchorFingers(h,range,bones) {
    const values=new Float32Array(bones.readByteArray(31*32));
    values.set(h.anchors[range].anchor);
    // Auxiliary tips are root-relative; rebuild them after anchoring the wrist.
    for(const [finger,first] of [2,6,11,16,21].entries()) {
        let tip=values.subarray(8,16);
        const end=first===2 ? 6 : first+5;
        for(let bone=first;bone<end;bone++)tip=composeBone(tip,values.subarray(bone*8,bone*8+8));
        values.set(tip,(26+finger)*8);
    }
    const output=Memory.alloc(values.byteLength);
    output.writeByteArray(values.buffer);
    return output;
}
function physicalSkeleton(controller,side,updated=null) {
    const captured=layout.captured.add(side*8).readU64();
    const native=controller.add(layout.skeleton).readU64();
    let skeleton=captured.equals(0) ? native : captured;
    if(skeleton.equals(0) && updated!==null)skeleton=updated;
    Object.assign(observations[side],{physicalSkeleton:skeleton,capturedSkeleton:captured,controllerSkeleton:native});
    return skeleton;
}
function resolve(side) {
    const observed=observations[side];
    const controller=layout.controllers.add(side*8).readPointer();
    Object.assign(observed,{state:'waiting-controller',controller:controller.toString(),
        hand:null,physicalSkeleton:null,capturedSkeleton:null,controllerSkeleton:null,
        handSkeleton:null,device:null,data:null,frame:null,
        trackedRaw:null,tracked:null,optical:null});
    if(controller.isNull())return null;
    const hand=controller.add(layout.hand).readPointer();
    observed.hand=hand.toString();
    observed.state='waiting-hand';
    if(hand.isNull())return null;
    if(controller.add(layout.role).readU32()!==side+1 || hand.add(layout.role).readU32()!==side+1)
        throw new Error('Controller role mismatch');
    const skeleton=physicalSkeleton(controller,side);
    const original={controllerMulti:controller.add(layout.multi).readU8(),
        handMulti:hand.add(layout.multi).readU8(),skeleton:hand.add(layout.skeleton).readU64()};
    send({event:'resolved',side,controller:controller.toString(),hand:hand.toString(),
        physicalSkeleton:skeleton.toString(),handSkeleton:original.skeleton.toString()});
    Object.assign(observed,{state:'resolved',handSkeleton:original.skeleton.toString(),
        device:hand.add(layout.device).readU32()});
    return {side,controller,hand,skeleton,original,device:hand.add(layout.device).readU32(),
        updatedSkeleton:null,anchors:[null,null],active:false,physical:false,priority:false};
}
function current(h) {
    if(driverGone)return false;
    const controller=layout.controllers.add(h.side*8).readPointer();
    return controller.equals(h.controller) && controller.add(layout.hand).readPointer().equals(h.hand);
}
const hands=[null,null];
function route(h) {
    const observed=observations[h.side];
    if(h.updatedSkeleton!==null && h.updatedSkeleton.shr(32).toNumber()!==h.controller.add(layout.device).readU32())
        h.updatedSkeleton=null;
    const skeleton=physicalSkeleton(h.controller,h.side,h.updatedSkeleton);
    const data=h.controller.add(layout.data).readPointer();
    Object.assign(observed,{state:'waiting-data',device:h.device,data,
        frame:null,trackedRaw:null,tracked:null,optical:null});
    if(data.isNull())return;
    const frame=data.readPointer();
    Object.assign(observed,{state:'waiting-frame',frame});
    if(frame.isNull())return;
    const trackedRaw=frame.add(layout.tracked+h.side*layout.stride).readU8();
    const tracked=trackedRaw&3;
    const optical=data.add(layout.snapshot+layout.optical+h.side).readU8();
    Object.assign(observed,{state:'sampling',trackedRaw,tracked,optical});
    observed.samples++;
    observed.trackedCounts[tracked]++;
    const physical=tracked===1;
    const priority=physical && optical===1;
    // Switching finger sources must keep the physical root and wrist.
    const active=priority && !skeleton.equals(0) && anchored(h,skeleton);
    if(active!==h.active || physical!==h.physical || priority!==h.priority || (active && !skeleton.equals(h.skeleton))) {
        h.hand.add(layout.skeleton).writeU64(active ? skeleton : h.original.skeleton);
        h.hand.add(layout.multi).writeU8(physical ? 1 : h.original.handMulti);
        h.controller.add(layout.multi).writeU8(priority ? 1 : h.original.controllerMulti);
        h.active=active;
        h.physical=physical;
        h.priority=priority;
        observed.routes++;
        send({event:'route',side:h.side,active,physical,priority,tracked,optical,
            physicalSkeleton:skeleton.toString()});
    }
    h.skeleton=skeleton;
}
function hookInput() {
    const opticalInput=layout.opticalInput.readPointer();
    const original=layout.original.readPointer();
    if(opticalInput.isNull() || original.isNull())return false;
    // VD suppresses native 004 writes before its trampoline, so capture the wrist here.
    skeletonListeners.push(Interceptor.attach(layout.nativeUpdate, {
        onEnter(args) {
            if(stopped || args[4].toUInt32()!==31 || args[0].equals(opticalInput))return;
            try {
                const handle=uint64(args[1].toString());
                this.hand=hands.find(h=>h!==null && current(h) &&
                    physicalSkeleton(h.controller,h.side,h.updatedSkeleton).equals(handle));
                if(this.hand===undefined)return;
                this.handle=handle;this.range=args[2].toUInt32();this.anchor=nativeAnchor(args[3]);
            } catch(error) { halt('PC wrist capture failed: '+error); }
        },
        onLeave(result) {
            if(stopped || this.hand===undefined || result.toInt32()!==0)return;
            rememberAnchor(this.hand,this.handle,this.range,this.anchor);
        }
    }));
    skeletonListeners.push(Interceptor.attach(original, {
        onEnter(args) {
            if(stopped || !args[0].equals(opticalInput) || args[4].toUInt32()!==31)return;
            try {
                const handle=uint64(args[1].toString()),range=args[2].toUInt32();
                const h=hands.find(h=>h!==null && h.active && current(h) && h.skeleton.equals(handle));
                if(h===undefined || (range!==0 && range!==1) || !anchored(h,handle))return;
                this.bones=anchorFingers(h,range,args[3]);
                args[3]=this.bones;
            } catch(error) { halt('PC wrist anchoring failed: '+error); }
        },
        onLeave() { this.bones=null; }
    }));
    const context = layout.context.readPointer();
    if (context.isNull()) return true;
    const input = new NativeFunction(context.readPointer().readPointer(),
        'pointer', ['pointer', 'pointer', 'pointer'])(
        context, Memory.allocUtf8String('IVRDriverInput_005'), Memory.alloc(4));
    if (input.isNull()) return true;

    const update = new NativeFunction(input.readPointer().add(6 * Process.pointerSize).readPointer(),
        'int', ['pointer', 'uint64', 'int', 'pointer', 'uint32']);
    Interceptor.replace(update, new NativeCallback((self, handle, range, bones, count) => {
        let hand,anchor=null;
        try {
            if (!stopped && count === 31 && !self.equals(input)) {
                hand = hands.find(h => h !== null && current(h) &&
                    h.controller.add(layout.device).readU32() === handle.shr(32).toNumber());
                if(hand!==undefined)anchor=nativeAnchor(bones);
                if (hand?.active && hand.skeleton.equals(handle)) {
                    rememberAnchor(hand,handle,range,anchor);
                    return 0;
                }
            }
        } catch (error) {
            halt('PC skeleton routing failed: ' + error);
        }

        const result = update(self, handle, range, bones, count);
        if (!stopped && hand && result === 0 && !handle.equals(0)) {
            hand.updatedSkeleton = handle;
            rememberAnchor(hand,handle,range,anchor);
        }
        return result;
    }, 'int', ['pointer', 'uint64', 'int', 'pointer', 'uint32']));
    inputUpdate = update;
    return true;
}
let timer=null,deadline=null,poseListener=null;
const modules=Process.attachModuleObserver({
    onRemoved(module) {
        if(driver===null || !module.base.equals(driver.base))return;
        driverGone=true;
        halt('Virtual Desktop driver unloaded');
    }
});
function restore(h) {
    if(h===null || !current(h))return;
    h.hand.add(layout.skeleton).writeU64(h.original.skeleton);
    h.hand.add(layout.multi).writeU8(h.original.handMulti);
    h.controller.add(layout.multi).writeU8(h.original.controllerMulti);
    h.active=false;
    h.physical=false;
    h.priority=false;
}
function renew(seconds) {
    if(seconds<1 || seconds>90)throw new Error('Invalid lease');
    if(stopped)throw new Error(reason || 'PC routing stopped');
    if(deadline!==null)clearTimeout(deadline);
    deadline=setTimeout(stop,seconds*1000);
}
function stop() {
    if(stopped) {
        if(cleanupError!==null)throw new Error(cleanupError);
        return;
    }
    if(driver!==null && !driverGone) {
        const loaded=Process.findModuleByName('driver_VirtualDesktop.dll');
        driverGone=loaded===null || !loaded.base.equals(driver.base);
        if(driverGone)reason='Virtual Desktop driver unloaded';
    }
    const wasRunning=timer!==null;
    stopped=true;
    if(timer!==null){clearInterval(timer);timer=null;}
    if(deadline!==null){clearTimeout(deadline);deadline=null;}
    modules.detach();
    const failures=[];
    if(inputUpdate!==null) {
        try { Interceptor.revert(inputUpdate); } catch(error) { failures.push(String(error)); }
        inputUpdate=null;
    }
    for(const listener of skeletonListeners.splice(0)) {
        try { listener.detach(); } catch(error) { failures.push(String(error)); }
    }
    if(poseListener!==null) {
        try { poseListener.detach(); } catch(error) { failures.push(String(error)); }
        poseListener=null;
    }
    if(!driverGone)for(const hand of hands) {
        try { restore(hand); } catch(error) { failures.push(String(error)); }
    }
    cleanupError=failures.length ? failures.join('; ') : null;
    send({event:wasRunning && reason===null && cleanupError===null ? 'restored' : 'stopped',reason,cleanupError});
    if(cleanupError!==null)throw new Error(cleanupError);
}
function halt(message) {
    reason=message;
    try { stop(); } catch(error) {}
}
rpc.exports={
    start(seconds) {
        if(stopped || timer!==null || seconds<1 || seconds>90)throw new Error('Invalid start request');
        driver=Process.findModuleByName('driver_VirtualDesktop.dll');
        if(driver===null)return false;
        if(layout===null) {
            try { layout=resolveLayout(driver); }
            catch(error) {
                if(String(error).includes('QFT_PC_LAYOUT_UNSUPPORTED:'))throw error;
                throw Error('QFT_PC_LAYOUT_UNSUPPORTED: '+error);
            }
        }
        const host=layout.host.readPointer();
        if(host.isNull())return false;
        hands[0]=resolve(0);hands[1]=resolve(1);
        if(!hookInput())return false;
        const poseUpdated=host.readPointer().add(Process.pointerSize).readPointer();
        poseListener=Interceptor.attach(poseUpdated, {
            onEnter(args) {
                if(stopped)return;
                try {
                    poses++;
                    const device=args[1].toUInt32();
                    const controller=hands.find(h=>h!==null && current(h) && h.controller.add(layout.device).readU32()===device);
                    // VD can receive the first optical frame before our polling timer.
                    if(controller!==undefined)route(controller);
                    const h=hands.find(h=>h!==null && h.device===device);
                    if(h===undefined)return;
                    const pose=args[2];
                    pose.add(0x114).writeU8(0);
                    pose.add(0x117).writeU8(0);
                    observations[h.side].posesSuppressed++;
                } catch(error) { halt('PC pose routing failed: '+error); }
            }
        });
        timer=setInterval(()=>{
            if(stopped)return;
            try {
                ticks++;
                for(let side=0;side<2;side++) {
                    let h=hands[side];
                    if(h===null || !current(h)) {
                        if(h!==null)send({event:'controller-changed',side});
                        h=hands[side]=resolve(side);
                        if(h===null)continue;
                    }
                    h.device=h.hand.add(layout.device).readU32();
                    route(h);
                }
            } catch(error) { halt('PC routing failed: '+error); }
        },10);
        renew(seconds);
        return true;
    },
    renew,
    status(){return {running:timer!==null,reason,cleanupError,ticks,poses,driverPath:driver?.path ?? null,
        active:hands.map(h=>h!==null && h.active),
        sides:observations.map((observed,side)=>({...Object.fromEntries(Object.entries(observed).map(([key,value])=>
            [key,value!==null && typeof value==='object' && !Array.isArray(value) ? value.toString() : value])),
            physical:hands[side]!==null && hands[side].physical,
            priority:hands[side]!==null && hands[side].priority}))};},
    stop,
    dispose:stop,
};
