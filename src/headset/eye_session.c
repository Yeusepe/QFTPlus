#define _GNU_SOURCE
#include "frida-core.h"
#include "shared.h"
#include <signal.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <spawn.h>

#define TRACE "/sys/kernel/tracing"
#define GROUP "qft_live_eyes"
#define INSTANCE TRACE "/instances/" GROUP
#define CONTROL_DIR "/data/local/tmp/qft-tracking-control-v1"
#define CONTROL_ADDRESS "127.0.0.1:27381"
extern char **environ;
static int averaging;
static char commands[128];
static size_t command_length;
static GMainLoop *loop;
static FridaScript *script;
static int trace_fd=-1, stopping, ready, restored, failed, detached, detaching, traced;
static int attach_lock=-1;
static volatile sig_atomic_t interrupted;
static gint64 started, stop_at;
static gint64 heartbeat_at, alive_at;
static void signal_stop(int number);

static int lock_attachment(void) {
    if(attach_lock<0)attach_lock=open("/data/local/tmp/qft-tracking-attach.lock",O_CREAT|O_RDWR|O_CLOEXEC|O_NOFOLLOW,0600);
    if(attach_lock<0)return -1;
    gint64 deadline=g_get_monotonic_time()+10000000;
    do {
        if(!flock(attach_lock,LOCK_EX|LOCK_NB))return 0;
        if(errno!=EWOULDBLOCK&&errno!=EINTR)return -1;
        usleep(50000);
    } while(g_get_monotonic_time()<deadline);
    return -1;
}
static void unlock_attachment(void) {if(attach_lock>=0)flock(attach_lock,LOCK_UN);}

static gboolean broker_idle(gpointer data) {
    unsigned *idle=data;
    const char *paths[]={"/data/local/tmp/qft-live-eyes.lock","/data/local/tmp/qft-meta-gaze.lock"};
    int active=0;
    for(unsigned i=0;i<2;i++) {
        int fd=open(paths[i],O_RDWR|O_CLOEXEC|O_NOFOLLOW);
        if(fd<0)continue;
        if(flock(fd,LOCK_EX|LOCK_NB))active=1;
        else flock(fd,LOCK_UN);
        close(fd);
    }
    *idle=active?0:*idle+1;
    if(interrupted||*idle>=20){g_main_loop_quit(loop);return G_SOURCE_REMOVE;}
    return G_SOURCE_CONTINUE;
}

int qft_eye_broker_main(void) {
    if(geteuid()!=0)return 2;
    umask(077);
    if(mkdir(CONTROL_DIR,0700)&&errno!=EEXIST)return 3;
    struct stat st;
    if(lstat(CONTROL_DIR,&st)||!S_ISDIR(st.st_mode)||st.st_uid|| (st.st_mode&077))return 3;
    int owner=open(CONTROL_DIR "/owner",O_CREAT|O_RDWR|O_CLOEXEC|O_NOFOLLOW,0600);
    if(owner<0||flock(owner,LOCK_EX|LOCK_NB))return 3;
    unsigned char random[32];char token[65];
    int entropy=open("/dev/urandom",O_RDONLY|O_CLOEXEC);
    if(entropy<0||read(entropy,random,sizeof random)!=sizeof random)return 3;
    close(entropy);
    for(unsigned i=0;i<sizeof random;i++)snprintf(token+i*2,3,"%02x",random[i]);
    frida_init();GError *error=NULL;
    FridaStaticAuthenticationService *auth=frida_static_authentication_service_new(token);
    FridaEndpointParameters *endpoint=frida_endpoint_parameters_new("127.0.0.1",27381,NULL,NULL,FRIDA_AUTHENTICATION_SERVICE(auth),NULL);
    FridaControlServiceOptions *options=frida_control_service_options_new();
    frida_control_service_options_set_enable_preload(options,FALSE);
    FridaControlService *service=frida_control_service_new(endpoint,options,&error);
    if(!service)return 4;
    frida_control_service_start_sync(service,NULL,&error);
    if(error)return 4;
    int fd=open(CONTROL_DIR "/token",O_CREAT|O_TRUNC|O_WRONLY|O_CLOEXEC|O_NOFOLLOW,0600);
    if(fd<0||write(fd,token,64)!=64)return 4;
    close(fd);
    loop=g_main_loop_new(NULL,FALSE);unsigned idle=0;
    signal(SIGTERM,signal_stop);signal(SIGHUP,signal_stop);signal(SIGINT,signal_stop);
    g_timeout_add_seconds(1,broker_idle,&idle);g_main_loop_run(loop);
    unlink(CONTROL_DIR "/token");
    frida_control_service_stop_sync(service,NULL,&error);
    _exit(error?5:0);
}

