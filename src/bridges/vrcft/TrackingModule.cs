using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;

namespace Qpro.GazeBridge;

[SupportedOSPlatform("windows")]

public sealed class TrackingModule : ExtTrackingModule
{
    private const string MapName = "VirtualDesktop.BodyState";
    private const int StateBytes = 360;
    private const int ExpressionOffset = 4;
    private const int ExpressionCount = 70;
    private const int GazePort = 27275;
    private const int GazePacketBytes = 24;
    private const long GazeTimeoutMs = 250;
    private const long GazeHoldMs = 1000;
    private const int TonguePort = 27276;
    private const int TonguePacketBytes = 56;
    private const long TongueTimeoutMs = 300;
    private static readonly (int Source, int[] Targets)[] ExpressionMap =
    [
        (2, [19]), (3, [18]), (4, [17]), (5, [16]),
        (6, [21]), (7, [20]), (8, [67]), (9, [66]),
        (10, [65]), (11, [64]), (24, [22]), (25, [24]),
        (26, [23]), (27, [25]), (30, [61]), (31, [60]),
        (32, [57, 59]), (33, [56, 58]), (34, [39]), (35, [37]),
        (36, [38]), (37, [36]), (38, [69]), (39, [68]),
        (40, [41, 43]), (41, [40, 42]), (42, [63]), (43, [62]),
        (44, [33]), (46, [32]), (48, [71]), (49, [70]),
        (50, [29]), (51, [51]), (52, [50]), (53, [53, 55]),
        (54, [52, 54]), (55, [49]), (56, [48]),
        (61, [45, 47]), (62, [44, 46])
    ];

