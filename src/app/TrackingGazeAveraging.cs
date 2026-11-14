using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace QFTPlus;

internal sealed partial class Tracking
{
    volatile bool averagingReady;
    internal volatile string AveragingError = "";
    Process? averaging;

    internal void SetMetaGazeAveraging(int samples)
    {
        if (samples is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(samples));
        if (!averagingReady) return;
        using var socket = new UdpClient();
        var data = Encoding.ASCII.GetBytes(samples.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try { socket.Send(data, data.Length, "127.0.0.1", 27278); }
        catch (SocketException) { AveragingError = Loc.Get("MetaGazeAveragingFailed"); }
    }

    void StartMetaGazeAveraging()
    {
        try
        {
            averaging = session.Launch(session.Python, [Path.Combine(session.Root, "gaze_averaging.py"),
                "--samples", session.MetaGazeAveraging.ToString(System.Globalization.CultureInfo.InvariantCulture)], "gaze-averaging.log", line =>
                {
                    if (line == "QFT_AVERAGING_CONTROL_READY")
                    {
                        averagingReady = true;
                        SetMetaGazeAveraging(session.MetaGazeAveraging);
                    }
                    else if (line.StartsWith("QFT_AVERAGING_APPLIED ", StringComparison.Ordinal)) AveragingError = "";
                    else if (line.StartsWith("QFT_AVERAGING_ERROR ", StringComparison.Ordinal))
                        AveragingError = Loc.Get(line.Contains("UNSUPPORTED", StringComparison.Ordinal) ? "MetaGazeAveragingUnavailable"
                            : line.Contains("BUSY", StringComparison.Ordinal) ? "MetaGazeAveragingBusy"
                            : line.Contains("RESTORE_FAILED", StringComparison.Ordinal) ? "MetaGazeAveragingRestoreFailed" : "MetaGazeAveragingFailed");
                });
            averaging.EnableRaisingEvents = true;
            averaging.Exited += (_, _) =>
            {
                averagingReady = false;
                if (!stop.IsCancellationRequested) AveragingError = Loc.Get("MetaGazeAveragingFailed");
            };
            if (averaging.HasExited) AveragingError = Loc.Get("MetaGazeAveragingFailed");
        }
        catch (Exception error)
        {
            AveragingError = Loc.Get("MetaGazeAveragingFailed");
            Write("QFT_AVERAGING_ERROR " + error.Message);
        }
    }
}
