using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QFTPlus;

internal static class AvatarPreview
{
    internal const int Width = 360, Height = 250;

    internal static async Task Run(Session session, Func<float[]> pose, Action<BitmapSource> show, CancellationToken token)
    {
        if (string.IsNullOrEmpty(session.Config["adbTarget"]?.GetValue<string>())) throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
        var assets = await HeadsetApp.SendAsync(session, token, ("avatarWanted", true));
        var library = assets["avatarLibrary"]?.GetValue<string>();
        var preset = assets["avatarPreset"]?.GetValue<string>();
        if (library is null || !library.StartsWith("/data/app/") || !library.EndsWith("/lib/arm64")
            || preset is null || !preset.StartsWith("/data/user/0/com.qftplus.headset/files/avatar/") || preset.Contains(".."))
            throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
        var adb = Adb.Exe(session.Root);
        var target = session.Config["adbTarget"]?.GetValue<string>() ?? "";
        var socket = "qft-avatar-" + Guid.NewGuid().ToString("N");
        var command = string.Join(" ", new[] { library + "/libqft_avatar_preview.so", library + "/libovravatar2.so", preset[..preset.LastIndexOf('/')], preset, socket }.Select(Adb.Quote));
        var info = Processes.Info(adb, ["-s", target, "shell", "-T", "su -c " + Adb.Quote(command)]);
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var errors = process.StandardError.ReadToEndAsync();
        string? port = null;
        try
        {
            if (await process.StandardOutput.ReadLineAsync(timeout.Token) != "QFT_AVATAR_READY")
                throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
            var output = process.StandardOutput.ReadToEndAsync();
            var forwarded = await Processes.RunAsync(adb, ["-s", target, "forward", "tcp:0", "localabstract:" + socket], timeout.Token);
            if (forwarded.Code != 0 || !int.TryParse(forwarded.Text.Trim(), out var number) || number is < 1 or > 65535)
                throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
            port = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", number, timeout.Token);
            var stream = client.GetStream();
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, timeout.Token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x31564651 || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != 0)
                throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
            while (true)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var values = pose();
                var packet = new byte[76 * sizeof(float)];
                Buffer.BlockCopy(values, 0, packet, 0, packet.Length);
                await stream.WriteAsync(packet, timeout.Token);
                await stream.ReadExactlyAsync(header, timeout.Token);
                var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x31564651 || size is < 1 or > Width * Height * 4 + 1024)
                    throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
                var frame = new byte[size];
                await stream.ReadExactlyAsync(frame, timeout.Token);
                show(Decode(frame));
                await Task.Delay(80, token);
            }
        }
        finally
        {
            try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            if (port is not null) await Processes.RunAsync(adb, ["-s", target, "forward", "--remove", "tcp:" + port], seconds: 5);
            if (errors.IsCompletedSuccessfully && errors.Result.Length > 0) Session.Log(Path.Combine(session.Root, "studio.log"), errors.Result);
        }
    }

    internal static BitmapSource Decode(byte[] frame)
    {
        using var zip = new ZLibStream(new MemoryStream(frame), CompressionMode.Decompress);
        var rgba = new byte[Width * Height * 4];
        zip.ReadExactly(rgba);
        if (zip.ReadByte() != -1) throw new IOException(Loc.Get("AvatarPreviewUnavailable"));
        var pixels = new byte[rgba.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var source = ((Height - 1 - y) * Width + x) * 4;
                var dest = (y * Width + x) * 4;
                pixels[dest] = rgba[source + 2]; pixels[dest + 1] = rgba[source + 1];
                pixels[dest + 2] = rgba[source]; pixels[dest + 3] = rgba[source + 3];
            }
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        image.Freeze();
        return image;
    }
}
