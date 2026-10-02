let driver=null,driverGone=false,stopped=false,reason=null,cleanupError=null;
const observations=[0,1].map(side=>({side,state:'waiting-driver',samples:0,
    trackedCounts:[0,0,0,0],routes:0,posesSuppressed:0}));
let ticks=0,poses=0;
let inputUpdate=null;
function physicalSkeleton(controller,side,updated=null) {
    const captured=driver.base.add(0x9a7a8+side*8).readU64();
    const native=controller.add(0xf0).readU64();
    let skeleton=captured.equals(0) ? native : captured;
    if(skeleton.equals(0) && updated!==null)skeleton=updated;
    Object.assign(observations[side],{physicalSkeleton:skeleton,capturedSkeleton:captured,controllerSkeleton:native});
    return skeleton;
}
function resolve(side) {
    const observed=observations[side];
    const controller=driver.base.add(0x9a740+side*8).readPointer();
    Object.assign(observed,{state:'waiting-controller',controller:controller.toString(),
        hand:null,physicalSkeleton:null,capturedSkeleton:null,controllerSkeleton:null,
        handSkeleton:null,device:null,data:null,frame:null,
        trackedRaw:null,tracked:null,optical:null});
    if(controller.isNull())return null;
    const hand=controller.add(0x48).readPointer();
    observed.hand=hand.toString();
    observed.state='waiting-hand';
    if(hand.isNull())return null;
    if(controller.add(0x1c).readU32()!==side+1 || hand.add(0x1c).readU32()!==side+1)
        throw new Error('Controller role mismatch');
    const skeleton=physicalSkeleton(controller,side);
    const original={controllerMulti:controller.add(0x1a).readU8(),
        handMulti:hand.add(0x1a).readU8(),skeleton:hand.add(0xf0).readU64()};
    send({event:'resolved',side,controller:controller.toString(),hand:hand.toString(),
        physicalSkeleton:skeleton.toString(),handSkeleton:original.skeleton.toString()});
    Object.assign(observed,{state:'resolved',handSkeleton:original.skeleton.toString(),
        device:hand.add(0x40).readU32()});
    return {side,controller,hand,skeleton,original,device:hand.add(0x40).readU32(),
        updatedSkeleton:null,active:false,physical:false,priority:false};
}
function current(h) {
    if(driverGone)return false;
    const controller=driver.base.add(0x9a740+h.side*8).readPointer();
    return controller.equals(h.controller) && controller.add(0x48).readPointer().equals(h.hand);
}
const hands=[null,null];
function hookInput() {
    const context = driver.base.add(0x9a6b8).readPointer();
    if (context.isNull()) return;
    const input = new NativeFunction(context.readPointer().readPointer(),
        'pointer', ['pointer', 'pointer', 'pointer'])(
        context, Memory.allocUtf8String('IVRDriverInput_005'), Memory.alloc(4));
    if (input.isNull()) return;

    const update = new NativeFunction(input.readPointer().add(6 * Process.pointerSize).readPointer(),
        'int', ['pointer', 'uint64', 'int', 'pointer', 'uint32']);
    Interceptor.replace(update, new NativeCallback((self, handle, range, bones, count) => {
        let hand;
        try {
            if (!stopped && count === 31 && !self.equals(input)) {
                hand = hands.find(h => h !== null && current(h) &&
                    h.controller.add(0x40).readU32() === handle.shr(32).toNumber());
                if (hand?.active && hand.skeleton.equals(handle)) return 0;
            }
        } catch (error) {
            halt('PC skeleton routing failed: ' + error);
        }

        const result = update(self, handle, range, bones, count);
        if (!stopped && hand && result === 0 && !handle.equals(0)) hand.updatedSkeleton = handle;
        return result;
    }, 'int', ['pointer', 'uint64', 'int', 'pointer', 'uint32']));
    inputUpdate = update;
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
    h.hand.add(0xf0).writeU64(h.original.skeleton);
    h.hand.add(0x1a).writeU8(h.original.handMulti);
    h.controller.add(0x1a).writeU8(h.original.controllerMulti);
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
        const host=driver.base.add(0x9a6e0).readPointer();
        if(host.isNull())return false;
        hands[0]=resolve(0);hands[1]=resolve(1);
        hookInput();
        const poseUpdated=host.readPointer().add(Process.pointerSize).readPointer();
        poseListener=Interceptor.attach(poseUpdated, {
            onEnter(args) {
                if(stopped)return;
                try {
                    poses++;
                    const device=args[1].toUInt32();
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
                    h.device=h.hand.add(0x40).readU32();
                    const observed=observations[side];
                    if(h.updatedSkeleton!==null && h.updatedSkeleton.shr(32).toNumber()!==h.controller.add(0x40).readU32())
                        h.updatedSkeleton=null;
                    const skeleton=physicalSkeleton(h.controller,side,h.updatedSkeleton);
                    const data=h.controller.add(0x10).readPointer();
                    Object.assign(observed,{state:'waiting-data',device:h.device,data,
                        frame:null,trackedRaw:null,tracked:null,optical:null});
                    if(data.isNull())continue;
                    const frame=data.readPointer();
                    Object.assign(observed,{state:'waiting-frame',frame});
                    if(frame.isNull())continue;
                    const trackedRaw=frame.add(0x8c+h.side*0x44).readU8();
                    const tracked=trackedRaw&3;
                    const optical=data.add(0xc8+0x168+h.side).readU8();
                    Object.assign(observed,{state:'sampling',trackedRaw,tracked,optical});
                    observed.samples++;
                    observed.trackedCounts[tracked]++;
                    const physical=tracked===1;
                    const priority=physical && optical===1;
                    const active=priority && !skeleton.equals(0);
                    if(active!==h.active || physical!==h.physical || priority!==h.priority || (active && !skeleton.equals(h.skeleton))) {
                        h.hand.add(0xf0).writeU64(active ? skeleton : h.original.skeleton);
                        h.hand.add(0x1a).writeU8(physical ? 1 : h.original.handMulti);
                        h.controller.add(0x1a).writeU8(priority ? 1 : h.original.controllerMulti);
                        h.active=active;
                        h.physical=physical;
                        h.priority=priority;
                        observed.routes++;
                        send({event:'route',side:h.side,active,physical,priority,tracked,optical,
                            physicalSkeleton:skeleton.toString()});
                    }
                    h.skeleton=skeleton;
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
