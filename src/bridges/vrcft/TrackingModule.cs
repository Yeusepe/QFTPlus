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
    private const int RuntimePort = 27275;
    private const long GazeTimeoutMs = 250;
    private const long GazeHoldMs = 1000;
    private const float BlinkClosingPerSecond = 3;
    private const long BlinkSettleMs = 150;
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
        (61, [45, 47]), (62, [44, 46]),
        (0, [5, 7]), (1, [4, 6]), (22, [9]), (23, [8]),
        (28, [1]), (29, [0]), (57, [11]), (58, [10]),
        (59, [3]), (60, [2])
    ];

    private readonly byte[] _first = new byte[StateBytes];
    private readonly byte[] _second = new byte[StateBytes];
    private readonly byte[] _packet = new byte[65536];
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _mapTick;
    private UdpClient? _steamLinkSocket;
    private readonly SteamLinkState _steamLink = new();
    private readonly byte[] _steamLinkBytes = new byte[StateBytes];
    private IPEndPoint _labelsEndpoint = new(IPAddress.Loopback, 27274);
    private static readonly JsonSerializerOptions LabelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private UdpClient? _labelSocket;
    private readonly byte[] _lastLabelState = new byte[StateBytes];
    private long _labelSequence, _labelChanges, _labelChangeQpc, _labelSchemaQpc, _labelSampleQpc;
    private readonly byte[] _lastVdState = new byte[StateBytes];
    private long _lastVdChange;
    private string? _source;
    private UdpClient? _runtimeSocket;
    private readonly PupilDilationState _pupil = new();
    private readonly ExtraFaceState _extraFace = new();
    private readonly HeadsetState _headset = new();
    private bool _needsEye;
    private bool _needsExpression;
    private long _lastGazeTick;
    private readonly float[] _gaze = new float[4];
    private readonly long[] _heldTick = new long[2], _lidTicks = new long[2];
    private readonly float[] _lids = new float[2];
    private long _blinkTick;
    private byte _gazeFlags;
    private readonly float[] _tongueValues = new float[12];
    private ushort _tongueMask = 0xFFF;
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
    private const int ShapeCount = (int)UnifiedExpressions.Max + 1;
    private readonly float[] _shapes = new float[ShapeCount], _input = new float[ShapeCount], _output = new float[ShapeCount];
    private readonly bool[] _owned = new bool[ShapeCount];
    private readonly float[] _eyes = new float[8], _eyeOutput = new float[8];
    private bool _eyesOwned;
    private volatile bool _headsetDirect;
    private static readonly (int Index, string Name)[] Shapes = Enum.GetValues<UnifiedExpressions>()
        .Where(e => e.ToString() != "Max" && (int)e >= 0 && (int)e < ShapeCount).Select(e => ((int)e, e.ToString())).ToArray();
    private static readonly Dictionary<string, int> ShapeIndices = Shapes.ToDictionary(s => s.Name, s => s.Index);
    private static readonly (string Left, string Right, int LeftIndex, int RightIndex)[] SharePairs = ExtraFaceState.SharePairs
        .Select(p => (p + "Left", p + "Right", (int)Enum.Parse<UnifiedExpressions>(p + "Left"), (int)Enum.Parse<UnifiedExpressions>(p + "Right"))).ToArray();
    private readonly OutputAdjustments _adjustments = new();
    private string? _settingsRoot;
    private long _outputTick;
    private string? _settingsConfigPath;
    private DateTime _settingsConfigTime;
    private long _settingsConfigTick, _outputStatusTick, _headsetAdjustTick;
    private JsonObject? _outputStatus;
    private bool _outputStatusFailed;
    private readonly ManualResetEvent _packetSignal = new(false);
    private EventWaitHandle? _frameSignal;
    private WaitHandle[] _wakeSignals = [];
    private readonly Lock _gate = new();
    private bool _closed;
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

        _runtimeSocket = Bind(RuntimePort, "Could not bind QFT+ port {Port}; factory tracking remains available");
        _steamLinkSocket = Bind(SteamLinkState.Port, "Steam Link port {Port} unavailable. Disable the standalone SteamLink module and restart VRCFaceTracking");
        if (_steamLinkSocket is not null) _steamLinkSocket.Client.ReceiveBufferSize = 1024 * 1024;
        TryOpenMap(Environment.TickCount64);
        _labelSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        StartLocalRuntime();
        Logger.LogInformation(
            "Quest Pro Virtual Desktop / Steam Link bridge initialized (eye={Eye}, face={Face}); " +
            "custom gaze falls back to the active streaming app after {Timeout} ms",
            _needsEye, _needsExpression, GazeTimeoutMs);
        return (_needsEye, _needsExpression);
    }

    private UdpClient? Bind(int port, string failure)
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
            Logger.LogError(error, failure, port);
            return null;
        }
    }

    private void WakeOnPackets(UdpClient socket)
    {
        if (WSAEventSelect(socket.Client.Handle, _packetSignal.SafeWaitHandle.DangerousGetHandle(), FdRead) != 0)
            Logger.LogWarning("QFT+ socket wake-up unavailable ({Error}); packets are read on the {Wait} ms fallback", Marshal.GetLastWin32Error(), WaitMs);
        _wakeSignals = _frameSignal is null ? [_packetSignal] : [_frameSignal, _packetSignal];
    }

    private void Receive(UdpClient? socket, int limit, long now)
    {
        if (socket is null) return;
        try
        {
            for (int i = 0; i < limit && socket.Available > 0; i++)
            {
                var packet = _packet.AsSpan(0, socket.Client.Receive(_packet));
                if (socket == _steamLinkSocket) _steamLink.Accept(packet, now);
                else if (packet.StartsWith("{"u8)) _extraFace.Accept(packet, now);
                else if (!AcceptGaze(packet, now) && !AcceptTongue(packet, now)) _pupil.Accept(packet, now);
            }
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending) { }
    }

    public override void Update()
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_wakeSignals.Length > 0) WaitHandle.WaitAny(_wakeSignals, WaitMs);
            else Thread.Sleep(WaitMs);
            _packetSignal.Reset();
            long now = Environment.TickCount64;
            Receive(_runtimeSocket, 64, now);
            if (_headset.Poll(now)) AcceptHeadset(now);
            if (!TryReadState(now)) Array.Clear(_second);

            var expressions = MemoryMarshal.Cast<byte, float>(_second.AsSpan(ExpressionOffset, ExpressionCount * sizeof(float)));

            if (_needsEye)
                UpdateEyes(expressions);
            if (_needsExpression)
                UpdateMouth(expressions, _second[0]);
            foreach (var (source, targets) in ExpressionMap)
                foreach (int target in targets)
                    if (Wanted(target)) Set(target, expressions[source]);
            _shapes.CopyTo(_input, 0);
            var shares = _extraFace.CurrentShares(now);
            foreach (var (leftName, rightName, left, right) in SharePairs)
            {
                if (!shares.TryGetValue(leftName, out float shareLeft) || !shares.TryGetValue(rightName, out float shareRight)) continue;
                if (!Wanted(left)) continue;
                var (fusedLeft, fusedRight) = ExtraFaceState.Split(_shapes[left], _shapes[right], shareLeft, shareRight);
                if (!_adjustments.Passthrough(leftName)) Set(_input, left, fusedLeft);
                if (!_adjustments.Passthrough(rightName)) Set(_input, right, fusedRight);
            }
            foreach (var entry in _extraFace.Current(now))
            {
                if (_adjustments.Passthrough(entry.Key) || !ShapeIndices.TryGetValue(entry.Key, out int index)) continue;
                if (!Wanted(index)) continue;
                Set(_input, index, entry.Value);
            }
            AdjustOutput(now);
            if (_headset.AdjustRequest is { } request) AdjustFromHeadset(request, now);
            if (_headsetDirect && !_headset.Fresh(now)) return;
            Publish(_output, _eyeOutput);
        }
    }

    public override void Teardown()
    {
        lock (_gate)
        {
            _closed = true;
            _runtimeSocket?.Dispose();
            _steamLinkSocket?.Dispose();
            _labelSocket?.Dispose();
            _headset.Dispose();
            if (_needsEye) ResetPupilDilation();
            Publish(_shapes, _eyes);
            _frameSignal?.Dispose();
            _view?.Dispose();
            _map?.Dispose();
        }
    }

    private bool Wanted(int shape) => shape <= 11 ? _needsEye : _needsExpression;

    private void StartLocalRuntime()
    {
        string configPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "QproAutoStart.json");
        _settingsConfigPath = configPath;
        try
        {
            var config = ReadAutoStart() ?? throw new InvalidDataException("QproAutoStart.json is unreadable");
            if (config.Count == 0) return;
            string root = _settingsRoot ?? throw new InvalidDataException("QproAutoStart.json has no root");
            if (config["headsetStandalone"]?.GetValue<bool>() == true) return;
            if (config["startWithVrcft"]?.GetValueKind() == JsonValueKind.False) return;
            string app = config["studioApp"]?.GetValue<string>() ?? Path.Combine(root, "QproFaceTracking.exe");
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
    private static readonly int[] EyePartners = Array.ConvertAll(EyeKeys, name => Array.IndexOf(EyeKeys, OutputAdjustments.Partner(name)));
    private void Publish(float[] values, float[] eyes)
    {
        var shapes = UnifiedTracking.Data.Shapes;
        for (int i = 0; i < ShapeCount && i < shapes.Length; i++)
            if (_owned[i]) shapes[i].Weight = values[i];
        if (!_eyesOwned) return;
        var eye = UnifiedTracking.Data.Eye;
        eye.Left.Gaze.x = eyes[0]; eye.Left.Gaze.y = eyes[1]; eye.Right.Gaze.x = eyes[2]; eye.Right.Gaze.y = eyes[3];
        eye.Left.PupilDiameter_MM = eyes[4] * 10; eye.Right.PupilDiameter_MM = eyes[5] * 10;
        eye.Left.Openness = eyes[6]; eye.Right.Openness = eyes[7];
        eye._minDilation = 0.0f; eye._maxDilation = 10.0f;
    }
    private JsonObject? ReadAutoStart()
    {
        var config = OutputAdjustments.Read(_settingsConfigPath!, ref _settingsConfigTime);
        if (config?["root"] is JsonValue folder && folder.TryGetValue(out string? root) && root.Length > 0) _settingsRoot = Path.GetFullPath(root);
        if (config is not null)
        {
            _headset.Configure(config["headsetStandalone"]?.GetValue<bool>() == true && config["headsetReceiverEnabled"]?.GetValue<bool>() != false ? config["headsetPairKey"]?.GetValue<string>() ?? "" : "");
            _headsetDirect = config["headsetStandalone"]?.GetValue<bool>() == true && config["headsetDirect"]?.GetValue<bool>() == true;
        }
        return config;
    }
    private void AdjustOutput(long tick)
    {
        var utc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
        _input.CopyTo(_output, 0); _eyes.CopyTo(_eyeOutput, 0);
        if (_settingsConfigPath is not null && tick - _settingsConfigTick >= 1000)
        {
            _settingsConfigTick = tick;
            try { ReadAutoStart(); }
            catch (Exception error) when (error is ArgumentException or SocketException) { }
        }
        if (_settingsRoot is null) return;
        var dt=_outputTick==0?.005:(tick-_outputTick)/1000.0; _outputTick=tick;
        _adjustments.Poll(_settingsRoot,tick,utc);
        var report = tick - _outputStatusTick >= 250;
        if (report)
        {
            _outputStatusTick = tick;
            try { report = DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(_settingsRoot, "output-status.lease")) <= TimeSpan.FromSeconds(3); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { report = false; }
            report |= tick - _headsetAdjustTick < 3000;
        }
        JsonObject inputs = new(), outputs = new(); JsonArray parameters = new();
        void Adjust(string name, float[] from, float[] to, int index, float neutral, float minimum, float maximum, int partner)
        {
            if (_adjustments.Enabled(name) || (to == _output ? _owned[index] : _eyesOwned) && _adjustments.Smoothed(name))
            {
                to[index] = _adjustments.Apply(name, from[index], neutral, minimum, maximum, dt, utc, partner < 0 ? null : from[partner]);
                if (to == _output) _owned[index] = true;
            }
            if (!report) return;
            parameters.Add(new JsonObject { ["name"] = name, ["area"] = OutputAdjustments.Area(name), ["minimum"] = minimum, ["maximum"] = maximum,
                ["neutral"] = neutral, ["modeled"] = OutputAdjustments.Modeled(name), ["partner"] = OutputAdjustments.Partner(name) });
            inputs[name] = float.IsFinite(from[index]) ? from[index] : 0;
            outputs[name] = float.IsFinite(to[index]) ? to[index] : 0;
        }
        foreach (var (index, name) in Shapes)
            if (Wanted(index)) Adjust(name, _input, _output, index, 0, 0, 1, ShapePartners.GetValueOrDefault(index, -1));
        for (int i = 0; _needsEye && i < EyeKeys.Length; i++)
            Adjust(EyeKeys[i], _eyes, _eyeOutput, i, i is 4 or 5 ? .5f : i >= 6 ? 1 : 0, i < 4 ? -1.2f : 0, i < 4 ? 1.2f : 1, EyePartners[i]);
        if (!report) return;
        try
        {
            var status = new JsonObject { ["updated"] = utc, ["settings"] = _adjustments.Settings.DeepClone(),
                ["inputs"] = inputs, ["outputs"] = outputs, ["pupilTracking"] = _pupil.Current(tick) is not null };
            _outputStatus = status.DeepClone().AsObject();
            _outputStatus["parameters"] = parameters; _outputStatus["areas"] = new JsonArray(OutputAdjustments.Areas.Select(a => (JsonNode?)a).ToArray());
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

    private void AdjustFromHeadset(byte[] request, long now)
    {
        _headset.AdjustRequest = null; _headsetAdjustTick = now;
        if (_settingsRoot is null) return;
        if (request.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(request) is not JsonObject settings) return;
                var path = Path.Combine(_settingsRoot, "output-settings.json");
                File.WriteAllText(path + ".tmp", settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                Logger?.LogWarning(error, "Could not save adjustments from the headset");
            }
        }
        if (_outputStatus is not null) _headset.ReplyAdjustments(JsonSerializer.SerializeToUtf8Bytes(_outputStatus));
    }

    private void TryOpenMap(long now)
    {
        if (_view is not null && _frameSignal is not null || now - _mapTick < 1000) return;
        _mapTick = now;
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _map?.Dispose();
            _map = null;
        }
    }

    private bool TryReadState(long now)
    {
        if (_headset.CopyTo(_second, now)) return true;
        Receive(_steamLinkSocket, 512, now);
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

    private void AcceptHeadset(long now)
    {
        var p = _headset.Current;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]), status = BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
        string[] names = ["CheekPuffLeft", "CheekPuffRight", "CheekSuckLeft", "CheekSuckRight", "BrowInnerUpLeft", "BrowInnerUpRight", "BrowOuterUpLeft", "BrowOuterUpRight", "BrowLowererLeft", "BrowLowererRight", "BrowPinchLeft", "BrowPinchRight"];
        var values = new Dictionary<string, float>(); var shares = new Dictionary<string, float>();
        for (int i = 0; i < 12; i++)
            if (i < 4 ? (flags & 8) != 0 : i >= 8 && (flags & 16) != 0) values[names[i]] = BinaryPrimitives.ReadSingleLittleEndian(p[(20 + i * 4)..]);
        for (int i = 0; i < 4; i++) if ((flags & 16) != 0) shares[names[i + 4]] = BinaryPrimitives.ReadSingleLittleEndian(p[(68 + i * 4)..]);
        _extraFace.Accept(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, enabled = (flags & 1) != 0, values, shares }), now);
        Span<byte> pupil = stackalloc byte[16]; pupil.Clear(); "QPPD"u8.CopyTo(pupil); pupil[4] = 1; pupil[5] = (byte)((status >> 3) & 1);
        p.Slice(96, 8).CopyTo(pupil[8..]); _pupil.Accept(pupil, now);
        Span<byte> tongue = stackalloc byte[56]; tongue.Clear(); "QPTO"u8.CopyTo(tongue); tongue[4] = 3; tongue[5] = (byte)((flags >> 1) & 1); tongue[6] = 31;
        if ((status & 4) != 0)
        {
            float extension = BinaryPrimitives.ReadSingleLittleEndian(p[84..]), horizontal = BinaryPrimitives.ReadSingleLittleEndian(p[88..]), vertical = BinaryPrimitives.ReadSingleLittleEndian(p[92..]);
            float[] directions = [extension, vertical, -vertical, -horizontal, horizontal];
            for (int i = 0; i < 5; i++) BinaryPrimitives.WriteSingleLittleEndian(tongue[(8 + i * 4)..], Math.Clamp(directions[i], 0, 1));
        }
        AcceptTongue(tongue, now);
    }

    private void SendLabels(string source)
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _labelSchemaQpc)
        {
            SendLabel(new { V = 1, Type = "schema", Names = SteamLinkState.ExpressionNames });
            _labelSchemaQpc = now + Stopwatch.Frequency * 2;
        }
        if (!_second.AsSpan().SequenceEqual(_lastLabelState)) { _second.CopyTo(_lastLabelState, 0); _labelChangeQpc = now; _labelChanges++; }
        else if (now - _labelSampleQpc < Stopwatch.Frequency / 20) return;
        _labelSampleQpc = now;
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
        TryOpenMap(now);
        if (_view is null)
            return false;
        try
        {
            _view.ReadArray(0, _first, 0, StateBytes);
            _view.ReadArray(0, _second, 0, StateBytes);
            if (!_first.AsSpan().SequenceEqual(_second)) _lastVdState.CopyTo(_second, 0);
            else if (!_second.AsSpan().SequenceEqual(_lastVdState))
            {
                _second.CopyTo(_lastVdState, 0);
                _lastVdChange = now;
            }
            return Packets.Fresh(_lastVdChange, now, 1000);
        }
        catch (ObjectDisposedException)
        {
            _view = null;
            _map?.Dispose();
            _map = null;
            return false;
        }
    }

    private void ResetPupilDilation()
    {
        _eyes[4] = _eyes[5] = .5f;
        _eyesOwned = true;
    }

    private bool AcceptGaze(ReadOnlySpan<byte> packet, long now)
    {
        if (!Packets.Header(packet, "QPGE"u8, 1, 24)) return false;
        var gaze = MemoryMarshal.Cast<byte, float>(packet[8..]);
        foreach (float value in gaze) if (!float.IsFinite(value)) return false;
        _gazeFlags = packet[5];
        gaze.CopyTo(_gaze);
        _lastGazeTick = now;
        return true;
    }

    private bool AcceptTongue(ReadOnlySpan<byte> packet, long now)
    {
        if (!Packets.Header(packet, "QPTO"u8, 3, 56)) return false;
        var raw = MemoryMarshal.Cast<byte, float>(packet[8..]);
        foreach (float value in raw) if (!float.IsFinite(value)) return false;
        for (int index = 0; index < _tongueValues.Length; index++) _tongueValues[index] = Math.Clamp(raw[index], 0.0f, 1.0f);
        _tongueMask = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) & 0xFFF);
        _tongueEnabled = (packet[5] & 1) != 0;
        _tongueDirty = true;
        _lastTongueTick = now;
        return true;
    }

    private void UpdateEyes(ReadOnlySpan<float> values)
    {
        long now = Environment.TickCount64;
        bool customFresh = Packets.Fresh(_lastGazeTick, now, GazeTimeoutMs);
        for (int side = 0; side < 2; side++)
        {
            var lid = values[12 + side];
            if (lid == _lids[side]) continue;
            if (_lidTicks[side] != 0 && (lid - _lids[side]) * 1000 / Math.Max(1, now - _lidTicks[side]) > BlinkClosingPerSecond) _blinkTick = now;
            (_lids[side], _lidTicks[side]) = (lid, now);
        }
        for (int side = 0; side < 2; side++)
        {
            if (now - _blinkTick <= BlinkSettleMs)
            {
                _heldTick[side] = now;
                continue;
            }
            var eye = _eyes.AsSpan(side * 2, 2);
            bool valid = _second[292 + side] != 0;
            bool nativeX = _adjustments.Passthrough(EyeKeys[side * 2]), nativeY = _adjustments.Passthrough(EyeKeys[side * 2 + 1]);
            if (customFresh && (_gazeFlags & (1 << side)) != 0 && !(nativeX && nativeY))
            {
                (float x, float y) = valid ? QuaternionToCartesian(_second, 296 + side * 28) : (eye[0], eye[1]);
                eye[0] = nativeX ? x : _gaze[side * 2];
                eye[1] = nativeY ? y : _gaze[side * 2 + 1];
            }
            else if (valid) (eye[0], eye[1]) = QuaternionToCartesian(_second, 296 + side * 28);
            else
            {
                if (now - _heldTick[side] > GazeHoldMs) eye.Clear();
                continue;
            }
            _heldTick[side] = now;
        }

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
    }

    private void UpdateMouth(ReadOnlySpan<float> values, byte faceFlags)
    {
        Set(31, Math.Min(1.0f - MathF.Pow(values[61], 1.0f / 6.0f), values[45]));
        Set(30, Math.Min(1.0f - MathF.Pow(values[62], 1.0f / 6.0f), values[47]));

        bool customFresh = _tongueEnabled && Packets.Fresh(_lastTongueTick, Environment.TickCount64, TongueTimeoutMs);
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

    private void Set(int expression, float value) => Set(_shapes, expression, value);

    private void Set(float[] shapes, int expression, float value)
    {
        shapes[expression] = value;
        _owned[expression] = true;
    }

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
