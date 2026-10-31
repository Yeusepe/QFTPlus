import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.channels.*;
import java.util.regex.*;
import org.json.*;

public final class ControllerRecovery {
    static File watchModule;
    static void log(String message) { System.out.println((System.nanoTime()/1_000_000L)+" "+message); }
    static final Pattern HEAD=Pattern.compile("^Frame: v\\d+, 6DoF,.*active: true$",Pattern.MULTILINE);
    static boolean savedRoomReady(String dump) throws Exception {
        if(!HEAD.matcher(dump).find())return false;
        for(String line:dump.split("\\n")) {
            if(!line.startsWith("{\"anchorLocalizationEdges\":"))continue;
            JSONObject data=new JSONObject(line);JSONArray anchors=data.getJSONArray("anchors");
            for(int i=0;i<anchors.length();i++) {
                JSONObject a=anchors.getJSONObject(i);
                if(!a.optBoolean("worldOrigin")||!a.optBoolean("persistent")||!a.optBoolean("registered")||!a.optBoolean("isTracked"))continue;
                JSONArray nodes=data.getJSONArray("l1Nodes");
                for(int j=0;j<nodes.length();j++) {
                    JSONObject node=nodes.getJSONObject(j);String root=node.optString("rootNodeUid");
                    if(node.optString("l1NodeUid").equals(a.optString("l1NodeUuid"))&&root.matches("[0-9a-f]{8}")&&!root.equals("00000000"))return true;
                }
            }
        }
        return false;
    }
    static boolean controllersReady(String dump) {
        int count=0;
        for(String line:dump.split("\\n")) {
            if(!line.contains("Tracking Level:")||!line.contains("InHand:"))continue;
            if(!line.contains("Tracking Level: 6DOF (PosTracked=1, PosValid=1, OrientTracked=1, OrientValid=1)")||!line.contains("Valid: Yes"))return false;
            count++;
        }
        return count==2;
    }
    static String command(String text) throws Exception {
        Process p=new ProcessBuilder("timeout","8","sh","-c",text).redirectErrorStream(true).start();
        ByteArrayOutputStream data=new ByteArrayOutputStream();
        try(InputStream input=p.getInputStream()) {byte[] block=new byte[8192];int n;while((n=input.read(block))!=-1)data.write(block,0,n);}
        if(p.waitFor()!=0)throw new IOException("Controller recovery command failed: "+text);
        return new String(data.toByteArray(),StandardCharsets.UTF_8).trim();
    }
    static String pid(String name) throws Exception {
        String value=command("pidof "+name+" || true");
        if(!value.matches("[0-9]+"))throw new IOException("Missing or ambiguous process: "+name);
        return value;
    }
    static void sameService(String expected) throws Exception {
        if(watchModule!=null&&(!new File(watchModule,"module.prop").isFile()||new File(watchModule,"disable").exists()))
            throw new IOException("Controller recovery is disabled");
        if(!pid("trackingservice").equals(expected))throw new IOException("Tracking service changed during controller recovery");
    }
    static boolean recover() throws Exception {
        try(FileOutputStream file=new FileOutputStream("/data/local/tmp/qft-controller-repair.lock");FileLock lock=file.getChannel().tryLock()) {
            if(lock==null){log("CONTROLLER_RECOVERY_BUSY");return false;}
            return recoverLocked();
        }
    }
    static String serviceStamp(String pid) throws Exception {
        return pid+" "+command("awk '{print $22}' /proc/"+pid+"/stat")+" "+command("cat /proc/sys/kernel/random/boot_id");
    }
    static boolean recoverLocked() throws Exception {
        String expected=pid("trackingservice");long end=System.nanoTime()+60_000_000_000L;int stable=0;
        while(System.nanoTime()<end) {
            sameService(expected);
            stable=savedRoomReady(command("dumpsys tracking"))?stable+1:0;
            if(stable>=3)break;
            Thread.sleep(1000);
        }
        if(stable<3){log("CONTROLLER_ROOM_PENDING "+expected);return false;}
        log("CONTROLLER_ROOM_READY "+expected);
        if(!command("dumpsys cm_wifi").contains("Any controller updating: false")){log("CONTROLLER_UPDATE_PENDING "+expected);return false;}
        String manager=pid("com.oculus.os.cm");
        if(!command("cat /proc/"+manager+"/cmdline").replace("\u0000", "").equals("com.oculus.os.cm"))throw new IOException("Controller manager identity changed");
        // Reconnect only after localization, so controllers import the saved-room origin.
        sameService(expected);command("kill -TERM "+manager);
        log("CONTROLLER_RECONNECT "+manager+" tracking="+expected);
        end=System.nanoTime()+75_000_000_000L;stable=0;
        while(System.nanoTime()<end) {
            sameService(expected);
            stable=controllersReady(command("dumpsys tracking"))?stable+1:0;
            if(stable>=10) {
                try(FileOutputStream ready=new FileOutputStream("/data/local/tmp/qft-controller-recovery-ready")) {
                    ready.write(serviceStamp(expected).getBytes(StandardCharsets.UTF_8));
                }
                log("CONTROLLER_PAIR_VALID "+expected);return true;
            }
            Thread.sleep(1000);
        }
        throw new IOException("Controllers did not remain pose-valid");
    }
    static void watch(File module) throws Exception {
        watchModule=module;
        if(!command("getprop ro.product.device").equals("seacliff"))throw new IOException("Unsupported headset");
        try(FileOutputStream file=new FileOutputStream("/data/local/tmp/qft-controller-recovery.lock");FileLock lock=file.getChannel().tryLock()) {
            if(lock==null)throw new IOException("Controller recovery is already running");
            String previous=command("pidof trackingservice || true");
            log("CONTROLLER_RECOVERY_ARMED "+previous);
            while(new File(module,"module.prop").isFile()&&!new File(module,"disable").exists()) {
                Thread.sleep(1000);
                String current=command("pidof trackingservice || true");
                if(!current.matches("[0-9]+")||current.equals(previous))continue;
                if(previous.isEmpty()){previous=current;continue;}
                try {
                    if(command("cat /data/local/tmp/qft-controller-recovery-ready 2>/dev/null || true").equals(serviceStamp(current))) {previous=current;continue;}
                    if(recover())previous=current;
                    else {log("CONTROLLER_RECOVERY_DEFERRED "+current);Thread.sleep(5000);}
                } catch(Exception error) {
                    previous=current;
                    log("CONTROLLER_RECOVERY_FAILED "+current+" "+error);
                }
            }
        }
    }
    public static void main(String[] args) throws Exception {
        if(args.length==2&&args[0].equals("--watch")){watch(new File(args[1]));return;}
        if(args.length!=0)throw new IllegalArgumentException("Unsupported controller recovery argument");
        if(!recover())throw new IOException("Controller recovery is waiting for the saved room or a firmware update");
    }
}