    private readonly byte[] _first = new byte[StateBytes];
    private readonly byte[] _second = new byte[StateBytes];
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private UdpClient? _steamLinkSocket;
    private readonly SteamLinkState _steamLink = new();
    private readonly byte[] _steamLinkBytes = new byte[StateBytes];
    private MemoryMappedFile? _steamLinkMap;
    private MemoryMappedViewAccessor? _steamLinkView;
    private long _steamLinkSequence;
    private readonly byte[] _lastVdState = new byte[StateBytes];
    private long _lastVdChange;
    private string? _source;
    private UdpClient? _gazeSocket;
    private UdpClient? _tongueSocket;
    private UdpClient? _extraFaceSocket;
    private UdpClient? _pupilSocket;
    private readonly PupilDilationState _pupil = new();
    private readonly ExtraFaceState _extraFace = new();
    private readonly Dictionary<int, float> _extraBaseline = new();
    private bool _needsEye;
    private bool _needsExpression;
    private long _lastGazeTick;
    private long _leftGazeHeldTick;
    private long _rightGazeHeldTick;
    private float _leftGazeX;
    private float _leftGazeY;
    private float _rightGazeX;
    private float _rightGazeY;
    private byte _gazeFlags;
    private readonly float[] _tongueValues = new float[12];
    private ushort _tongueMask = 0xFFF;
    private long _lastTongueTimingTick;
    private static readonly int[] TongueExpressions = {
        (int)UnifiedExpressions.TongueOut, (int)UnifiedExpressions.TongueUp,
        (int)UnifiedExpressions.TongueDown, (int)UnifiedExpressions.TongueLeft,
        (int)UnifiedExpressions.TongueRight, (int)UnifiedExpressions.TongueRoll,
        (int)UnifiedExpressions.TongueBendDown, (int)UnifiedExpressions.TongueCurlUp,
        (int)UnifiedExpressions.TongueSquish, (int)UnifiedExpressions.TongueFlat,
        (int)UnifiedExpressions.TongueTwistLeft, (int)UnifiedExpressions.TongueTwistRight,
    };
    private bool _tongueEnabled;
    private bool _tongueDirty;
    private long _lastTongueTick;
    private static readonly string[] TongueNames = Array.ConvertAll(TongueExpressions, e => ((UnifiedExpressions)e).ToString());
    private static readonly Dictionary<int, int> ShapePartners = Enum.GetValues<UnifiedExpressions>()
        .Select(e => (Shape: (int)e, Partner: OutputAdjustments.Partner(e.ToString())))
        .Where(p => p.Partner is not null && Enum.IsDefined(typeof(UnifiedExpressions), p.Partner))
        .ToDictionary(p => p.Shape, p => (int)Enum.Parse<UnifiedExpressions>(p.Partner!));
    private float[] _shapeInputs = [];
    private readonly OutputAdjustments _adjustments = new();
    private readonly Dictionary<int, float> _outputBaseline = new();
    private float[]? _eyeBaseline;
    private string? _settingsRoot;
    private long _outputTick;
    private string? _settingsConfigPath;
    private long _settingsConfigTick, _outputStatusTick;
    private bool _outputStatusFailed;

    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(
        bool eyeAvailable,
        bool expressionAvailable)
    {
        _needsEye = eyeAvailable;
        _needsExpression = expressionAvailable;
        ModuleInformation = new ModuleMetadata
        {
            Name = "QFT+"
        };

        _gazeSocket = Bind(GazePort, "gaze");
        _tongueSocket = Bind(TonguePort, "tongue");

        try
        {
            _steamLinkSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, SteamLinkState.Port));
            _steamLinkSocket.Client.Blocking = false;
            _steamLinkSocket.Client.ReceiveBufferSize = 1024 * 1024;
            _steamLinkMap = MemoryMappedFile.CreateOrOpen(SteamLinkSharedState.MapName, SteamLinkSharedState.Bytes);
            _steamLinkView = _steamLinkMap.CreateViewAccessor();
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException)
        {
            Logger.LogError(error, "Steam Link port 9015 unavailable. Disable the standalone SteamLink module and restart VRCFaceTracking");
        }
        TryOpenMap();
        _extraFaceSocket = Bind(27278, "extra expressions", LogLevel.Warning);
        _pupilSocket = Bind(27279, "pupil dilation", LogLevel.Warning);
        StartLocalRuntime();
        Logger.LogInformation(
            "Quest Pro Virtual Desktop / Steam Link bridge initialized (eye={Eye}, face={Face}); " +
            "custom gaze falls back to the active streaming app after {Timeout} ms",
            _needsEye, _needsExpression, GazeTimeoutMs);
        return (_needsEye, _needsExpression);
    }

    private UdpClient? Bind(int port, string feature, LogLevel level = LogLevel.Error)
    {
        try
        {
            var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            socket.Client.Blocking = false;
            return socket;
        }
        catch (SocketException error)
        {
            Logger.Log(level, error, "Could not bind QFT+ {Feature} port {Port}; factory tracking remains available", feature, port);
            return null;
        }
    }

    private static void Receive(UdpClient? socket, Action<byte[]> accept, int limit = 16)
    {
        if (socket is null) return;
        try
        {
            for (int i = 0; i < limit && socket.Available > 0; i++)
            {
                IPEndPoint sender = new(IPAddress.Loopback, 0);
                accept(socket.Receive(ref sender));
            }
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending) { }
    }

    public override void Update()
    {
        ReceiveGaze();
        ReceiveTongue();
        Receive(_extraFaceSocket, packet => _extraFace.Accept(packet, Environment.TickCount64));
        Receive(_pupilSocket, packet => _pupil.Accept(packet, Environment.TickCount64));
        RestoreAdjustedOutput();
        RestoreExtraFaceBaseline();
        if (!TryReadState()) Array.Clear(_second);

        var expressions = MemoryMarshal.Cast<byte, float>(_second.AsSpan(ExpressionOffset, ExpressionCount * sizeof(float)));

        if (_needsEye)
            UpdateEyes(expressions);
        if (_needsExpression)
            UpdateMouth(expressions, _second[0]);
        var shares = _extraFace.CurrentShares(Environment.TickCount64);
        foreach (var pair in ExtraFaceState.SharePairs)
        {
            if (!shares.TryGetValue(pair + "Left", out float shareLeft) || !shares.TryGetValue(pair + "Right", out float shareRight)) continue;
            int left = (int)Enum.Parse<UnifiedExpressions>(pair + "Left"), right = (int)Enum.Parse<UnifiedExpressions>(pair + "Right");
            if (left <= 11 ? !_needsEye : !_needsExpression) continue;
            float nativeLeft = UnifiedTracking.Data.Shapes[left].Weight, nativeRight = UnifiedTracking.Data.Shapes[right].Weight;
            _extraBaseline.TryAdd(left, nativeLeft); _extraBaseline.TryAdd(right, nativeRight);
            var (fusedLeft, fusedRight) = ExtraFaceState.Split(nativeLeft, nativeRight, shareLeft, shareRight);
            if (!_adjustments.Passthrough(pair + "Left")) Set(left, fusedLeft);
            if (!_adjustments.Passthrough(pair + "Right")) Set(right, fusedRight);
        }
        foreach (var entry in _extraFace.Current(Environment.TickCount64))
        {
            if (_adjustments.Passthrough(entry.Key) || !Enum.TryParse<UnifiedExpressions>(entry.Key, out var expression)) continue;
            int index = (int)expression;
            if (index <= 11 ? !_needsEye : !_needsExpression) continue;
            _extraBaseline.TryAdd(index, UnifiedTracking.Data.Shapes[index].Weight);
            Set(index, entry.Value);
        }
        AdjustOutput();
        Thread.Sleep(5);
    }

    public override void Teardown()
    {
        _steamLinkSocket?.Dispose(); _steamLinkSocket = null;
        _steamLinkView?.Write(360, 0L);
        _steamLinkView?.Dispose(); _steamLinkView = null;
        _steamLinkMap?.Dispose(); _steamLinkMap = null;
        RestoreAdjustedOutput();
        if (_needsEye) ResetPupilDilation();
        _pupilSocket?.Dispose();
        _pupilSocket = null;
        _gazeSocket?.Dispose();
        _gazeSocket = null;
        _tongueSocket?.Dispose();
        _tongueSocket = null;
        _extraFaceSocket?.Dispose();
        _extraFaceSocket = null;
        RestoreExtraFaceBaseline();
        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }

    private void StartLocalRuntime()
    {
        string configPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "QproAutoStart.json");
        _settingsConfigPath = configPath;
        if (!File.Exists(configPath)) return;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            string root = Path.GetFullPath(config.RootElement.GetProperty("root").GetString()!);
            _settingsRoot = root;
            if (config.RootElement.TryGetProperty("startWithVrcft", out var enabled) && enabled.ValueKind == JsonValueKind.False) return;
            string app = config.RootElement.TryGetProperty("studioApp", out var configured)
                ? configured.GetString()! : Path.Combine(root, "QproFaceTracking.exe");
            if (!File.Exists(app)) throw new FileNotFoundException("QFT+ is missing", app);
            var start = new ProcessStartInfo(app)
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--from-vrcft");
            start.ArgumentList.Add("--root");
            start.ArgumentList.Add(root);
            using var process = Process.Start(start);
            Logger.LogInformation("Qpro automatic runtime started; diagnostics: {Path}",
                Path.Combine(root, "autostart.log"));
        }
        catch (Exception error)
        {
            Logger.LogError(error, "Qpro automatic runtime could not start; stock tracking remains available");
        }
    }

    private float[] ReadEyes() => [UnifiedTracking.Data.Eye.Left.Gaze.x, UnifiedTracking.Data.Eye.Left.Gaze.y,
        UnifiedTracking.Data.Eye.Right.Gaze.x, UnifiedTracking.Data.Eye.Right.Gaze.y,
        UnifiedTracking.Data.Eye.Left.PupilDiameter_MM / 10, UnifiedTracking.Data.Eye.Right.PupilDiameter_MM / 10,
        UnifiedTracking.Data.Eye.Left.Openness, UnifiedTracking.Data.Eye.Right.Openness];
    private static readonly string[] EyeKeys = ["GazeLeftX", "GazeLeftY", "GazeRightX", "GazeRightY", "PupilLeft", "PupilRight", "OpennessLeft", "OpennessRight"];
    private void WriteEyes(float[] v)
    {
        UnifiedTracking.Data.Eye.Left.Gaze.x=v[0]; UnifiedTracking.Data.Eye.Left.Gaze.y=v[1];
        UnifiedTracking.Data.Eye.Right.Gaze.x=v[2]; UnifiedTracking.Data.Eye.Right.Gaze.y=v[3];
        UnifiedTracking.Data.Eye.Left.PupilDiameter_MM=v[4]*10; UnifiedTracking.Data.Eye.Right.PupilDiameter_MM=v[5]*10;
        UnifiedTracking.Data.Eye.Left.Openness=v[6]; UnifiedTracking.Data.Eye.Right.Openness=v[7];
    }
    private void RestoreAdjustedOutput()
    {
        foreach (var entry in _outputBaseline) Set(entry.Key, entry.Value);
        _outputBaseline.Clear();
        if (_eyeBaseline is not null) { WriteEyes(_eyeBaseline); _eyeBaseline=null; }
    }
    private void AdjustOutput()
    {
        var tick=Environment.TickCount64; var utc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;

        if (_settingsConfigPath is not null && tick - _settingsConfigTick >= 1000)
        {
            _settingsConfigTick = tick;
            try
            {
                using var file = new FileStream(_settingsConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var config = JsonDocument.Parse(file);
                if (config.RootElement.ValueKind == JsonValueKind.Object && config.RootElement.TryGetProperty("root", out var folder) && folder.ValueKind == JsonValueKind.String && folder.GetString() is { Length: > 0 } root)
                    _settingsRoot = Path.GetFullPath(root);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (_settingsRoot is null) return;
        var dt=_outputTick==0?.005:(tick-_outputTick)/1000.0; _outputTick=tick;
        _adjustments.Poll(_settingsRoot,tick,utc);
        var report = tick - _outputStatusTick >= 250;
        JsonObject? inputs = report ? new() : null, outputs = report ? new() : null;
        if (_shapeInputs.Length != UnifiedTracking.Data.Shapes.Length) _shapeInputs = new float[UnifiedTracking.Data.Shapes.Length];
        for (var i = 0; i < _shapeInputs.Length; i++) _shapeInputs[i] = UnifiedTracking.Data.Shapes[i].Weight;
        foreach (var shape in Enum.GetValues<UnifiedExpressions>())
        {
            var index=(int)shape; var name=shape.ToString();
            if (name=="Max" || index<0 || index>=UnifiedTracking.Data.Shapes.Length || (index<=11?!_needsEye:!_needsExpression)) continue;
            var value=_shapeInputs[index];
            if (report) inputs![name] = float.IsFinite(value) ? value : 0;
            if (_adjustments.Enabled(name))
            {
                _outputBaseline[index]=value;
                float? partner = ShapePartners.TryGetValue(index, out var other) && other < _shapeInputs.Length ? _shapeInputs[other] : null;
                Set(index,_adjustments.Apply(name,value,0,0,1,dt,utc,partner));
            }
            if (report) outputs![name] = float.IsFinite(UnifiedTracking.Data.Shapes[index].Weight) ? UnifiedTracking.Data.Shapes[index].Weight : 0;
        }
        if (_needsEye)
        {
            var raw=ReadEyes(); var eyes=(float[])raw.Clone(); var changed=false;
            for(var i=0;i<EyeKeys.Length;i++)
            {
                var name=EyeKeys[i];
                if (report) inputs![name] = float.IsFinite(eyes[i]) ? eyes[i] : 0;
                if (_adjustments.Enabled(name))
                {
                    _eyeBaseline??=raw; changed=true;
                    var other=Array.IndexOf(EyeKeys,OutputAdjustments.Partner(name)??"");
                    eyes[i]=_adjustments.Apply(name,eyes[i],i is 4 or 5?.5f:i>=6?1:0,i<4?-1.2f:0,i<4?1.2f:1,dt,utc,other<0?null:raw[other]);
                }
                if (report) outputs![name] = float.IsFinite(eyes[i]) ? eyes[i] : 0;
            }
            if(changed)WriteEyes(eyes);
        }
        if (!report) return;
        _outputStatusTick = tick;
        try
        {
            var status = new JsonObject { ["updated"] = utc, ["settings"] = _adjustments.Settings.DeepClone(),
                ["inputs"] = inputs, ["outputs"] = outputs, ["pupilTracking"] = _pupil.Current(tick) is not null };
            var path = Path.Combine(_settingsRoot, "output-status.json");
            File.WriteAllText(path + ".tmp", status.ToJsonString());
            File.Move(path + ".tmp", path, true);
            _outputStatusFailed = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_outputStatusFailed) Logger?.LogWarning(error, "Could not publish QFT+ adjustment readout");
            _outputStatusFailed = true;
        }
    }

    private void TryOpenMap()
    {
        if (_view is not null)
            return;
        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, StateBytes, MemoryMappedFileAccess.Read);
        }
        catch (FileNotFoundException)
        {
            _map?.Dispose();
            _map = null;
        }
    }

    private void RestoreExtraFaceBaseline()
    {
        foreach (var entry in _extraBaseline) Set(entry.Key, entry.Value);
        _extraBaseline.Clear();
    }

    private bool TryReadState()
    {
        long now = Environment.TickCount64;
        Receive(_steamLinkSocket, packet => _steamLink.Accept(packet, now), 512);
        bool steamLink = _steamLink.CopyTo(_steamLinkBytes, now);
        if (_steamLinkView is not null)
        {
            _steamLinkView.Write(368, ++_steamLinkSequence);
            Thread.MemoryBarrier();
            _steamLinkView.WriteArray(0, _steamLinkBytes, 0, StateBytes);
            _steamLinkView.Write(360, steamLink ? now : 0L);
            Thread.MemoryBarrier();
            _steamLinkView.Write(368, ++_steamLinkSequence);
        }
        bool vd = TryReadVirtualDesktop(now);
        if (steamLink) _steamLinkBytes.CopyTo(_second, 0);
        string source = steamLink ? "Steam Link" : vd ? "Virtual Desktop" : "waiting for tracking";
        if (source != _source)
        {
            _source = source;
            Logger?.LogInformation("QFT+ factory source: {Source}", source);
        }
        return steamLink || vd;
    }

    private bool TryReadVirtualDesktop(long now)
    {
        TryOpenMap();
        if (_view is null)
            return false;
        try
        {
            _view.ReadArray(0, _first, 0, StateBytes);
            Thread.MemoryBarrier();
            _view.ReadArray(0, _second, 0, StateBytes);
            if (!_first.AsSpan().SequenceEqual(_second)) return false;
            if (!_second.AsSpan().SequenceEqual(_lastVdState))
            {
                _second.CopyTo(_lastVdState, 0);
                _lastVdChange = now;
            }
            return _lastVdChange != 0 && now - _lastVdChange <= 1000;
        }
        catch (ObjectDisposedException)
        {
            _view = null;
            _map = null;
            return false;
        }
    }

    private static void ResetPupilDilation()
    {
        UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 5.0f;
        UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = 5.0f;
        UnifiedTracking.Data.Eye._minDilation = 0.0f;
        UnifiedTracking.Data.Eye._maxDilation = 10.0f;
    }

    private void ReceiveGaze() => Receive(_gazeSocket, packet =>
    {
        if (packet.Length != GazePacketBytes ||
            !packet.AsSpan(0, 4).SequenceEqual("QPGE"u8) ||
            packet[4] != 1)
            return;
        _gazeFlags = packet[5];
        _leftGazeX = ReadFloat(packet, 8);
        _leftGazeY = ReadFloat(packet, 12);
        _rightGazeX = ReadFloat(packet, 16);
        _rightGazeY = ReadFloat(packet, 20);
        _lastGazeTick = Environment.TickCount64;
    });

    private void ReceiveTongue() => Receive(_tongueSocket, packet =>
    {
        if (packet.Length < 8 ||
            !packet.AsSpan(0, 4).SequenceEqual("QPTO"u8) ||
            !((packet[4] == 1 && packet.Length == TonguePacketBytes) ||
              (packet[4] == 2 && packet.Length == 72)))
            return;
        bool valid = true;
        for (int index = 0; index < _tongueValues.Length; index++)
            valid &= float.IsFinite(ReadFloat(packet, 8 + index * 4));
        if (!valid) return;
        _tongueMask = packet[4] == 1 ? (ushort)0xFFF :
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)) & 0xFFF);
        _tongueEnabled = (packet[5] & 1) != 0;
        for (int index = 0; index < _tongueValues.Length; ++index)
            _tongueValues[index] = Math.Clamp(ReadFloat(packet, 8 + index * 4), 0.0f, 1.0f);
        if (packet[4] == 2 && Environment.TickCount64 - _lastTongueTimingTick >= 5000)
        {
            double arrival = BitConverter.ToDouble(packet, 56), sent = BitConverter.ToDouble(packet, 64);
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (double.IsFinite(arrival) && double.IsFinite(sent) && arrival > 0 && arrival <= sent && sent <= now && now - arrival < 10)
            {
                Logger.LogInformation("Tongue PC arrival-to-bridge {AgeMs:F2} ms; publication-to-bridge {UdpMs:F2} ms", (now-arrival)*1000, (now-sent)*1000);
                _lastTongueTimingTick = Environment.TickCount64;
            }
        }
        _tongueDirty = true;
        _lastTongueTick = Environment.TickCount64;
    });

    private void UpdateEyes(ReadOnlySpan<float> values)
    {
        bool leftValid = _second[292] != 0;
        bool rightValid = _second[293] != 0;
        long now = Environment.TickCount64;
        bool customFresh = _lastGazeTick != 0 &&
            now - _lastGazeTick <= GazeTimeoutMs;

        bool leftX = _adjustments.Passthrough("GazeLeftX"), leftY = _adjustments.Passthrough("GazeLeftY");
        bool rightX = _adjustments.Passthrough("GazeRightX"), rightY = _adjustments.Passthrough("GazeRightY");
        if (customFresh && (_gazeFlags & 1) != 0 && !(leftX && leftY))
        {
            (float x, float y) = leftValid ? QuaternionToCartesian(_second, 296) : (UnifiedTracking.Data.Eye.Left.Gaze.x, UnifiedTracking.Data.Eye.Left.Gaze.y);
            UnifiedTracking.Data.Eye.Left.Gaze.x = leftX ? x : _leftGazeX;
            UnifiedTracking.Data.Eye.Left.Gaze.y = leftY ? y : _leftGazeY;
            _leftGazeHeldTick = now;
        }
        else if (leftValid)
        {
            (float x, float y) = QuaternionToCartesian(_second, 296);
            UnifiedTracking.Data.Eye.Left.Gaze.x = x;
            UnifiedTracking.Data.Eye.Left.Gaze.y = y;
            _leftGazeHeldTick = now;
        }
        else if (now - _leftGazeHeldTick > GazeHoldMs) { UnifiedTracking.Data.Eye.Left.Gaze.x = 0; UnifiedTracking.Data.Eye.Left.Gaze.y = 0; }

        if (customFresh && (_gazeFlags & 2) != 0 && !(rightX && rightY))
        {
            (float x, float y) = rightValid ? QuaternionToCartesian(_second, 324) : (UnifiedTracking.Data.Eye.Right.Gaze.x, UnifiedTracking.Data.Eye.Right.Gaze.y);
            UnifiedTracking.Data.Eye.Right.Gaze.x = rightX ? x : _rightGazeX;
            UnifiedTracking.Data.Eye.Right.Gaze.y = rightY ? y : _rightGazeY;
            _rightGazeHeldTick = now;
        }
        else if (rightValid)
        {
            (float x, float y) = QuaternionToCartesian(_second, 324);
            UnifiedTracking.Data.Eye.Right.Gaze.x = x;
            UnifiedTracking.Data.Eye.Right.Gaze.y = y;
            _rightGazeHeldTick = now;
        }
        else if (now - _rightGazeHeldTick > GazeHoldMs) { UnifiedTracking.Data.Eye.Right.Gaze.x = 0; UnifiedTracking.Data.Eye.Right.Gaze.y = 0; }

        UnifiedTracking.Data.Eye.Left.Openness = 1.0f - Math.Clamp(
            values[12] + values[4] * values[28], 0.0f, 1.0f);
        UnifiedTracking.Data.Eye.Right.Openness = 1.0f - Math.Clamp(
            values[13] + values[5] * values[29], 0.0f, 1.0f);
        ResetPupilDilation();
        if (_pupil.Current(Environment.TickCount64) is { } pupil)
        {
            if (!_adjustments.Passthrough("PupilLeft")) UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = pupil.Left * 10;
            if (!_adjustments.Passthrough("PupilRight")) UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = pupil.Right * 10;
        }

        UpdateEyeExpressions(values);
    }

    private static void UpdateEyeExpressions(ReadOnlySpan<float> values)
    {
        Set(5, values[0]); Set(7, values[0]);
        Set(4, values[1]); Set(6, values[1]);
        Set(9, values[22]); Set(8, values[23]);
        Set(1, values[28]); Set(0, values[29]);
        Set(11, values[57]); Set(10, values[58]);
        Set(3, values[59]); Set(2, values[60]);
    }

    private void UpdateMouth(ReadOnlySpan<float> values, byte faceFlags)
    {
        foreach ((int source, int[] targets) in ExpressionMap)
            foreach (int target in targets)
                Set(target, values[source]);

        Set(31, Math.Min(1.0f - MathF.Pow(values[61], 1.0f / 6.0f), values[45]));
        Set(30, Math.Min(1.0f - MathF.Pow(values[62], 1.0f / 6.0f), values[47]));

        bool customFresh = _tongueEnabled && _lastTongueTick != 0 &&
            Environment.TickCount64 - _lastTongueTick <= TongueTimeoutMs;
        int mask = customFresh ? _tongueMask : 0;
        for (int index = 0; index < TongueNames.Length; index++)
            if (_adjustments.Passthrough(TongueNames[index])) mask &= ~(1 << index);
        for (int index = 0; index < TongueExpressions.Length; index++)
        {
            if ((mask & (1 << index)) == 0) Set(TongueExpressions[index], 0);
            else if (_tongueDirty) Set(TongueExpressions[index], _tongueValues[index]);
        }
        SetStockTongue(79, 0, mask);
        if ((faceFlags & 2) != 0)
        {
            SetStockTongue(72, values[63], mask);
            SetStockTongue(76, values[64], mask);
            SetStockTongue(75, values[65], mask);
            SetStockTongue(73, values[66], mask);
            SetStockTongue(74, values[67], mask);
        }
        else
        {
            SetStockTongue(72, values[68], mask);
            SetStockTongue(79, values[64], mask);
            SetStockTongue((int)UnifiedExpressions.TongueBendDown, values[69], mask);
        }
        _tongueDirty = false;
    }

    private static void SetStockTongue(int expression, float value, int mask)
    {
        int index = Array.IndexOf(TongueExpressions, expression);
        if (index < 0 || (mask & (1 << index)) == 0) Set(expression, value);
    }

    private static void Set(int expression, float value) =>
        UnifiedTracking.Data.Shapes[expression].Weight = value;

    private static float ReadFloat(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));

    private static (float x, float y) QuaternionToCartesian(byte[] bytes, int offset)
    {
        var q = MemoryMarshal.Read<Quaternion>(bytes.AsSpan(offset, 16));
        if (q.Length() <= 1e-6f)
            return (0.0f, 0.0f);
        q = Quaternion.Normalize(q);
        float first = MathF.Asin(Math.Clamp(2.0f * (q.X * q.Z - q.W * q.Y), -1.0f, 1.0f));
        float second = MathF.Atan2(
            2.0f * (q.Y * q.Z + q.W * q.X),
            q.W * q.W - q.X * q.X - q.Y * q.Y + q.Z * q.Z);
        return (first, second);
    }
}
