using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Qpro.GazeBridge;

internal sealed class HeadsetState : IDisposable
{
    internal const int Port = 27276;
    readonly byte[] session = RandomNumberGenerator.GetBytes(16), buffer = new byte[65536], payload = new byte[608];
    byte[]? key;
    UdpClient? socket;
    ulong sequence;
    long received;
    double clockOffset = double.PositiveInfinity;
    string configured = "";
    byte[]? challenge;
    EndPoint? adjustFrom;
    internal byte[]? AdjustRequest;
    internal bool Fresh(long now) => received != 0 && now >= received && now - received < 300;
    internal ReadOnlySpan<byte> Current => payload;
    internal void Configure(string secret)
    {
        if (secret == configured) return;
        socket?.Dispose(); socket = null; key = null; received = 0; sequence = 0; clockOffset = double.PositiveInfinity;
        configured = "";
        if (secret.Length != 64 || !secret.All(Uri.IsHexDigit)) return;
        key = Convert.FromHexString(secret);
        socket = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        socket.Client.Blocking = false;
        configured = secret;
    }
    internal bool Accept(ReadOnlySpan<byte> packet, long now)
    {
        if (key is null || packet.Length != 688 || !packet.StartsWith("QFTDATA1"u8) || !packet.Slice(8, 16).SequenceEqual(session) ||
            !CryptographicOperations.FixedTimeEquals(packet[^32..], HMACSHA256.HashData(key, packet[..^32]))) return false;
        ulong next = BinaryPrimitives.ReadUInt64LittleEndian(packet[24..]);
        ulong captured = BinaryPrimitives.ReadUInt64LittleEndian(packet[32..]), sent = BinaryPrimitives.ReadUInt64LittleEndian(packet[40..]);
        if (next <= sequence || sent < captured || sent - captured > 250_000_000) return false;
        var data = packet.Slice(48, 608);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) > 127 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) > 31 || BinaryPrimitives.ReadUInt32LittleEndian(data[288..]) > 7) return false;
        for (int i = 20; i < 264; i += 4) if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(data[i..]))) return false;
        for (int i = 296; i < 576; i += 4) { float value = BinaryPrimitives.ReadSingleLittleEndian(data[i..]); if (!float.IsFinite(value) || value < 0 || value > 1) return false; }
        for (int i = 576; i < 608; i += 4) if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(data[i..]))) return false;
        double offset = now - sent / 1e6;
        clockOffset = Math.Min(clockOffset, offset);
        if (offset - clockOffset > 150) return false;
        data.CopyTo(payload); sequence = next; received = now;
        return true;
    }
    internal bool Poll(long now)
    {
        if (socket is null || key is null) return false;
        bool changed = false;
        try
        {
            for (int i = 0; i < 64 && socket.Available > 0; i++)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var packet = buffer.AsSpan(0, socket.Client.ReceiveFrom(buffer, ref from));
                if (packet.Length == 56 && packet.StartsWith("QFTDISC1"u8) &&
                    CryptographicOperations.FixedTimeEquals(packet[24..], HMACSHA256.HashData(key, packet[..24])))
                {
                    if (challenge is null || !packet.Slice(8, 16).SequenceEqual(challenge))
                    {
                        challenge = packet.Slice(8, 16).ToArray();
                        RandomNumberGenerator.Fill(session); sequence = 0; received = 0; clockOffset = double.PositiveInfinity;
                    }
                    byte[] response = new byte[72]; "QFTPAIR1"u8.CopyTo(response); packet.Slice(8, 16).CopyTo(response.AsSpan(8));
                    session.CopyTo(response, 24); HMACSHA256.HashData(key, response.AsSpan(0, 40)).CopyTo(response, 40);
                    socket.Client.SendTo(response, from);
                }
                else if (packet.Length >= 56 && packet.StartsWith("QFTADJR1"u8) && packet.Slice(8, 16).SequenceEqual(session) &&
                    CryptographicOperations.FixedTimeEquals(packet[^32..], HMACSHA256.HashData(key, packet[..^32])))
                {
                    AdjustRequest = packet[24..^32].ToArray(); adjustFrom = from;
                }
                else changed |= Accept(packet, now);
            }
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending or SocketError.ConnectionReset) { }
        return changed;
    }
    internal bool CopyTo(byte[] state, long now)
    {
        if (!Fresh(now)) return false;
        Array.Clear(state);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(288));
        state[0] = (byte)(flags & 1); state[1] = state[0];
        payload.AsSpan(296, 280).CopyTo(state.AsSpan(4));
        state[292] = (byte)((flags >> 1) & 1); state[293] = (byte)((flags >> 2) & 1);
        payload.AsSpan(576, 16).CopyTo(state.AsSpan(296)); payload.AsSpan(592, 16).CopyTo(state.AsSpan(324));
        BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(352), state[292]); BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(356), state[293]);
        return true;
    }
    internal void ReplyAdjustments(ReadOnlySpan<byte> json)
    {
        if (socket is null || key is null || adjustFrom is null) return;
        var reply = new byte[24 + json.Length + 32];
        "QFTADJS1"u8.CopyTo(reply); session.CopyTo(reply, 8); json.CopyTo(reply.AsSpan(24));
        HMACSHA256.HashData(key, reply.AsSpan(0, reply.Length - 32)).CopyTo(reply, reply.Length - 32);
        try { socket.Client.SendTo(reply, adjustFrom); }
        catch (SocketException) { }
    }
    public void Dispose() => socket?.Dispose();
}
