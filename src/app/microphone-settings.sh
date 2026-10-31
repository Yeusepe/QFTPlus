#!/system/bin/sh
set -eu
MOD=/data/adb/modules/qft-microphone-settings
result() { echo "QFT_MIC_SETTINGS $1 ${noise:-0} ${gate_threshold:--1}"; }
if [ "$(id -u)" != 0 ] || [ ! -d /data/adb/modules ]; then result root_required; exit 1; fi
if [ "$(getprop ro.product.device)" != seacliff ]; then result unsupported; exit 1; fi
if [ -e "$MOD" ] && ! grep -qx 'id=qft-microphone-settings' "$MOD/module.prop"; then result conflict; exit 1; fi
running() {
    pid=$(cat "$MOD/pid" 2>/dev/null || true)
    case "$pid" in ''|*[!0-9]*) return 1 ;; esac
    tr '\000' ' ' 2>/dev/null < "/proc/$pid/cmdline" | grep -Fq "MicrophoneControl $MOD"
}
valid() {
    case "$noise" in 0|1) ;; *) return 1 ;; esac
    case "$gate_threshold" in -1) return 0 ;; ''|*[!0-9]*) return 1 ;; esac
    [ "${#gate_threshold}" -le 7 ] && [ "$gate_threshold" -le 1048575 ]
}
status() {
    noise=$(sed -n 's/^noise=//p' "$MOD/options" 2>/dev/null || true)
    gate_threshold=$(sed -n 's/^gate_threshold=//p' "$MOD/options" 2>/dev/null || true)
    noise=${noise:-0}
    if [ -z "$gate_threshold" ]; then
        gate=$(sed -n 's/^gate=//p' "$MOD/options" 2>/dev/null || true)
        case "$gate" in 1) gate_threshold=0 ;; 0|'') gate_threshold=-1 ;; *) result failed; return ;; esac
    fi
    valid || { result failed; return; }
    if [ -e "$MOD/disable" ] || [ -e "$MOD/remove" ]; then noise=0; gate_threshold=-1; result off
    elif [ "$noise:$gate_threshold" = 0:-1 ]; then result off
    elif running; then
        state=$(cat "$MOD/state" 2>/dev/null || true)
        case "$state" in active|waiting) result "$state" ;; *) result attention ;; esac
    else result attention; fi
}
if [ "$action" = status ]; then status; exit 0; fi
[ "$action" = apply ] || exit 1
valid || { result failed; exit 1; }
if [ "$noise:$gate_threshold" != 0:-1 ]; then
    [ "$(sha256sum /data/local/tmp/qft-microphone-control.new.jar | cut -d ' ' -f 1)" = "$controller_sha256" ] || { result failed; exit 1; }
fi
if running; then
    touch "$MOD/stop"
    for attempt in 1 2 3 4 5 6 7 8 9 10; do running || break; sleep 1; done
    # Older controllers restored settings but left Binder threads running.
    if running && [ "$(cat "$MOD/state" 2>/dev/null)" = off ]; then
        kill "$pid"
        for attempt in 1 2 3; do running || break; sleep 1; done
    fi
    if running; then result failed; exit 1; fi
fi
mkdir -p "$MOD"
chmod 0700 "$MOD"
printf 'id=qft-microphone-settings\nname=%s\nversion=1\nversionCode=1\nauthor=QFT+\ndescription=%s\n' "$module_name" "$module_description" > "$MOD/module.prop"
printf 'noise=%s\ngate_threshold=%s\n' "$noise" "$gate_threshold" > "$MOD/options.new"
mv -f "$MOD/options.new" "$MOD/options"
if [ "$noise:$gate_threshold" = 0:-1 ]; then touch "$MOD/disable"; status; exit 0; fi
cp /data/local/tmp/qft-microphone-control.new.jar "$MOD/control.jar"
rm -f /data/local/tmp/qft-microphone-control.new.jar
chmod 0600 "$MOD/control.jar" "$MOD/options"
touch "$MOD/skip_mount"
cat > "$MOD/service.sh" <<'BOOT'
#!/system/bin/sh
MOD=${0%/*}
while [ "$(getprop sys.boot_completed)" != 1 ]; do sleep 2; done
[ ! -e "$MOD/disable" ] && [ ! -e "$MOD/remove" ] && [ ! -e "$MOD/stop" ] || exit 0
export CLASSPATH="$MOD/control.jar"
exec /system/bin/app_process /system/bin MicrophoneControl "$MOD" > "$MOD/controller.log" 2>&1
BOOT
chmod 0700 "$MOD/service.sh"
rm -f "$MOD/stop" "$MOD/disable" "$MOD/remove" "$MOD/state"
setsid /system/bin/sh "$MOD/service.sh" </dev/null >/dev/null 2>&1 &
for attempt in 1 2 3 4 5; do [ -s "$MOD/state" ] && break; sleep 1; done
status