static FridaDevice *shared_device(FridaDeviceManager *manager,GError **error) {
    if(mkdir(CONTROL_DIR,0700)&&errno!=EEXIST)return NULL;
    struct stat st;
    if(lstat(CONTROL_DIR,&st)||!S_ISDIR(st.st_mode)||st.st_uid||(st.st_mode&077))return NULL;
    int owner=open(CONTROL_DIR "/owner",O_CREAT|O_RDWR|O_CLOEXEC|O_NOFOLLOW,0600);
    if(owner<0)return NULL;
    if(!flock(owner,LOCK_EX|LOCK_NB)) {
        unlink(CONTROL_DIR "/token");flock(owner,LOCK_UN);
        posix_spawn_file_actions_t actions;posix_spawn_file_actions_init(&actions);
        for(int i=0;i<3;i++)posix_spawn_file_actions_addopen(&actions,i,"/dev/null",i?O_WRONLY:O_RDONLY,0);
        char *args[]={"qft-eye-broker","--eye-broker",NULL};pid_t child;
        int result=posix_spawn(&child,"/proc/self/exe",&actions,NULL,args,environ);
        posix_spawn_file_actions_destroy(&actions);
        if(result){close(owner);return NULL;}
    }
    close(owner);
    gint64 deadline=g_get_monotonic_time()+10000000;
    do {
        char token[65]={0};int fd=open(CONTROL_DIR "/token",O_RDONLY|O_CLOEXEC|O_NOFOLLOW);
        ssize_t length=fd<0?-1:read(fd,token,65);if(fd>=0)close(fd);
        if(length==64) {
            FridaRemoteDeviceOptions *options=frida_remote_device_options_new();
            frida_remote_device_options_set_token(options,token);
            FridaDevice *device=frida_device_manager_add_remote_device_sync(manager,CONTROL_ADDRESS,options,NULL,error);
            g_object_unref(options);
            if(device)return device;
            g_clear_error(error);
        }
        usleep(50000);
    } while(g_get_monotonic_time()<deadline);
    return NULL;
}

