#!/system/bin/sh
set -eu
target=/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl
property=persist.device_config.oculus_shared_vision.oculus_eyetracking_enable_experimental_model
trace=/sys/kernel/tracing
group=qft_headset_eyes
instance=$trace/instances/$group
work=/data/local/tmp/qft-headset-convergence
source=$1
script=$0
fail() { echo "QFT_CONVERGENCE_ERROR: $*"; exit 1; }
mounted=0
traced=0
reader=
before=
restore() {
    if [ "$traced" = 1 ]; then
        echo 0 > "$instance/tracing_on"
        for event in detector_output detector_output_secondary; do
            [ ! -e "$instance/events/$group/$event/enable" ] || echo 0 > "$instance/events/$group/$event/enable"
        done
    fi
    if [ -n "$reader" ]; then kill "$reader" 2>/dev/null; wait "$reader" 2>/dev/null; fi
    if [ "$traced" = 1 ]; then
        for event in detector_output detector_output_secondary; do
            if grep -q "p:$group/$event " "$trace/uprobe_events"; then echo "-:$group/$event" >> "$trace/uprobe_events" || result=1; fi
        done
        if [ -d "$instance" ]; then
            for attempt in 1 2 3 4 5 6 7 8; do
                echo 1 > "$instance/free_buffer"
                rmdir "$instance" 2>/dev/null && break
                sleep .15
            done
            [ ! -d "$instance" ] || result=1
        fi
    fi
    if [ "$mounted" = 1 ]; then
        stop trackingservice
        setprop "$property" "$before" || result=1
        umount "$target" || result=1
        start trackingservice || result=1
    fi
    if { [ "$traced" = 0 ] || [ ! -d "$instance" ]; } && { [ "$mounted" = 0 ] || ! grep -qF " $target " /proc/mounts; }; then
        rm -f "$work/bolt.ptl" "$work/pid" "$work/property"; rmdir "$work"
    else echo 'QFT_CONVERGENCE_ERROR: Could not restore the eye tracking resources'; result=1; fi
}
cleanup() {
    code=$?
    trap - EXIT HUP INT TERM
    set +e
    result=$code
    restore
    rm -f "$source" "$script"
    rmdir "${source%/*}" 2>/dev/null
    exit "$result"
}
if ! mkdir "$work" 2>/dev/null; then
    pid=$(cat "$work/pid" 2>/dev/null) || pid=
    if [ -n "$pid" ] && grep -q convergence "/proc/$pid/cmdline" 2>/dev/null; then fail 'Another convergence session is still active'; fi
    set +e
    result=0
    before=$(cat "$work/property" 2>/dev/null)
    [ ! -d "$instance" ] || traced=1
    if [ -e "$work/bolt.ptl" ] && grep -qF " $target " /proc/mounts; then mounted=1
    elif [ -e "$work/property" ] && [ "$(getprop "$property")" != "$before" ]; then
        stop trackingservice; setprop "$property" "$before"; start trackingservice
    fi
    restore
    set -e
    [ "$result" = 0 ] || fail 'Could not restore the previous convergence session'
    traced=0; mounted=0
    mkdir "$work" || fail 'Another convergence session is still active'
fi
echo $$ > "$work/pid"
before=$(getprop "$property")
echo "$before" > "$work/property"
trap cleanup EXIT HUP INT TERM
grep -qF " $target " /proc/mounts && fail 'The eye model is in use by another app'
[ ! -d "$instance" ] || fail 'The convergence trace is already in use'
cp "$1" "$work/bolt.ptl"
chown root:root "$work/bolt.ptl"
chmod 644 "$work/bolt.ptl"
chcon u:object_r:vendor_configs_file:s0 "$work/bolt.ptl"
mkdir "$instance"
traced=1
echo mono > "$instance/trace_clock"
echo "p:$group/detector_output /odm/lib64/libtrackingengines.so:0x$2 $4" >> "$trace/uprobe_events"
echo "p:$group/detector_output_secondary /odm/lib64/libtrackingengines.so:0x$3 $4" >> "$trace/uprobe_events"
for event in detector_output detector_output_secondary; do echo 1 > "$instance/events/$group/$event/enable"; done
echo 1 > "$instance/tracing_on"
mount --bind "$work/bolt.ptl" "$target"
mounted=1
setprop "$property" true
stop trackingservice
start trackingservice
sleep 2
cat "$instance/trace_pipe" &
reader=$!
echo QFT_CONVERGENCE_READY
read -r unused || true
