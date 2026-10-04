package com.qftplus.headset;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
public final class BootReceiver extends BroadcastReceiver {
    public void onReceive(Context context, Intent intent) {
        if (Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()) &&
                context.getSharedPreferences("settings", 0).getBoolean("boot", false) &&
                context.getSharedPreferences("settings", 0).getBoolean("enabled", false))
            context.startForegroundService(new Intent(context, TrackingService.class));
    }
}
