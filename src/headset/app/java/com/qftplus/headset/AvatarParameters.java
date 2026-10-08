package com.qftplus.headset;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

final class AvatarParameters implements AutoCloseable {
    final Map<String, Character> present = new ConcurrentHashMap<>();
    volatile int avatar;
    volatile boolean heard;
    volatile boolean learning;
    volatile byte[] watch; volatile int watchBits; volatile long watchSent;
    long echoTotal; int echoCount;
    private static final byte[] CHANGE = "/avatar/change\0".getBytes(StandardCharsets.US_ASCII);
    private final NsdManager nsd;
    private final DatagramSocket osc;
    private final ServerSocket http;
    private final String name = "QFTPlus-" + Long.toHexString(System.nanoTime() & 0xffffff).toUpperCase();
    private final NsdManager.RegistrationListener[] registrations = new NsdManager.RegistrationListener[2];
    private volatile boolean closed;

    AvatarParameters(Context context, InetAddress vrchat) throws IOException {
        nsd = context.getSystemService(NsdManager.class);
        InetAddress local;
        try (DatagramSocket route = new DatagramSocket()) { route.connect(vrchat, 9); local = route.getLocalAddress(); }
        osc = new DatagramSocket(0); http = new ServerSocket(0);
        String hostInfo = "{\"NAME\":\"" + name + "\",\"OSC_IP\":\"" + local.getHostAddress() + "\",\"OSC_PORT\":" + osc.getLocalPort()
            + ",\"OSC_TRANSPORT\":\"UDP\",\"EXTENSIONS\":{\"ACCESS\":true,\"VALUE\":true}}";
        String tree = "{\"FULL_PATH\":\"/\",\"ACCESS\":0,\"CONTENTS\":{\"avatar\":{\"FULL_PATH\":\"/avatar\",\"ACCESS\":0,\"CONTENTS\":"
            + "{\"change\":{\"FULL_PATH\":\"/avatar/change\",\"ACCESS\":2,\"TYPE\":\"s\"}}}}}";
        Thread server = new Thread(() -> serve(hostInfo, tree), "qft-oscquery"); server.setDaemon(true); server.start();
        Thread receiver = new Thread(this::receive, "qft-avatar-parameters"); receiver.setDaemon(true); receiver.start();
        register("_oscjson._tcp", http.getLocalPort(), 0);
        register("_osc._udp", osc.getLocalPort(), 1);
    }
    private void register(String type, int port, int slot) {
        NsdServiceInfo info = new NsdServiceInfo(); info.setServiceName(name); info.setServiceType(type); info.setPort(port);
        registrations[slot] = new NsdManager.RegistrationListener() {
            public void onServiceRegistered(NsdServiceInfo i) { }
            public void onRegistrationFailed(NsdServiceInfo i, int error) { android.util.Log.w("QFT", "OSCQuery registration failed " + error); }
            public void onServiceUnregistered(NsdServiceInfo i) { }
            public void onUnregistrationFailed(NsdServiceInfo i, int error) { }
        };
        try { nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, registrations[slot]); } catch (RuntimeException e) { registrations[slot] = null; }
    }
    private void serve(String hostInfo, String tree) {
        while (!closed) try (Socket client = http.accept()) {
            client.setSoTimeout(2000);
            InputStream in = client.getInputStream(); byte[] request = new byte[2048]; int n = in.read(request);
            String line = n > 0 ? new String(request, 0, n, StandardCharsets.US_ASCII).split("\r\n", 2)[0] : "";
            byte[] body = (line.contains("HOST_INFO") ? hostInfo : tree).getBytes(StandardCharsets.UTF_8);
            OutputStream out = client.getOutputStream();
            out.write(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.length + "\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
            out.write(body); out.flush();
        } catch (IOException ignored) { }
    }
    private void receive() {
        byte[] buffer = new byte[65535];
        DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
        while (!closed) try {
            packet.setLength(buffer.length); osc.receive(packet);
            heard = true;
            read(buffer, 0, packet.getLength());
        } catch (IOException | RuntimeException ignored) { }
    }
    private void read(byte[] b, int at, int length) {
        if (length >= 16 && b[at] == '#') {
            for (int i = at + 16; i + 4 <= at + length; ) {
                int size = big(b, i);
                if (size <= 0 || size > at + length - i - 4) return;
                read(b, i + 4, size); i += 4 + size;
            }
            return;
        }
        byte[] w = watch;
        if (w != null && length >= w.length + 8 && starts(b, at, w) && b[at + w.length] == ',' && b[at + w.length + 1] == 'f'
                && big(b, at + w.length + 4) == watchBits) {
            synchronized (this) { echoTotal += System.nanoTime() - watchSent; echoCount++; }
            watch = null;
        }
        if (!learning && (length < CHANGE.length || !starts(b, at, CHANGE))) return;
        int addressEnd = end(b, at, length), tagsAt = (addressEnd + 4) & ~3, tagsEnd = end(b, tagsAt, at + length - tagsAt);
        String address = new String(b, at, addressEnd - at, StandardCharsets.US_ASCII);
        char type = tagsEnd - tagsAt > 1 ? (char)b[tagsAt + 1] : 0;
        if (address.equals("/avatar/change")) { present.clear(); avatar++; }
        else if (address.startsWith("/avatar/parameters/") && type != 0) present.put(address.substring(19), type == 'F' ? 'T' : type);
    }
    private static int big(byte[] b, int at) { return (b[at] & 255) << 24 | (b[at + 1] & 255) << 16 | (b[at + 2] & 255) << 8 | (b[at + 3] & 255); }
    private static boolean starts(byte[] b, int at, byte[] prefix) {
        for (int i = 0; i < prefix.length; i++) if (b[at + i] != prefix[i]) return false;
        return true;
    }
    private static int end(byte[] b, int at, int length) {
        int i = at; while (i < at + length && b[i] != 0) i++;
        return i;
    }
    @Override public void close() {
        closed = true;
        for (NsdManager.RegistrationListener r : registrations) if (r != null) try { nsd.unregisterService(r); } catch (RuntimeException ignored) { }
        osc.close(); try { http.close(); } catch (IOException ignored) { }
    }
}
