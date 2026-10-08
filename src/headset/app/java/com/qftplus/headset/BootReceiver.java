package com.qftplus.headset;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
public final class BootReceiver extends BroadcastReceiver {
    public void onReceive(Context context, Intent intent) {
        android.content.SharedPreferences settings = context.getSharedPreferences("settings", 0);
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()) || !settings.getBoolean("enabled", false)) return;
        if (settings.getBoolean("boot", false)) context.startForegroundService(new Intent(context, TrackingService.class));
        else if (!TrackingService.running) settings.edit().putBoolean("enabled", false).apply();
    }
}
