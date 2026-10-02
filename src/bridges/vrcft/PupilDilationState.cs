using System.Buffers.Binary;

namespace Qpro.GazeBridge;

internal sealed class PupilDilationState
{
    private (float Left, float Right)? _values;
    private long _received;

    internal bool Accept(ReadOnlySpan<byte> packet, long now)
    {
        if (!Packets.Header(packet, "QPPD"u8, 1, 16) || packet[5] > 1 || packet[6] != 0 || packet[7] != 0) return false;
        float left = BinaryPrimitives.ReadSingleLittleEndian(packet[8..]);
        float right = BinaryPrimitives.ReadSingleLittleEndian(packet[12..]);
        if (!float.IsFinite(left) || !float.IsFinite(right) || left < 0 || left > 1 || right < 0 || right > 1)
            return false;
        _values = packet[5] == 1 ? (left, right) : null;
        _received = now;
        return true;
    }

    internal (float Left, float Right)? Current(long now) => Packets.Fresh(_received, now, 300) ? _values : null;
}
