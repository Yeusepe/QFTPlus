using System.Buffers.Binary;

namespace Qpro.GazeBridge;

internal sealed class PupilDilationState
{
    private (float Left, float Right)? _values;
    private long _received;

    internal bool Accept(byte[] packet, long now)
    {
        if (packet.Length != 16 || !packet.AsSpan(0, 4).SequenceEqual("QPPD"u8) ||
            packet[4] != 1 || packet[5] > 1 || packet[6] != 0 || packet[7] != 0) return false;
        float left = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8, 4)));
        float right = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, 4)));
        if (!float.IsFinite(left) || !float.IsFinite(right) || left < 0 || left > 1 || right < 0 || right > 1)
            return false;
        _values = packet[5] == 1 ? (left, right) : null;
        _received = now;
        return true;
    }

    internal (float Left, float Right)? Current(long now) =>
        now >= _received && now - _received <= 300 ? _values : null;
}
