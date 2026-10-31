#!/system/bin/sh
module=${0%/*}
[ ! -e "$module/disable" ] || exit 0
while [ "$(getprop sys.boot_completed)" != 1 ]; do sleep 2; done
export CLASSPATH="$module/controller-recovery.jar"
exec app_process /system/bin ControllerRecovery --watch "$module" >>"$module/recovery.log" 2>&1
