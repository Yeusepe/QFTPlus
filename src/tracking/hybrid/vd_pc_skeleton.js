let driver=null,driverGone=false,stopped=false,reason=null,cleanupError=null;
function resolve(side) {
    const controller=driver.base.add(0x9a740+side*8).readPointer();
    if(controller.isNull())return null;
    const hand=controller.add(0x48).readPointer();
    const skeleton=driver.base.add(0x9a7a8+side*8).readU64();
    if(hand.isNull() || skeleton.equals(0))return null;
    if(controller.add(0x1c).readU32()!==side+1 || hand.add(0x1c).readU32()!==side+1)
        throw new Error('Controller role mismatch');
    const original={controllerMulti:controller.add(0x1a).readU8(),
        handMulti:hand.add(0x1a).readU8(),skeleton:hand.add(0xf0).readU64()};
    send({event:'resolved',side,controller:controller.toString(),hand:hand.toString(),
        physicalSkeleton:skeleton.toString(),handSkeleton:original.skeleton.toString()});
    return {side,controller,hand,skeleton,original,device:hand.add(0x40).readU32(),active:false,physical:false};
}
function current(h) {
    if(driverGone)return false;
    const controller=driver.base.add(0x9a740+h.side*8).readPointer();
    return controller.equals(h.controller) && controller.add(0x48).readPointer().equals(h.hand);
}
const hands=[null,null];
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
        const poseUpdated=host.readPointer().add(Process.pointerSize).readPointer();
        poseListener=Interceptor.attach(poseUpdated, {
            onEnter(args) {
                if(stopped)return;
                try {
                    const device=args[1].toUInt32();
                    if(!hands.some(h=>h!==null && h.device===device))return;
                    const pose=args[2];
                    pose.add(0x114).writeU8(0);
                    pose.add(0x117).writeU8(0);
                } catch(error) { halt('PC pose routing failed: '+error); }
            }
        });
        timer=setInterval(()=>{
            if(stopped)return;
            try {
                for(let side=0;side<2;side++) {
                    let h=hands[side];
                    if(h===null || !current(h)) {
                        if(h!==null)send({event:'controller-changed',side});
                        h=hands[side]=resolve(side);
                        if(h===null)continue;
                    }
                    h.device=h.hand.add(0x40).readU32();
                    const data=h.controller.add(0x10).readPointer();
                    const frame=data.readPointer();
                    const tracked=frame.add(0x8c+h.side*0x44).readU8()&3;
                    const optical=data.add(0xc8+0x168+h.side).readU8();
                    const physical=tracked===1;
                    const active=physical && optical===1;
                    if(active!==h.active || physical!==h.physical) {
                        h.hand.add(0xf0).writeU64(active ? h.skeleton : h.original.skeleton);
                        h.hand.add(0x1a).writeU8(physical ? 1 : h.original.handMulti);
                        h.controller.add(0x1a).writeU8(active ? 1 : h.original.controllerMulti);
                        h.active=active;
                        h.physical=physical;
                        send({event:'route',side:h.side,active,physical,tracked,optical});
                    }
                }
            } catch(error) { halt('PC routing failed: '+error); }
        },10);
        renew(seconds);
        return true;
    },
    renew,
    status(){return {running:timer!==null,reason,cleanupError,active:hands.map(h=>h!==null && h.active)};},
    stop,
    dispose:stop,
};
