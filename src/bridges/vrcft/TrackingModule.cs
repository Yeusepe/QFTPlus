using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
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
    private readonly OutputAdjustments _adjustments = new();
    private readonly Dictionary<int, float> _outputBaseline = new();
    private float[]? _eyeBaseline;
    private string? _settingsRoot;
    private long _outputTick;

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

        try
        {
            _gazeSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, GazePort));
            _gazeSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        {
            Logger.LogError(error, "Could not bind the local Quest Pro gaze port {Port}", GazePort);
        }

        try
        {
            _tongueSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, TonguePort));
            _tongueSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        {
            Logger.LogError(error, "Could not bind the local Quest Pro tongue port {Port}", TonguePort);
        }

        TryOpenMap();
        try
        {
            _extraFaceSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 27278));
            _extraFaceSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        { Logger.LogWarning(error, "Extra-face research port is unavailable; stock face tracking continues"); }
        try
        {
            _pupilSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 27279));
            _pupilSocket.Client.Blocking = false;
        }
        catch (SocketException error)
        { Logger.LogWarning(error, "Pupil dilation port unavailable; neutral pupil size continues"); }
        StartLocalRuntime();
        Logger.LogInformation(
            "Quest Pro full Virtual Desktop bridge initialized (eye={Eye}, face={Face}); " +
            "custom gaze falls back to Virtual Desktop after {Timeout} ms",
            _needsEye, _needsExpression, GazeTimeoutMs);
        return (_needsEye, _needsExpression);
    }

    public override void Update()
    {
        ReceiveGaze();
        ReceiveTongue();
        ReceiveExtraFace();
        ReceivePupilDilation();
        RestoreAdjustedOutput();
        RestoreExtraFaceBaseline();
        if (!TryReadState())
        {
            if (_needsEye) ResetPupilDilation();
            _adjustments.Reset();
            AdjustOutput(manualOnly: true);
            Thread.Sleep(10);
            return;
        }

        Span<float> expressions = stackalloc float[ExpressionCount];
        for (int index = 0; index < ExpressionCount; ++index)
            expressions[index] = BitConverter.ToSingle(_second, ExpressionOffset + index * 4);

        if (_needsEye)
            UpdateEyes(expressions);
        if (_needsExpression && (_second[0] & 1) != 0)
            UpdateMouth(expressions, _second[0]);
        if ((_second[0] & 1) != 0)
            foreach (var entry in _extraFace.Current(Environment.TickCount64))
            {
                if (!Enum.TryParse<UnifiedExpressions>(entry.Key, out var expression)) continue;
                int index = (int)expression;
                if (index <= 11 ? !_needsEye : !_needsExpression) continue;
                _extraBaseline[index] = UnifiedTracking.Data.Shapes[index].Weight;
                Set(index, entry.Value);
            }
        AdjustOutput();
        Thread.Sleep(5);
    }

    public override void Teardown()
    {
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
    private void AdjustOutput(bool manualOnly = false)
    {
        if (_settingsRoot is null) return;
        var tick=Environment.TickCount64; var utc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
        var dt=_outputTick==0?.005:(tick-_outputTick)/1000.0; _outputTick=tick;
        _adjustments.Poll(_settingsRoot,tick,utc);
        foreach (var shape in Enum.GetValues<UnifiedExpressions>())
        {
            var index=(int)shape; var name=shape.ToString();
            if (name=="Max" || index<0 || index>=UnifiedTracking.Data.Shapes.Length || (index<=11?!_needsEye:!_needsExpression)) continue;
            if (!_adjustments.Enabled(name) || (manualOnly&&!_adjustments.Manual(name,utc))) continue;
            var value=UnifiedTracking.Data.Shapes[index].Weight; _outputBaseline[index]=value;
            Set(index,_adjustments.Apply(name,value,0,0,1,dt,utc));
        }
        if (!_needsEye) return;
        var eyes=ReadEyes(); var changed=false;
        for(var i=0;i<EyeKeys.Length;i++)
        {
            var name=EyeKeys[i];
            if (!_adjustments.Enabled(name) || (manualOnly&&!_adjustments.Manual(name,utc))) continue;
            _eyeBaseline??=(float[])eyes.Clone(); changed=true;
            eyes[i]=_adjustments.Apply(name,eyes[i],i is 4 or 5?.5f:i>=6?1:0,i<4?-1.2f:0,i<4?1.2f:1,dt,utc);
        }
        if(changed)WriteEyes(eyes);
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

    private void ReceiveExtraFace()
    {
        if (_extraFaceSocket is null) return;
        try
        {
            for (int i = 0; i < 16 && _extraFaceSocket.Available > 0; i++)
            {
                IPEndPoint sender = new(IPAddress.Loopback, 0);
                _extraFace.Accept(_extraFaceSocket.Receive(ref sender), Environment.TickCount64);
            }
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending) { }
    }

    private void RestoreExtraFaceBaseline()
    {
        foreach (var entry in _extraBaseline) Set(entry.Key, entry.Value);
        _extraBaseline.Clear();
    }

    private bool TryReadState()
    {
        TryOpenMap();
        if (_view is null)
            return false;
        try
        {
            _view.ReadArray(0, _first, 0, StateBytes);
            Thread.MemoryBarrier();
            _view.ReadArray(0, _second, 0, StateBytes);
            return _first.AsSpan().SequenceEqual(_second);
        }
        catch (ObjectDisposedException)
        {
            _view = null;
            _map = null;
            return false;
        }
    }

    private void ReceivePupilDilation()
    {
        if (_pupilSocket is null) return;
        try
        {
            for (int i = 0; i < 16 && _pupilSocket.Available > 0; i++)
            {
                IPEndPoint sender = new(IPAddress.Loopback, 0);
                _pupil.Accept(_pupilSocket.Receive(ref sender), Environment.TickCount64);
            }
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        { }
    }

    private static void ResetPupilDilation()
    {
        UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 5.0f;
        UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = 5.0f;
        UnifiedTracking.Data.Eye._minDilation = 0.0f;
        UnifiedTracking.Data.Eye._maxDilation = 10.0f;
    }

    private void ReceiveGaze()
    {
        if (_gazeSocket is null)
            return;
        try
        {
            while (_gazeSocket.Available >= GazePacketBytes)
            {
                IPEndPoint sender = new(IPAddress.Loopback, 0);
                byte[] packet = _gazeSocket.Receive(ref sender);
                if (packet.Length != GazePacketBytes ||
                    packet[0] != (byte)'Q' || packet[1] != (byte)'P' ||
                    packet[2] != (byte)'G' || packet[3] != (byte)'E' ||
                    packet[4] != 1)
                    continue;
                _gazeFlags = packet[5];
                _leftGazeX = ReadFloat(packet, 8);
                _leftGazeY = ReadFloat(packet, 12);
                _rightGazeX = ReadFloat(packet, 16);
                _rightGazeY = ReadFloat(packet, 20);
                _lastGazeTick = Environment.TickCount64;
            }
        }
        catch (SocketException error) when (
            error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
        }
    }

    private void ReceiveTongue()
    {
        if (_tongueSocket is null)
            return;
        try
        {
            for (int attempt = 0; attempt < 16 && _tongueSocket.Available > 0; attempt++)
            {
                IPEndPoint sender = new(IPAddress.Loopback, 0);
                byte[] packet = _tongueSocket.Receive(ref sender);
                if (packet.Length < 8 ||
                    packet[0] != (byte)'Q' || packet[1] != (byte)'P' ||
                    packet[2] != (byte)'T' || packet[3] != (byte)'O' ||
                    !((packet[4] == 1 && packet.Length == TonguePacketBytes) ||
                      (packet[4] == 2 && packet.Length == 72)))
                    continue;
                bool valid = true;
                for (int index = 0; index < _tongueValues.Length; index++)
                    valid &= float.IsFinite(ReadFloat(packet, 8 + index * 4));
                if (!valid) continue;
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
            }
        }
        catch (SocketException error) when (
            error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
        }
    }

    private void UpdateEyes(ReadOnlySpan<float> values)
    {
        bool leftValid = _second[292] != 0;
        bool rightValid = _second[293] != 0;
        bool customFresh = _lastGazeTick != 0 &&
            Environment.TickCount64 - _lastGazeTick <= GazeTimeoutMs;

        if (customFresh && (_gazeFlags & 1) != 0)
        {
            UnifiedTracking.Data.Eye.Left.Gaze.x = _leftGazeX;
            UnifiedTracking.Data.Eye.Left.Gaze.y = _leftGazeY;
        }
        else if (leftValid)
        {
            (float x, float y) = QuaternionToCartesian(_second, 296);
            UnifiedTracking.Data.Eye.Left.Gaze.x = x;
            UnifiedTracking.Data.Eye.Left.Gaze.y = y;
        }

        if (customFresh && (_gazeFlags & 2) != 0)
        {
            UnifiedTracking.Data.Eye.Right.Gaze.x = _rightGazeX;
            UnifiedTracking.Data.Eye.Right.Gaze.y = _rightGazeY;
        }
        else if (rightValid)
        {
            (float x, float y) = QuaternionToCartesian(_second, 324);
            UnifiedTracking.Data.Eye.Right.Gaze.x = x;
            UnifiedTracking.Data.Eye.Right.Gaze.y = y;
        }

        UnifiedTracking.Data.Eye.Left.Openness = 1.0f - Math.Clamp(
            values[12] + values[4] * values[28], 0.0f, 1.0f);
        UnifiedTracking.Data.Eye.Right.Openness = 1.0f - Math.Clamp(
            values[13] + values[5] * values[29], 0.0f, 1.0f);
        ResetPupilDilation();
        if (leftValid && rightValid && UnifiedTracking.Data.Eye.Left.Openness > .3f &&
            UnifiedTracking.Data.Eye.Right.Openness > .3f && _pupil.Current(Environment.TickCount64) is { } pupil)
        {
            UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = pupil.Left * 10;
            UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = pupil.Right * 10;
        }

        if ((_second[0] & 1) != 0)
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
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));

    private static (float x, float y) QuaternionToCartesian(byte[] bytes, int offset)
    {
        float x = BitConverter.ToSingle(bytes, offset);
        float y = BitConverter.ToSingle(bytes, offset + 4);
        float z = BitConverter.ToSingle(bytes, offset + 8);
        float w = BitConverter.ToSingle(bytes, offset + 12);
        float length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (length <= 1e-6f)
            return (0.0f, 0.0f);
        x /= length; y /= length; z /= length; w /= length;
        float first = MathF.Asin(Math.Clamp(2.0f * (x * z - w * y), -1.0f, 1.0f));
        float second = MathF.Atan2(
            2.0f * (y * z + w * x),
            w * w - x * x - y * y + z * z);
        return (first, second);
    }
}
