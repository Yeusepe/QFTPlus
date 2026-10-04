package com.qftplus.headset;
import java.io.IOException;
import java.net.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

final class HeadsetLink implements AutoCloseable {
    private final DatagramSocket socket = new DatagramSocket();
    private final Mac mac;
    private final byte[] challenge = new byte[16];
    private byte[] session;
    private InetAddress host;
    private final int port;
    private long discovery, received;
    private final byte[] response = new byte[65536];
    volatile String adjustments; volatile long adjustmentsTime;
    HeadsetLink(String key,String initialHost,int port) throws Exception {
        if(!key.matches("[0-9a-f]{64}"))throw new IOException("Enable the experiment in desktop Settings to pair this headset");
        byte[] secret=new byte[32];for(int i=0;i<32;i++)secret[i]=(byte)Integer.parseInt(key.substring(2*i,2*i+2),16);
        mac=Mac.getInstance("HmacSHA256");mac.init(new SecretKeySpec(secret,"HmacSHA256"));
        host=initialHost.isEmpty()?null:InetAddress.getByName(initialHost);this.port=port;
        socket.setBroadcast(true);socket.setSoTimeout(1);new SecureRandom().nextBytes(challenge);
    }
    private byte[] signed(byte[] data) { byte[] packet=Arrays.copyOf(data,data.length+32);System.arraycopy(mac.doFinal(data),0,packet,data.length,32);return packet; }
    private void send(byte[] packet,InetAddress target) throws IOException { socket.send(new DatagramPacket(packet,packet.length,target,port)); }
    boolean connected(long now) { return session!=null && now-received<10000000000L; }
    void poll(long now) throws IOException {
        if(now-discovery>3000000000L) {
            discovery=now;
            byte[] message=new byte[24];System.arraycopy("QFTDISC1".getBytes(StandardCharsets.US_ASCII),0,message,0,8);System.arraycopy(challenge,0,message,8,16);
            byte[] packet=signed(message);
            if(host!=null)send(packet,host);
            for(NetworkInterface network:Collections.list(NetworkInterface.getNetworkInterfaces()))
                if(network.isUp()&&!network.isLoopback())for(InterfaceAddress address:network.getInterfaceAddresses())if(address.getBroadcast()!=null)
                    try{send(packet,address.getBroadcast());}catch(IOException ignored){}
        }
        for(int i=0;i<4;i++) {
            DatagramPacket packet=new DatagramPacket(response,response.length);
            try{socket.receive(packet);}catch(SocketTimeoutException empty){break;}
            int length=packet.getLength();
            if(session!=null&&length>=56&&packet.getPort()==port&&Arrays.equals(Arrays.copyOfRange(response,0,8),"QFTADJS1".getBytes(StandardCharsets.US_ASCII))&&
                Arrays.equals(Arrays.copyOfRange(response,8,24),session)&&MessageDigest.isEqual(Arrays.copyOfRange(response,length-32,length),mac.doFinal(Arrays.copyOf(response,length-32)))) {
                adjustments=new String(response,24,length-56,StandardCharsets.UTF_8);adjustmentsTime=now;continue;
            }
            if(packet.getLength()!=72||packet.getPort()!=port||!Arrays.equals(Arrays.copyOfRange(response,0,8),"QFTPAIR1".getBytes(StandardCharsets.US_ASCII))||
                !Arrays.equals(Arrays.copyOfRange(response,8,24),challenge)||!MessageDigest.isEqual(Arrays.copyOfRange(response,40,72),mac.doFinal(Arrays.copyOf(response,40))))continue;
            host=packet.getAddress();session=Arrays.copyOfRange(response,24,40);received=now;
        }
    }
    void tracking(byte[] payload,long sequence,long captured,long now) throws IOException {
        if(!connected(now))return;
        ByteBuffer packet=ByteBuffer.allocate(656).order(ByteOrder.LITTLE_ENDIAN);
        packet.put("QFTDATA1".getBytes(StandardCharsets.US_ASCII)).put(session).putLong(sequence).putLong(captured).putLong(now).put(payload);
        send(signed(packet.array()),host);
    }
    boolean adjustments(byte[] settings) throws IOException {
        if(session==null||host==null)return false;
        byte[] packet=new byte[24+settings.length];
        System.arraycopy("QFTADJR1".getBytes(StandardCharsets.US_ASCII),0,packet,0,8);System.arraycopy(session,0,packet,8,16);System.arraycopy(settings,0,packet,24,settings.length);
        send(signed(packet),host);return true;
    }
    public void close(){socket.close();}
}
