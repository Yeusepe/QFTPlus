const driver=Process.getModuleByName('driver_VirtualDesktop.dll');
const hands=[];
for(let side=0;side<2;side++) {
    const controller=driver.base.add(0x9a740+side*8).readPointer();
    if(controller.isNull())throw new Error('Controller shim unavailable');
    const hand=controller.add(0x48).readPointer();
    const skeleton=driver.base.add(0x9a7a8+side*8).readU64();
    if(hand.isNull() || skeleton.equals(0))throw new Error('Skeleton handles unavailable');
    if(controller.add(0x1c).readU32()!==side+1 || hand.add(0x1c).readU32()!==side+1)
        throw new Error('Controller role mismatch');
    const original={controllerMulti:controller.add(0x1a).readU8(),
        handMulti:hand.add(0x1a).readU8(),skeleton:hand.add(0xf0).readU64()};
    hands.push({side,controller,hand,skeleton,original,active:false,physical:false});
    send({event:'resolved',side,controller:controller.toString(),hand:hand.toString(),
        physicalSkeleton:skeleton.toString(),handSkeleton:original.skeleton.toString()});
}
let timer=null,deadline=null,poseListener=null;
function restore(h) {
    const current=driver.base.add(0x9a740+h.side*8).readPointer();
    if(!current.equals(h.controller) || !current.add(0x48).readPointer().equals(h.hand))return;
    h.hand.add(0xf0).writeU64(h.original.skeleton);
    h.hand.add(0x1a).writeU8(h.original.handMulti);
    h.controller.add(0x1a).writeU8(h.original.controllerMulti);
    h.active=false;
    h.physical=false;
}
function renew(seconds) {
    if(seconds<1 || seconds>90)throw new Error('Invalid lease');
    if(deadline!==null)clearTimeout(deadline);
    deadline=setTimeout(stop,seconds*1000);
}
function stop() {
    if(timer!==null){clearInterval(timer);timer=null;}
    if(deadline!==null){clearTimeout(deadline);deadline=null;}
    if(poseListener!==null){poseListener.detach();poseListener=null;}
    hands.forEach(restore);
    send({event:'restored'});
}
rpc.exports={
    start(seconds) {
        if(timer!==null || seconds<1 || seconds>90)throw new Error('Invalid duration');
        const host=driver.base.add(0x9a6e0).readPointer();
        if(host.isNull())throw new Error('SteamVR driver host is unavailable');
        const poseUpdated=host.readPointer().add(Process.pointerSize).readPointer();
        poseListener=Interceptor.attach(poseUpdated, {
            onEnter(args) {
                if(!hands.some(h=>h.hand.add(0x40).readU32()===args[1].toUInt32()))return;
                const pose=args[2];
                pose.add(0x114).writeU8(0);
                pose.add(0x117).writeU8(0);
            }
        });
        timer=setInterval(()=>{
            for(const h of hands) {
                const current=driver.base.add(0x9a740+h.side*8).readPointer();
                if(!current.equals(h.controller) || !current.add(0x48).readPointer().equals(h.hand)){stop();throw new Error('Controller changed');}
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
        },10);
        renew(seconds);
    },
    renew,
    status(){return {running:timer!==null,active:hands.map(h=>h.active)};},
    stop,
};
