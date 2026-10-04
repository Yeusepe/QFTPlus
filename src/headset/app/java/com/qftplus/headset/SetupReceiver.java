package com.qftplus.headset;
import android.content.*;

public final class SetupReceiver extends BroadcastReceiver {
    public void onReceive(Context context, Intent intent) {
        String host=intent.getStringExtra("host");int port=intent.getIntExtra("port",27276);
        String key=intent.getStringExtra("key");
        if(host==null||!host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}")||port<1||port>65535||key==null||!key.matches("[0-9a-f]{64}")){setResultCode(2);return;}
        for(String octet:host.split("\\."))if(Integer.parseInt(octet)>255){setResultCode(2);return;}
        context.getSharedPreferences("settings",0).edit().putString("host",host).putInt("port",port).putString("key",key)
            .putBoolean("enabled",intent.getBooleanExtra("enabled",true)).putBoolean("boot",true)
            .putBoolean("pupils",intent.getBooleanExtra("pupils",false)).putBoolean("tongue",intent.getBooleanExtra("tongue",false)).commit();
        setResultCode(0);setResultData("QFT_PAIRED");
    }
}
