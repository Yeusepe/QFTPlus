#!/system/bin/sh
set -eu
MOD=/data/adb/modules/quest-memtrack-cache
TARGET=/vendor/bin/hw/vendor.qti.hardware.memtrack-service
STOCK=e26437d66ab966b5d510a720fd657a8299f46e413588b684e82a67f33471915f
FIXED=244861555967833f00b8689f92d612371ca07113d2db90410f3cdf7adda08d21
result() { echo "QFT_MIC_REPAIR $1"; }
file_hash() { sha256sum "$1" 2>/dev/null | cut -d ' ' -f 1; }
if [ "$(id -u)" != 0 ] || [ ! -x /data/adb/magisk/busybox ] || [ ! -d /data/adb/modules ]; then
    result root_required
    exit 1
fi
if [ -e "$MOD" ] && ! grep -qx 'id=quest-memtrack-cache' "$MOD/module.prop"; then
    result conflict
    exit 1
fi
CURRENT=$(file_hash "$TARGET")
status() {
    ENABLED=false
    if [ -f "$MOD/module.prop" ] && [ ! -e "$MOD/disable" ] && [ ! -e "$MOD/remove" ]; then ENABLED=true; fi
    case "$CURRENT" in
        "$STOCK"|"$FIXED") ;;
        *) if $ENABLED; then result unsupported_enabled; else result unsupported; fi; return ;;
    esac
    PID=$(pidof vendor.qti.hardware.memtrack-service || true)
    RUNNING=$(file_hash "/proc/$PID/exe")
    if $ENABLED; then
        if [ "$(file_hash "$MOD/system/vendor/bin/hw/vendor.qti.hardware.memtrack-service")" != "$FIXED" ]; then
            result damaged
        elif [ "$RUNNING" = "$FIXED" ]; then result active
        else result pending_on; fi
    elif [ "$RUNNING" = "$FIXED" ]; then result pending_off
    else result off; fi
}
case "$action" in
    status) status; exit 0 ;;
    disable)
        if [ -d "$MOD" ]; then touch "$MOD/disable"; fi
        status
        exit 0 ;;
    enable) ;;
    *) exit 1 ;;
esac
case "$CURRENT" in "$STOCK"|"$FIXED") ;; *) result unsupported; exit 1 ;; esac
STAGE=$(mktemp -d /data/adb/qft-microphone.XXXXXX)
cleanup() {
    if [ ! -e "$MOD" ] && [ -d "$STAGE/previous" ]; then mv "$STAGE/previous" "$MOD"; fi
    rm -rf "$STAGE"
}
trap cleanup EXIT
NEXT="$STAGE/next"
mkdir -p "$NEXT/system/vendor/bin/hw"
cp "$TARGET" "$NEXT/original"
# Reconstruct stock when an earlier installation is already mounted.
if [ "$CURRENT" = "$FIXED" ]; then
    printf '\005' | dd of="$NEXT/original" bs=1 seek=19245 count=1 conv=notrunc 2>/dev/null
fi
[ "$(file_hash "$NEXT/original")" = "$STOCK" ]
PATCH="$NEXT/system/vendor/bin/hw/vendor.qti.hardware.memtrack-service"
cp "$NEXT/original" "$PATCH"
# cmp w8,#1 -> cmp w8,#0: accept zero owners, still reject negative counts.
printf '\001' | dd of="$PATCH" bs=1 seek=19245 count=1 conv=notrunc 2>/dev/null
[ "$(file_hash "$PATCH")" = "$FIXED" ]
printf 'id=quest-memtrack-cache\nname=%s\nversion=1.1\nversionCode=2\nauthor=QFT+\ndescription=%s\n' "$module_name" "$module_description" > "$NEXT/module.prop"
cat > "$NEXT/post-fs-data.sh" <<'BOOT'
#!/system/bin/sh
MODDIR=${0%/*}
TARGET=/vendor/bin/hw/vendor.qti.hardware.memtrack-service
PATCH="$MODDIR/system/vendor/bin/hw/vendor.qti.hardware.memtrack-service"
# Magisk runs this before mounting modules. Refuse changed firmware or a damaged patch.
touch "$MODDIR/skip_mount"
if [ "$(sha256sum "$TARGET" 2>/dev/null | cut -d ' ' -f 1)" = e26437d66ab966b5d510a720fd657a8299f46e413588b684e82a67f33471915f ] &&
   [ "$(sha256sum "$PATCH" 2>/dev/null | cut -d ' ' -f 1)" = 244861555967833f00b8689f92d612371ca07113d2db90410f3cdf7adda08d21 ]; then
    rm -f "$MODDIR/skip_mount"
fi
BOOT
chown -R 0:0 "$NEXT"
find "$NEXT" -type d -exec chmod 0755 '{}' \;
chmod 0644 "$NEXT/module.prop"
chmod 0755 "$NEXT/post-fs-data.sh" "$NEXT/original" "$PATCH"
chown 0:2000 "$NEXT/original" "$PATCH"
chcon u:object_r:hal_memtrack_default_exec:s0 "$NEXT/original" "$PATCH"
if [ -e "$MOD" ]; then mv "$MOD" "$STAGE/previous"; fi
mv "$NEXT" "$MOD"
status
