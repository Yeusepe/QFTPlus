package com.qftplus.headset;

import android.content.Context;
import android.net.nsd.*;
import android.os.*;
import org.json.JSONObject;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;

final class OscDiscovery implements AutoCloseable {
    static final class Destination {
        final String name, host, service, type;
        final int port;
        Destination(String name, String host, int port, String service, String type) {
            this.name = name; this.host = host; this.port = port; this.service = service; this.type = type;
        }
        String id() { return type + "/" + service; }
    }
    interface Listener { void changed(List<Destination> destinations); }
    private final NsdManager manager;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService http = Executors.newSingleThreadExecutor();
    private final ExecutorService listening = Executors.newSingleThreadExecutor();
    private final Map<String, InetAddress> responders = new HashMap<>();
    private final android.net.wifi.WifiManager.MulticastLock multicast;
    private final List<NsdManager.DiscoveryListener> searches = new ArrayList<>();
    private final Map<String, Destination> found = new LinkedHashMap<>();
    private final Set<String> present = new HashSet<>();
    private final ArrayDeque<NsdServiceInfo> pending = new ArrayDeque<>();
    private final Listener listener;
    private boolean resolving, closed;
    OscDiscovery(Context context, Listener listener) {
        manager = context.getSystemService(NsdManager.class); this.listener = listener;
        multicast = context.getSystemService(android.net.wifi.WifiManager.class).createMulticastLock("qft-osc-discovery");
        multicast.setReferenceCounted(false); multicast.acquire();
        listening.execute(this::listenForResponders);
        for (String type : new String[]{"_osc._udp.", "_oscjson._tcp."}) {
            NsdManager.DiscoveryListener search = new NsdManager.DiscoveryListener() {
                public void onDiscoveryStarted(String t) { }
                public void onDiscoveryStopped(String t) { }
                public void onStartDiscoveryFailed(String t, int error) { }
                public void onStopDiscoveryFailed(String t, int error) { }
                public void onServiceFound(NsdServiceInfo info) { main.post(() -> {
                    if (closed) return;
                    String id = id(info); present.add(id); pending.add(info); resolveNext();
                }); }
                public void onServiceLost(NsdServiceInfo info) { main.post(() -> {
                    present.remove(id(info)); found.remove(id(info)); publish();
                }); }
            };
            searches.add(search);
            try { manager.discoverServices(type, NsdManager.PROTOCOL_DNS_SD, search); }
            catch (RuntimeException ignored) { searches.remove(search); }
        }
    }
    private static String id(NsdServiceInfo info) { return info.getServiceType() + "/" + info.getServiceName(); }
    private void publish() { if (!closed) listener.changed(new ArrayList<>(found.values())); }
    private void resolveNext() {
        if (closed || resolving || pending.isEmpty()) return;
        NsdServiceInfo info = pending.remove(); resolving = true;
        try { manager.resolveService(info, new NsdManager.ResolveListener() {
            public void onResolveFailed(NsdServiceInfo service, int error) { main.post(() -> { resolving = false; resolveNext(); }); }
            public void onServiceResolved(NsdServiceInfo service) { main.post(() -> {
                resolving = false; resolveNext();
                InetAddress host = reachable(addresses(service));
                if (closed || host == null || !present.contains(id(info))) return;
                if (service.getServiceType().contains("_oscjson")) {
                    http.execute(() -> {
                        Destination destination = query(service, host);
                        main.post(() -> accept(info, destination));
                    });
                } else accept(info, new Destination(service.getServiceName(), host.getHostAddress(), service.getPort(), info.getServiceName(), info.getServiceType()));
            }); }
        }); } catch (RuntimeException error) { resolving = false; resolveNext(); }
    }
    private Destination reachableFrom(Destination d) {
        InetAddress from = responders.get(d.service);
        try {
            if (from != null && !onThisNetwork(InetAddress.getByName(d.host)) && onThisNetwork(from))
                return new Destination(d.name, from.getHostAddress(), d.port, d.service, d.type);
        } catch (UnknownHostException ignored) { }
        return d;
    }
    private void accept(NsdServiceInfo info, Destination destination) {
        if (!closed && present.contains(id(info)) && destination != null && destination.port > 0 && destination.port <= 65535) {
            found.put(id(info), reachableFrom(destination)); publish();
        }
    }
    private static List<InetAddress> addresses(NsdServiceInfo service) {
        List<InetAddress> all = new ArrayList<>(service.getHostAddresses());
        if (all.isEmpty() && service.getHost() != null) all.add(service.getHost());
        return all;
    }
    static InetAddress reachable(List<InetAddress> addresses) {
        InetAddress best = null; int bestScore = 0;
        for (InetAddress a : addresses) { int score = score(a); if (score > bestScore) { best = a; bestScore = score; } }
        return best;
    }
    private static int score(InetAddress a) {
        if (a.isLoopbackAddress() || a.isAnyLocalAddress() || a.isMulticastAddress()) return 0;
        byte[] b = a.getAddress();
        boolean cgnat = a instanceof Inet4Address && (b[0] & 255) == 100 && (b[1] & 0xC0) == 64;
        int score = cgnat || a.isLinkLocalAddress() ? 1 : a.isSiteLocalAddress() ? 3 : 2;
        return (onThisNetwork(a) ? 10 : 0) + score * 2 + (a instanceof Inet4Address ? 1 : 0);
    }
    private static boolean onThisNetwork(InetAddress a) {
        try {
            for (NetworkInterface n : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!n.isUp() || n.isLoopback()) continue;
                for (InterfaceAddress local : n.getInterfaceAddresses()) {
                    byte[] x = local.getAddress().getAddress(), y = a.getAddress();
                    int bits = local.getNetworkPrefixLength();
                    if (x.length != y.length || bits <= 0) continue;
                    boolean same = true;
                    for (int i = 0; i < bits && same; i++) same = ((x[i / 8] ^ y[i / 8]) & (0x80 >> (i % 8))) == 0;
                    if (same) return true;
                }
            }
        } catch (Exception ignored) { }
        return false;
    }
    private static Destination query(NsdServiceInfo service, InetAddress resolved) {
        HttpURLConnection connection = null;
        try {
            String host = resolved.getHostAddress();
            connection = (HttpURLConnection)new URL("http", host, service.getPort(), "/?HOST_INFO").openConnection();
            connection.setConnectTimeout(1500); connection.setReadTimeout(1500); connection.setInstanceFollowRedirects(false);
            if (connection.getResponseCode() != 200) return null;
            byte[] bytes;
            try (java.io.InputStream input = connection.getInputStream()) { bytes = input.readNBytes(16385); }
            if (bytes.length > 16384) return null;
            JSONObject info = new JSONObject(new String(bytes, StandardCharsets.UTF_8));
            if (!info.optString("OSC_TRANSPORT", "UDP").equalsIgnoreCase("UDP")) return null;
            String advertised = info.optString("OSC_IP", host);
            InetAddress address = InetAddress.getByName(advertised);
            if (reachable(Arrays.asList(address, resolved)) != address) advertised = host;
            return new Destination(info.optString("NAME", service.getServiceName()), advertised, info.getInt("OSC_PORT"), service.getServiceName(), service.getServiceType());
        } catch (Exception ignored) { return null; }
        finally { if (connection != null) connection.disconnect(); }
    }
    private void listenForResponders() {
        try (MulticastSocket socket = new MulticastSocket(null)) {
            socket.setReuseAddress(true); socket.bind(new InetSocketAddress(5353));
            InetAddress group = InetAddress.getByName("224.0.0.251");
            socket.joinGroup(group); socket.setSoTimeout(500);
            java.io.ByteArrayOutputStream query = new java.io.ByteArrayOutputStream();
            query.write(new byte[]{0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0});
            for (String type : new String[]{"_osc._udp.local", "_oscjson._tcp.local"}) {
                for (String label : type.split("[.]")) { byte[] b = label.getBytes(StandardCharsets.UTF_8); query.write(b.length); query.write(b); }
                query.write(new byte[]{0, 0, 12, 0, 1});
            }
            byte[] packet = query.toByteArray(), buffer = new byte[9000];
            long nextQuery = 0;
            while (!closed) {
                if (SystemClock.elapsedRealtime() >= nextQuery) { socket.send(new DatagramPacket(packet, packet.length, group, 5353)); nextQuery = SystemClock.elapsedRealtime() + 5000; }
                DatagramPacket answer = new DatagramPacket(buffer, buffer.length);
                try { socket.receive(answer); } catch (SocketTimeoutException timeout) { continue; }
                InetAddress from = answer.getAddress();
                for (String instance : instances(buffer, answer.getLength())) main.post(() -> {
                    if (closed || from.equals(responders.put(instance, from))) return;
                    for (Map.Entry<String, Destination> e : found.entrySet()) if (e.getValue().service.equals(instance)) e.setValue(reachableFrom(e.getValue()));
                    publish();
                });
            }
        } catch (Exception ignored) { }
    }
    private static List<String> instances(byte[] b, int length) {
        List<String> out = new ArrayList<>();
        try {
            if ((b[2] & 0x80) == 0) return out;
            int at = 12, questions = ((b[4] & 255) << 8) | (b[5] & 255);
            int records = (((b[6] & 255) << 8) | (b[7] & 255)) + (((b[8] & 255) << 8) | (b[9] & 255)) + (((b[10] & 255) << 8) | (b[11] & 255));
            for (int i = 0; i < questions; i++) at = skipName(b, at) + 4;
            for (int i = 0; i < records && at + 10 <= length; i++) {
                at = skipName(b, at);
                int type = ((b[at] & 255) << 8) | (b[at + 1] & 255), size = ((b[at + 8] & 255) << 8) | (b[at + 9] & 255);
                at += 10;
                if (type == 12) { String name = readName(b, at); int end = name.indexOf("._"); if (end > 0) out.add(name.substring(0, end)); }
                at += size;
            }
        } catch (RuntimeException ignored) { }
        return out;
    }
    private static int skipName(byte[] b, int at) {
        while (true) { int n = b[at] & 255; if (n == 0) return at + 1; if ((n & 0xC0) == 0xC0) return at + 2; at += n + 1; }
    }
    private static String readName(byte[] b, int at) {
        StringBuilder name = new StringBuilder();
        for (int jumps = 0; jumps < 16; ) {
            int n = b[at] & 255;
            if (n == 0) break;
            if ((n & 0xC0) == 0xC0) { at = ((n & 0x3F) << 8) | (b[at + 1] & 255); jumps++; continue; }
            if (name.length() > 0) name.append('.');
            name.append(new String(b, at + 1, n, StandardCharsets.UTF_8)); at += n + 1;
        }
        return name.toString();
    }
    public void close() {
        multicast.release(); listening.shutdownNow();
        closed = true; pending.clear();
        for (NsdManager.DiscoveryListener search : searches) try { manager.stopServiceDiscovery(search); } catch (RuntimeException ignored) { }
        searches.clear(); http.shutdownNow();
    }
}
