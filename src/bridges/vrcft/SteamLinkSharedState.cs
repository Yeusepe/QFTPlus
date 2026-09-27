using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Qpro.GazeBridge;

[SupportedOSPlatform("windows")]
internal static class SteamLinkSharedState
{
    internal const string MapName = "QFTPlus.SteamLink.BodyState";
    internal const int Bytes = 376;
    internal static bool TryRead(byte[] state)
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, Bytes, MemoryMappedFileAccess.Read);
            long sequence = view.ReadInt64(368);
            if ((sequence & 1) != 0) return false;
            long tick = view.ReadInt64(360);
            view.ReadArray(0, state, 0, 360);
            Thread.MemoryBarrier();
            return sequence == view.ReadInt64(368) && tick > 0 &&
                Environment.TickCount64 - tick is >= 0 and <= SteamLinkState.TimeoutMs &&
                ((state[0] & 1) != 0 || state[292] != 0 || state[293] != 0);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
