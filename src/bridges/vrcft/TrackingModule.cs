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
    private const string FrameEventName = "VirtualDesktop.BodyStateEvent";
    private const int WaitMs = 10;
    private const int StateBytes = 360;
    private const int ExpressionOffset = 4;
    private const int ExpressionCount = 70;
    private const int GazePort = 27275;
    private const int GazePacketBytes = 24;
    private const long GazeTimeoutMs = 250;
    private const long GazeHoldMs = 1000;
    private const float BlinkClosed = 0.5f;
    private const long BlinkSettleMs = 100;
    private const int TonguePort = 27276;
    private const int TonguePacketBytes = 72;
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
    private IPEndPoint _labelsEndpoint = new(IPAddress.Loopback, 27274);
    private static readonly JsonSerializerOptions LabelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private UdpClient? _labelSocket;
    private readonly byte[] _lastLabelState = new byte[StateBytes];
    private long _labelSequence, _labelChanges, _labelChangeQpc, _labelSchemaQpc;
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
    private long _leftBlinkTick;
    private long _rightBlinkTick;
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
    private const int ShapeCount = (int)UnifiedExpressions.Max + 1;
    private readonly float[] _shapes = new float[ShapeCount];
    private readonly bool[] _owned = new bool[ShapeCount];
    private readonly float[] _eyes = new float[8], _rawEyes = new float[8], _eyeBaselineStore = new float[8];
    private bool _eyesOwned;
    private static readonly (int Index, string Name)[] Shapes = Enum.GetValues<UnifiedExpressions>()
        .Where(e => e.ToString() != "Max" && (int)e >= 0 && (int)e < ShapeCount).Select(e => ((int)e, e.ToString())).ToArray();
    private static readonly (string Left, string Right, int LeftIndex, int RightIndex)[] SharePairs = ExtraFaceState.SharePairs
        .Select(p => (p + "Left", p + "Right", (int)Enum.Parse<UnifiedExpressions>(p + "Left"), (int)Enum.Parse<UnifiedExpressions>(p + "Right"))).ToArray();
    private readonly OutputAdjustments _adjustments = new();
    private readonly Dictionary<int, float> _outputBaseline = new();
    private float[]? _eyeBaseline;
    private string? _settingsRoot;
    private long _outputTick;
    private string? _settingsConfigPath;
    private long _settingsConfigTick, _outputStatusTick;
    private bool _outputStatusFailed;
    private readonly ManualResetEvent _packetSignal = new(false);
    private EventWaitHandle? _frameSignal;
    private WaitHandle[] _wakeSignals = [];
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAEventSelect(IntPtr socket, IntPtr eventHandle, int networkEvents);
    private const int FdRead = 1;

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
            WakeOnPackets(_steamLinkSocket);
            _steamLinkSocket.Client.ReceiveBufferSize = 1024 * 1024;
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException)
        {
            Logger.LogError(error, "Steam Link port 9015 unavailable. Disable the standalone SteamLink module and restart VRCFaceTracking");
        }
        TryOpenMap();
        _labelSocket = new UdpClient(AddressFamily.InterNetwork);
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
            WakeOnPackets(socket);
            return socket;
        }
        catch (SocketException error)
        {
            Logger.Log(level, error, "Could not bind QFT+ {Feature} port {Port}; factory tracking remains available", feature, port);
            return null;
        }
    }

    private void WakeOnPackets(UdpClient socket)
    {
        if (WSAEventSelect(socket.Client.Handle, _packetSignal.SafeWaitHandle.DangerousGetHandle(), FdRead) != 0)
            Logger.LogWarning("QFT+ socket wake-up unavailable ({Error}); packets are read on the {Wait} ms fallback", Marshal.GetLastWin32Error(), WaitMs);
        _wakeSignals = _frameSignal is null ? [_packetSignal] : [_frameSignal, _packetSignal];
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
        if (_wakeSignals.Length > 0) WaitHandle.WaitAny(_wakeSignals, WaitMs);
        else Thread.Sleep(WaitMs);
        _packetSignal.Reset();
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
        foreach (var (leftName, rightName, left, right) in SharePairs)
        {
            if (!shares.TryGetValue(leftName, out float shareLeft) || !shares.TryGetValue(rightName, out float shareRight)) continue;
            if (left <= 11 ? !_needsEye : !_needsExpression) continue;
            float nativeLeft = _shapes[left], nativeRight = _shapes[right];
            _extraBaseline.TryAdd(left, nativeLeft); _extraBaseline.TryAdd(right, nativeRight);
            var (fusedLeft, fusedRight) = ExtraFaceState.Split(nativeLeft, nativeRight, shareLeft, shareRight);
            if (!_adjustments.Passthrough(leftName)) Set(left, fusedLeft);
            if (!_adjustments.Passthrough(rightName)) Set(right, fusedRight);
        }
        foreach (var entry in _extraFace.Current(Environment.TickCount64))
        {
            if (_adjustments.Passthrough(entry.Key) || !Enum.TryParse<UnifiedExpressions>(entry.Key, out var expression)) continue;
            int index = (int)expression;
            if (index <= 11 ? !_needsEye : !_needsExpression) continue;
            _extraBaseline.TryAdd(index, _shapes[index]);
            Set(index, entry.Value);
        }
        AdjustOutput();
        Publish();
    }

    public override void Teardown()
    {
        _steamLinkSocket?.Dispose(); _steamLinkSocket = null;
        _labelSocket?.Dispose(); _labelSocket = null;
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
        Publish();
        _wakeSignals = [];
        _frameSignal?.Dispose();
        _frameSignal = null;
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
                Path.Combine(root, "tracking.log"));
        }
        catch (Exception error)
        {
            Logger.LogError(error, "Qpro automatic runtime could not start; stock tracking remains available");
        }
    }

    private static readonly string[] EyeKeys = ["GazeLeftX", "GazeLeftY", "GazeRightX", "GazeRightY", "PupilLeft", "PupilRight", "OpennessLeft", "OpennessRight"];
    private void Publish()
    {
        var shapes = UnifiedTracking.Data.Shapes;
        for (int i = 0; i < ShapeCount && i < shapes.Length; i++)
            if (_owned[i]) shapes[i].Weight = _shapes[i];
        if (!_eyesOwned) return;
        var eye = UnifiedTracking.Data.Eye;
        eye.Left.Gaze.x = _eyes[0]; eye.Left.Gaze.y = _eyes[1]; eye.Right.Gaze.x = _eyes[2]; eye.Right.Gaze.y = _eyes[3];
        eye.Left.PupilDiameter_MM = _eyes[4] * 10; eye.Right.PupilDiameter_MM = _eyes[5] * 10;
        eye.Left.Openness = _eyes[6]; eye.Right.Openness = _eyes[7];
        eye._minDilation = 0.0f; eye._maxDilation = 10.0f;
    }
    private void RestoreAdjustedOutput()
    {
        foreach (var entry in _outputBaseline) Set(entry.Key, entry.Value);
        _outputBaseline.Clear();
        if (_eyeBaseline is not null) { Array.Copy(_eyeBaseline, _eyes, 8); _eyeBaseline=null; }
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
        if (_shapeInputs.Length != ShapeCount) _shapeInputs = new float[ShapeCount];
        Array.Copy(_shapes, _shapeInputs, ShapeCount);
        foreach (var (index, name) in Shapes)
        {
            if (index<=11?!_needsEye:!_needsExpression) continue;
            var value=_shapeInputs[index];
            if (report) inputs![name] = float.IsFinite(value) ? value : 0;
            if (_adjustments.Enabled(name))
            {
                _outputBaseline[index]=value;
                float? partner = ShapePartners.TryGetValue(index, out var other) && other < _shapeInputs.Length ? _shapeInputs[other] : null;
                Set(index,_adjustments.Apply(name,value,0,0,1,dt,utc,partner));
            }
            if (report) outputs![name] = float.IsFinite(_shapes[index]) ? _shapes[index] : 0;
        }
        if (_needsEye)
        {
            var raw=_rawEyes; Array.Copy(_eyes, raw, 8); var eyes=_eyes;
            for(var i=0;i<EyeKeys.Length;i++)
            {
                var name=EyeKeys[i];
                if (report) inputs![name] = float.IsFinite(eyes[i]) ? eyes[i] : 0;
                if (_adjustments.Enabled(name))
                {
                    if (_eyeBaseline is null) { _eyeBaseline = _eyeBaselineStore; Array.Copy(raw, _eyeBaseline, 8); }
                    var other=Array.IndexOf(EyeKeys,OutputAdjustments.Partner(name)??"");
                    eyes[i]=_adjustments.Apply(name,eyes[i],i is 4 or 5?.5f:i>=6?1:0,i<4?-1.2f:0,i<4?1.2f:1,dt,utc,other<0?null:raw[other]);
                }
                if (report) outputs![name] = float.IsFinite(eyes[i]) ? eyes[i] : 0;
            }
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
        if (_frameSignal is null && EventWaitHandle.TryOpenExisting(FrameEventName, out var frame))
        {
            _frameSignal = frame;
            _wakeSignals = [frame, _packetSignal];
        }
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
        bool vd = TryReadVirtualDesktop(now);
        if (steamLink) _steamLinkBytes.CopyTo(_second, 0);
        string source = steamLink ? "Steam Link" : vd ? "Virtual Desktop" : "waiting for tracking";
        if (source != _source)
        {
            _source = source;
            Logger?.LogInformation("QFT+ factory source: {Source}", source);
        }
        if (steamLink || vd) SendLabels(source);
        return steamLink || vd;
    }

    private void SendLabels(string source)
    {
        long now = Stopwatch.GetTimestamp();
        if (!_second.AsSpan().SequenceEqual(_lastLabelState)) { _second.CopyTo(_lastLabelState, 0); _labelChangeQpc = now; _labelChanges++; }
        if (now >= _labelSchemaQpc)
        {
            SendLabel(new { V = 1, Type = "schema", Names = SteamLinkState.ExpressionNames });
            _labelSchemaQpc = now + Stopwatch.Frequency * 2;
        }
        float[] Floats(int offset, int count) => MemoryMarshal.Cast<byte, float>(_second.AsSpan(offset, count * sizeof(float))).ToArray();
        SendLabel(new
        {
            V = 1, Type = "sample", Source = source, Sequence = ++_labelSequence, Qpc = now, QpcFrequency = Stopwatch.Frequency,
            UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), SourceChangeSequence = _labelChanges,
            SourceUnchangedMs = (now - _labelChangeQpc) * 1000.0 / Stopwatch.Frequency,
            Values = Floats(ExpressionOffset, ExpressionCount), FaceFlags = _second[0], IsEyeFollowingBlendshapesValid = _second[1] != 0,
            LeftEyeIsValid = _second[292] != 0, RightEyeIsValid = _second[293] != 0,
            LeftEyeOrientation = Floats(296, 4), LeftEyePosition = Floats(312, 3), RightEyeOrientation = Floats(324, 4), RightEyePosition = Floats(340, 3),
            LeftEyeConfidence = BitConverter.ToSingle(_second, 352), RightEyeConfidence = BitConverter.ToSingle(_second, 356)
        });
    }

    private void SendLabel<T>(T message)
    {
        try { var data = JsonSerializer.SerializeToUtf8Bytes(message, LabelJson); _labelSocket?.Send(data, data.Length, _labelsEndpoint); }
        catch (Exception error) when (error is SocketException or ArgumentException) { }
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

    private void ResetPupilDilation()
    {
        _eyes[4] = _eyes[5] = .5f;
        _eyesOwned = true;
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
            packet[4] != 2 || packet.Length != TonguePacketBytes)
            return;
        bool valid = true;
        for (int index = 0; index < _tongueValues.Length; index++)
            valid &= float.IsFinite(ReadFloat(packet, 8 + index * 4));
        if (!valid) return;
        _tongueMask = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)) & 0xFFF);
        _tongueEnabled = (packet[5] & 1) != 0;
        for (int index = 0; index < _tongueValues.Length; ++index)
            _tongueValues[index] = Math.Clamp(ReadFloat(packet, 8 + index * 4), 0.0f, 1.0f);
        if (Environment.TickCount64 - _lastTongueTimingTick >= 5000)
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
        if (values[12] > BlinkClosed) _leftBlinkTick = now;
        if (values[13] > BlinkClosed) _rightBlinkTick = now;

        bool leftX = _adjustments.Passthrough("GazeLeftX"), leftY = _adjustments.Passthrough("GazeLeftY");
        bool rightX = _adjustments.Passthrough("GazeRightX"), rightY = _adjustments.Passthrough("GazeRightY");
        if (now - _leftBlinkTick <= BlinkSettleMs) _leftGazeHeldTick = now;
        else if (customFresh && (_gazeFlags & 1) != 0 && !(leftX && leftY))
        {
            (float x, float y) = leftValid ? QuaternionToCartesian(_second, 296) : (_eyes[0], _eyes[1]);
            _eyes[0] = leftX ? x : _leftGazeX;
            _eyes[1] = leftY ? y : _leftGazeY;
            _leftGazeHeldTick = now;
        }
        else if (leftValid)
        {
            (float x, float y) = QuaternionToCartesian(_second, 296);
            _eyes[0] = x;
            _eyes[1] = y;
            _leftGazeHeldTick = now;
        }
        else if (now - _leftGazeHeldTick > GazeHoldMs) { _eyes[0] = 0; _eyes[1] = 0; }

        if (now - _rightBlinkTick <= BlinkSettleMs) _rightGazeHeldTick = now;
        else if (customFresh && (_gazeFlags & 2) != 0 && !(rightX && rightY))
        {
            (float x, float y) = rightValid ? QuaternionToCartesian(_second, 324) : (_eyes[2], _eyes[3]);
            _eyes[2] = rightX ? x : _rightGazeX;
            _eyes[3] = rightY ? y : _rightGazeY;
            _rightGazeHeldTick = now;
        }
        else if (rightValid)
        {
            (float x, float y) = QuaternionToCartesian(_second, 324);
            _eyes[2] = x;
            _eyes[3] = y;
            _rightGazeHeldTick = now;
        }
        else if (now - _rightGazeHeldTick > GazeHoldMs) { _eyes[2] = 0; _eyes[3] = 0; }

        _eyes[6] = 1.0f - Math.Clamp(
            values[12] + values[4] * values[28], 0.0f, 1.0f);
        _eyes[7] = 1.0f - Math.Clamp(
            values[13] + values[5] * values[29], 0.0f, 1.0f);
        ResetPupilDilation();
        if (_pupil.Current(Environment.TickCount64) is { } pupil)
        {
            if (!_adjustments.Passthrough("PupilLeft")) _eyes[4] = pupil.Left;
            if (!_adjustments.Passthrough("PupilRight")) _eyes[5] = pupil.Right;
        }

        UpdateEyeExpressions(values);
    }

    private void UpdateEyeExpressions(ReadOnlySpan<float> values)
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

    private void SetStockTongue(int expression, float value, int mask)
    {
        int index = Array.IndexOf(TongueExpressions, expression);
        if (index < 0 || (mask & (1 << index)) == 0) Set(expression, value);
    }

    private void Set(int expression, float value)
    {
        _shapes[expression] = value;
        _owned[expression] = true;
    }

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