static int put(const char *path,const char *text,int append) {
    int fd=open(path,O_WRONLY|O_CLOEXEC|(append?O_APPEND:0));
    if(fd<0)return -1;
    size_t length=strlen(text);
    int result=write(fd,text,length)==(ssize_t)length?0:-1;
    close(fd);return result;
}
static int release_trace(void) {
    if(trace_fd>=0){close(trace_fd);trace_fd=-1;}
    if(access(INSTANCE,F_OK))return 0;
    put(INSTANCE "/tracing_on","0",0);
    const char *names[]={"detector_output","detector_output_secondary"};
    for(int i=0;i<2;i++) {
        char path[256],event[128];
        snprintf(path,sizeof path,INSTANCE "/events/" GROUP "/%s/enable",names[i]);
        put(path,"0",0);
        snprintf(event,sizeof event,"-:" GROUP "/%s\n",names[i]);
        put(TRACE "/uprobe_events",event,1);
    }
    for(int i=0;i<8;i++) {
        put(INSTANCE "/free_buffer","1",0);
        if(!rmdir(INSTANCE)||errno==ENOENT){traced=0;return 0;}
        usleep(150000);
    }
    return -1;
}
static int start_trace(void) {
    if(mkdir(INSTANCE,0700))return -1;
    traced=1;
    if(put(INSTANCE "/trace_clock","mono",0))return -1;
    const unsigned offsets[]={0xb1f470,0xb1f4e4};
    const char *names[]={"detector_output","detector_output_secondary"};
    for(int i=0;i<2;i++) {
        char probe[512],path[256];
        snprintf(probe,sizeof probe,"p:" GROUP "/%s /odm/lib64/libtrackingengines.so:0x%x "
            "x=+0x300(%%x20):x32 y=+0x304(%%x20):x32 z=+0x308(%%x20):x32 "
            "tag=+0x0(%%x20):x8 valid=+0x30c(%%x20):x8\n",names[i],offsets[i]);
        if(put(TRACE "/uprobe_events",probe,1))return -1;
        snprintf(path,sizeof path,INSTANCE "/events/" GROUP "/%s/enable",names[i]);
        if(put(path,"1",0))return -1;
    }
    trace_fd=open(INSTANCE "/trace_pipe",O_RDONLY|O_CLOEXEC|O_NONBLOCK);
    return trace_fd<0?-1:put(INSTANCE "/tracing_on","1",0);
}
static void request_stop(void) {
    if(stopping)return;
    stopping=1;stop_at=g_get_monotonic_time();
    if(traced&&release_trace())failed=1;
    if(script)frida_script_post(script,"{\"type\":\"qft-stop\"}",NULL);
    else g_main_loop_quit(loop);
}
static void signal_stop(int number) { (void)number;interrupted=1; }
static void on_detached(FridaSession *session,FridaSessionDetachReason reason,FridaCrash *crash,gpointer data) {
    (void)data;
    if(detaching)return;
    fprintf(stderr,"EYE_HOST lost pid=%u reason=%d\n",frida_session_get_pid(session),(int)reason);
    if(crash)fprintf(stderr,"EYE_CRASH %s\n%s\n",frida_crash_get_summary(crash),frida_crash_get_report(crash));
    detached=1;failed=1;puts("QFT_EYES_ERROR service_changed");g_main_loop_quit(loop);
}
static void on_message(FridaScript *sender,const gchar *message,GBytes *bytes,gpointer data) {
    (void)sender;(void)bytes;(void)data;
    JsonParser *parser=json_parser_new();
    if(!json_parser_load_from_data(parser,message,-1,NULL)){failed=1;request_stop();goto done;}
    JsonObject *root=json_node_get_object(json_parser_get_root(parser));
    const char *type=json_object_get_string_member_with_default(root,"type","");
    if(!strcmp(type,"send")) {
        JsonObject *payload=json_object_get_object_member(root,"payload");
        const char *event=json_object_get_string_member_with_default(payload,"event","");
        if(!strcmp(event,"ready")) {
            if(stopping)goto done;
            if(!averaging&&start_trace()){failed=1;puts("QFT_EYES_ERROR trace");request_stop();}
            else {ready=1;alive_at=g_get_monotonic_time();unlock_attachment();puts("QFT_EYES_READY");}
        } else if(averaging&&!strcmp(event,"applied")) {
            gint64 samples=json_object_get_int_member_with_default(payload,"samples",0);
            if(samples<1||samples>25){failed=1;request_stop();}
            else printf("QFT_AVERAGING_APPLIED %d\n",(int)samples);
        } else if(!strcmp(event,"alive")) {
            alive_at=g_get_monotonic_time();
        } else if(!strcmp(event,"restored")) {
            restored=1;puts("QFT_EYES_RESTORED");g_main_loop_quit(loop);
        } else if(!strcmp(event,"error")) {
            fprintf(stderr,"EYE_AGENT %s\n",message);
            failed=1;puts("QFT_EYES_ERROR model");request_stop();
        } else fprintf(stderr,"EYE_AGENT %s\n",message);
    } else if(!strcmp(type,"error")) {
        fprintf(stderr,"EYE_AGENT %s\n",message);
        failed=1;puts("QFT_EYES_ERROR agent");request_stop();
    }
done:
    fflush(stdout);g_object_unref(parser);
}
static void control_input(const char *input,ssize_t count) {
    for(ssize_t i=0;i<count;i++) {
        if(input[i]=='\n') {
            commands[command_length]=0;
            char *end=NULL;long value=0;
            if(!strncmp(commands,"samples:",8))value=strtol(commands+8,&end,10);
            if(!end||end==commands+8||*end||value<1||value>25) {failed=1;puts("QFT_EYES_ERROR command");request_stop();return;}
            char message[96];snprintf(message,sizeof message,"{\"type\":\"qft-averaging\",\"payload\":%ld}",value);
            frida_script_post(script,message,NULL);command_length=0;
        } else if(command_length<sizeof commands-1)commands[command_length++]=input[i];
        else {failed=1;puts("QFT_EYES_ERROR command");request_stop();return;}
    }
}
static gboolean poll_session(gpointer data) {
    (void)data;
    char input[32];ssize_t count=read(STDIN_FILENO,input,sizeof input);
    if(count==0||interrupted)request_stop();
    else if(averaging&&count>0&&!stopping)control_input(input,count);
    gint64 now=g_get_monotonic_time();
    if(script&&now-heartbeat_at>1000000) {
        heartbeat_at=now;frida_script_post(script,"{\"type\":\"qft-heartbeat\"}",NULL);
    }
    if(!ready&&!stopping&&now-started>20000000){failed=1;puts("QFT_EYES_ERROR timeout");request_stop();}
    if(stopping&&now-stop_at>12000000){failed=1;g_main_loop_quit(loop);return G_SOURCE_REMOVE;}
    if(trace_fd>=0&&!stopping&&now-alive_at<2000000) {
        char buffer[32768];
        count=read(trace_fd,buffer,sizeof buffer);
        if(count>0)fwrite(buffer,1,count,stdout);
        else if(count<0&&errno!=EAGAIN&&errno!=EINTR){failed=1;request_stop();}
    }
    return G_SOURCE_CONTINUE;
}
static gchar *configuration(const char *library,const char *payload_path,const char *status_path) {
    gchar *payload=NULL;gsize length=0;
    if(!g_file_get_contents(payload_path,&payload,&length,NULL)||length!=374634)return NULL;
    gchar *hash=g_compute_checksum_for_data(G_CHECKSUM_SHA256,(guchar*)payload,length);
    int valid=!strcmp(hash,"70847c7fa4beb32cfdb923484336eeaaca8d2f402e85338e3766394ec6ecd014");g_free(hash);
    if(!valid){g_free(payload);return NULL;}
    guint32 graph_length,binary_length;
    memcpy(&graph_length,payload+8,4);memcpy(&binary_length,payload+12,4);
    if(memcmp(payload,"QFTEYE01",8)||graph_length!=108564||binary_length!=266054){g_free(payload);return NULL;}
    gchar *graph=g_strndup(payload+16,graph_length),*binary=g_malloc(binary_length*2+1);
    for(guint i=0;i<binary_length;i++)sprintf(binary+i*2,"%02x",(guchar)payload[16+graph_length+i]);
    JsonBuilder *builder=json_builder_new();json_builder_begin_object(builder);
    json_builder_set_member_name(builder,"owner");json_builder_add_int_value(builder,getpid());
    json_builder_set_member_name(builder,"status");json_builder_add_string_value(builder,status_path);
    json_builder_set_member_name(builder,"library");json_builder_add_string_value(builder,library);
    json_builder_set_member_name(builder,"json");json_builder_add_string_value(builder,graph);
    json_builder_set_member_name(builder,"binary");json_builder_add_string_value(builder,binary);
    json_builder_end_object(builder);
    JsonNode *node=json_builder_get_root(builder);JsonGenerator *generator=json_generator_new();json_generator_set_root(generator,node);
    gchar *result=json_generator_to_data(generator,NULL);
    json_node_free(node);g_object_unref(generator);g_object_unref(builder);g_free(graph);g_free(binary);g_free(payload);return result;
}
int qft_eye_session_main(int argc,char **argv) {
    if(argc!=4||geteuid()!=0)return 2;
    averaging=!strcmp(argv[1],"--averaging");
    long sample_count=25;
    if(averaging) {
        char *end=NULL;sample_count=strtol(argv[3],&end,10);
        if(end==argv[3]||*end||sample_count<1||sample_count>25){puts("QFT_EYES_ERROR command");return 2;}
    }
    frida_init();
    setvbuf(stdout,NULL,_IOLBF,0);
    int lock=open(averaging?"/data/local/tmp/qft-meta-gaze.lock":"/data/local/tmp/qft-live-eyes.lock",O_CREAT|O_RDWR|O_CLOEXEC|O_NOFOLLOW,0600);
    if(lock<0||flock(lock,LOCK_EX|LOCK_NB)){puts("QFT_EYES_ERROR busy");return 3;}
    if(ftruncate(lock,0)||dprintf(lock,"%d\n",getpid())<0)return 3;
    FILE *mounts=fopen("/proc/mounts","re");char line[1024];int legacy=0;
    if(!mounts)return 3;
    while(fgets(line,sizeof line,mounts))if(strstr(line," /odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl "))legacy=1;
    fclose(mounts);
    if(!averaging&&(legacy||!access(TRACE "/instances/qft_headset_eyes",F_OK))){puts("QFT_EYES_ERROR busy");return 3;}
    gchar *engine=NULL;gsize size=0;
    if(!g_file_get_contents("/odm/lib64/libtrackingengines.so",&engine,&size,NULL))return 4;
    gchar *hash=g_compute_checksum_for_data(G_CHECKSUM_SHA256,(guchar*)engine,size);g_free(engine);
    int supported=!strcmp(hash,"0fb6f54a3e190bec791d757ea18d32a8ecc1af4a861992d04b1703c93293cd03");g_free(hash);
    if(!supported){puts("QFT_EYES_ERROR unsupported");return 4;}
    pid_t pid;if(find_pids("trackingservice",&pid,1)!=1){puts("QFT_EYES_ERROR service");return 4;}
    char status_path[128],process_path[64];struct stat process;
    snprintf(status_path,sizeof status_path,"/data/local/tmp/qft-eye-status-%d",getpid());
    snprintf(process_path,sizeof process_path,"/proc/%d",pid);
    if(stat(process_path,&process))return 4;
    int status=averaging?-1:open(status_path,O_CREAT|O_TRUNC|O_WRONLY|O_CLOEXEC|O_NOFOLLOW,0600);
    if(!averaging&&(status<0||fchown(status,process.st_uid,process.st_gid))){puts("QFT_EYES_ERROR payload");return 4;}
    if(status>=0)close(status);printf("QFT_EYES_OWNER %d\n",getpid());
    gchar *config=averaging?g_strdup_printf("{\"samples\":%ld}",sample_count):configuration(argv[1],argv[3],status_path),*source=NULL;
    if(!config||!g_file_get_contents(argv[2],&source,&size,NULL)){puts("QFT_EYES_ERROR payload");unlink(status_path);return 4;}
    if(!averaging&&release_trace()){puts("QFT_EYES_ERROR trace_cleanup");return 4;}
    gchar *agent=g_strconcat("const qftEyeConfig = ",config,";\n",source,NULL);g_free(config);g_free(source);
    // Both features share one injector so their agent lifetimes cannot conflict in Meta's process.
    if(lock_attachment()){puts("QFT_EYES_ERROR busy");return 3;}
    pid_t current;
    if(find_pids("trackingservice",&current,1)!=1||current!=pid){puts("QFT_EYES_ERROR service");return 4;}
    loop=g_main_loop_new(NULL,FALSE);GError *error=NULL;
    FridaDeviceManager *manager=frida_device_manager_new();
    FridaDevice *device=shared_device(manager,&error);
    fprintf(stderr,"EYE_HOST attaching pid=%d mode=%s\n",pid,averaging?"averaging":"eyes");
    FridaSession *session=device?frida_device_attach_sync(device,pid,NULL,NULL,&error):NULL;
    if(session) {
        fprintf(stderr,"EYE_HOST attached\n");
        g_signal_connect(session,"detached",G_CALLBACK(on_detached),NULL);
        script=frida_session_create_script_sync(session,agent,NULL,NULL,&error);
    }
    g_free(agent);
    if(script) {
        g_signal_connect(script,"message",G_CALLBACK(on_message),NULL);
        frida_script_load_sync(script,NULL,&error);
    }
    if(error||!script){fprintf(stderr,"EYE_ATTACH %s\n",error?error->message:"failed");failed=1;}
    else {
        signal(SIGTERM,signal_stop);signal(SIGHUP,signal_stop);signal(SIGINT,signal_stop);
        fcntl(STDIN_FILENO,F_SETFL,fcntl(STDIN_FILENO,F_GETFL)|O_NONBLOCK);
        started=g_get_monotonic_time();g_timeout_add(20,poll_session,NULL);g_main_loop_run(loop);
    }
    if(traced&&release_trace())failed=1;
    unlock_attachment();
    detaching=1;
    fprintf(stderr,"EYE_HOST cleanup restored=%d failed=%d\n",restored,failed);
    g_clear_error(&error);
    int cleanup_locked=detached?0:!lock_attachment();
    if(!detached&&!cleanup_locked){failed=1;fprintf(stderr,"EYE_HOST cleanup lock timed out\n");}
    if(script&&!detached&&cleanup_locked)frida_script_unload_sync(script,NULL,&error);
    fprintf(stderr,"EYE_HOST unloaded\n");
    if(error){failed=1;g_clear_error(&error);}
    if(session&&!detached&&cleanup_locked)frida_session_detach_sync(session,NULL,&error);
    fprintf(stderr,"EYE_HOST detached\n");
    if(error)failed=1;
    unlock_attachment();
    if(!restored&&!detached)failed=1;
    puts("QFT_EYES_CLOSED");
    unlink(status_path);
    fflush(stdout);flock(lock,LOCK_UN);close(lock);
    _exit(failed?5:0);
}
#ifndef QFT_EYE_SESSION_LIBRARY
int main(int argc,char **argv) {
    if(argc==2&&!strcmp(argv[1],"--eye-broker"))return qft_eye_broker_main();
    return qft_eye_session_main(argc,argv);
}
#endif
