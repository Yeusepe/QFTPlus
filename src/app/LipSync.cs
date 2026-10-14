using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace QFTPlus;

internal sealed class LipSync : IDisposable
{
    internal const string Headset = "headset";
    internal static readonly string[] Visemes = ["sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou"];
    internal static bool Vowel(string name) => Array.IndexOf(Visemes, name) >= 10;
    internal static string Parameter(string viseme) => "Viseme" + (viseme == "sil" ? "Sil" : viseme.ToUpperInvariant());
    internal static readonly string[] Engines = ["Original", "Enhanced", "Laughter"];
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal sealed class Viseme
    {
        public bool On { get; set; } = true;
        public bool WithTracking { get; set; }
        public double Strength { get; set; } = 1;
    }

    internal sealed class Settings
    {
        public bool Enabled { get; set; }
        public string Device { get; set; } = "";
        public string Engine { get; set; } = "Enhanced";
        public double Gain { get; set; } = 1;
        public double GateMargin { get; set; } = 20;
        public int Smoothing { get; set; } = 70;
        public int FadeInMs { get; set; } = 20;
        public int FadeOutMs { get; set; } = 80;
        public int FrameMs { get; set; } = 10;
        public bool Index { get; set; }
        public string IndexName { get; set; } = "Viseme";
        public string WeightName { get; set; } = "VisemeWeight";
        public string Laughter { get; set; } = "";
        public Dictionary<string, Viseme> Visemes { get; set; } = [];

        internal Viseme Of(string name) => Visemes.TryGetValue(name, out var viseme) ? viseme : Visemes[name] = new() { WithTracking = !Vowel(name) };
        internal Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;
    }

    internal static Settings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    internal static void Save(string path, Settings settings)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        File.Move(temporary, path, true);
    }

    internal static (string Id, string Name)[] Devices()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToArray();
    }

    internal static string DefaultDevice()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).FriendlyName;
        }
        catch (COMException) { return ""; }
    }

    internal string Status
    {
        get => capture is HeadsetMicrophone { Connected: false } ? Loc.Get("HeadsetMicrophoneWaiting")
            : !Active(Environment.TickCount64) ? status
            : Matched == 0 ? Loc.Get("ListeningNoAvatarParameters") : Loc.Format("ListeningDelay", LatencyMs);
        private set => status = value;
    }
    string status = Loc.Get("LipSyncOff");
    internal bool Listening => capture is not null;
    internal bool Enabled => settings.Enabled;
    internal float Level = -100;
    internal readonly float[] Output = new float[Visemes.Length];
    internal int LatencyMs;

    readonly string library;
    readonly Func<bool> faceTracking;
    readonly Func<IWaveIn>? headset;
    readonly object sync = new();
    readonly Timer retry;
    Settings settings = new();
    Native? ovr;
    IWaveIn? capture;
    long fed;
    uint context;
    (int Rate, int Frame, string Engine, int Smoothing) built;
    float[] frame = [];
    int filled, frameNumber, frameDelay;
    float laughter;
    NoiseGate gate = new();
    readonly float[] raw = new float[Visemes.Length], eased = new float[Visemes.Length];
    UdpClient? udp;
    readonly IPEndPoint? target;
    volatile int vrchatPort = VRChatOsc.DefaultPort, queryPort;
    long discoverAt, avatarAt;
    HashSet<string>? avatar;
    readonly Dictionary<string, string[]> routes = [];
    readonly Dictionary<string, float> sent = [];
    long refreshed;
    bool activeSent;
    internal int? Matched { get; private set; }

    internal static readonly string[] DefaultPrefixes = ["FT/v2/", "v2/"];

    internal static string[] Addresses(string name, IReadOnlySet<string>? avatar, string[] fallback) =>
        avatar is null ? fallback.Select(prefix => prefix + name).ToArray() : avatar.Where(a => a == name || a.EndsWith("/" + name, StringComparison.Ordinal)).ToArray();

    internal LipSync(string root, Func<bool> faceTracking, Func<IWaveIn>? headset = null, IPEndPoint? target = null)
    {
        library = Path.Combine(root, "lipsync", "OVRLipSync.dll");
        (this.faceTracking, this.headset, this.target) = (faceTracking, headset, target);
        retry = new(_ => Watch(), null, 1000, 1000);
    }

    void Watch()
    {
        if (settings.Enabled && capture is null) Start();
        if (settings.Enabled && target is null && Environment.TickCount64 >= discoverAt)
        {
            discoverAt = Environment.TickCount64 + 30_000;
            var (osc, query) = VRChatOsc.Find(TimeSpan.FromSeconds(1.5));
            (vrchatPort, queryPort) = (osc ?? vrchatPort, query ?? queryPort);
        }
        if (settings.Enabled && queryPort != 0 && Environment.TickCount64 >= avatarAt)
        {
            avatarAt = Environment.TickCount64 + 3_000;
            var found = VRChatOsc.AvatarParameters(queryPort);
            if (found is null) (queryPort, discoverAt) = (0, Math.Min(discoverAt, Environment.TickCount64 + 5_000));
            lock (sync)
                if (found is null != avatar is null || found is not null && !found.SetEquals(avatar!))
                    (avatar, refreshed) = (found, 0);
        }
        lock (sync)
            if (activeSent && !Active(Environment.TickCount64))
            {
                Level = -100;
                Array.Clear(Output);
                Array.Clear(eased);
                Send(true);
            }
    }

    bool Active(long now) => capture is not null && now - fed < 1000;

    internal void Apply(Settings changed)
    {
        var next = changed.Copy();
        lock (sync)
        {
            var reopen = next.Device != settings.Device || next.Enabled != settings.Enabled;
            if (reopen) Stop();
            else if (next.Index != settings.Index || next.IndexName != settings.IndexName
                || next.WeightName != settings.WeightName || next.Laughter != settings.Laughter
                || Visemes.Skip(1).Any(v => settings.Of(v).On && !next.Of(v).On)) Silence();
            settings = next;
            if (!reopen) return;
        }
        if (next.Enabled) Start();
        else Status = Loc.Get("LipSyncOff");
    }

    void Start()
    {
        lock (sync)
        {
            if (capture is not null || !settings.Enabled) return;
            try
            {
                ovr ??= File.Exists(library) ? new Native(library) : throw new FileNotFoundException(Loc.Get("LipSyncMissing"));
                var started = settings.Device == Headset && headset is not null ? headset() : Windows(settings.Device);
                started.DataAvailable += (_, e) => { if (capture == started) Feed(started.WaveFormat, e.Buffer, e.BytesRecorded); };
                started.RecordingStopped += (_, e) =>
                {
                    lock (sync) if (capture == started)
                    {
                        capture = null; Silence();
                        if (e.Exception is not null) Status = started is HeadsetMicrophone ? e.Exception.Message : Loc.Format("MicrophoneUnavailable", e.Exception.Message);
                    }
                    started.Dispose();
                };
                capture = started;
                try { started.StartRecording(); }
                catch { capture = null; started.Dispose(); throw; }
                if (capture == started) Status = Loc.Get("Listening");
            }
            catch (Exception error) when (error is COMException or IOException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
            {
                Status = error is FileNotFoundException ? error.Message : Loc.Format("MicrophoneUnavailable", error.Message);
            }
        }
    }

    static WasapiCapture Windows(string id)
    {
        using var devices = new MMDeviceEnumerator();
        return new(id.Length > 0 ? devices.GetDevice(id) : devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console), true, 10);
    }

    void Stop()
    {
        var stopping = capture;
        capture = null;
        stopping?.StopRecording();
        Silence();
    }

    void Silence()
    {
        Array.Clear(Output);
        Array.Clear(eased);
        Level = -100;
        gate = new();
        (frameNumber, filled, laughter) = (0, 0, 0);
        Send(true);
    }

    internal void Feed(WaveFormat format, byte[] buffer, int bytes)
    {
        lock (sync)
        {
            ovr ??= File.Exists(library) ? new Native(library) : null;
            if (ovr is null) return;
            fed = Environment.TickCount64;
            var s = settings;
            var size = format.SampleRate * Math.Clamp(s.FrameMs, 5, 40) / 1000;
            if (built != (format.SampleRate, size, s.Engine, s.Smoothing)) Build(format.SampleRate, size, s);
            var bits = format.BitsPerSample;
            var isFloat = (format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format).Encoding == WaveFormatEncoding.IeeeFloat;
            for (var at = 0; at + format.BlockAlign <= bytes; at += format.BlockAlign)
            {
                var sum = 0f;
                for (var channel = 0; channel < format.Channels; channel++) sum += Sample(buffer, at + channel * bits / 8, bits, isFloat);
                frame[filled++] = sum / format.Channels * (float)s.Gain;
                if (filled < frame.Length) continue;
                filled = 0;
                Frame(s, format.SampleRate);
            }
        }
    }

    static float Sample(byte[] b, int at, int bits, bool isFloat) => bits switch
    {
        32 when isFloat => BitConverter.ToSingle(b, at),
        32 => BitConverter.ToInt32(b, at) / 2147483648f,
        24 => (b[at] | b[at + 1] << 8 | (sbyte)b[at + 2] << 16) / 8388608f,
        16 => BitConverter.ToInt16(b, at) / 32768f,
        _ => 0,
    };

    void Build(int rate, int size, Settings s)
    {
        if (context != 0) ovr!.DestroyContext(context);
        if (built.Rate != rate || built.Frame != size)
        {
            if (built.Rate != 0) ovr!.Shutdown();
            Check(ovr!.Initialize(rate, size));
        }
        context = 0;
        Check(ovr!.CreateContextEx(ref context, Math.Max(0, Array.IndexOf(Engines, s.Engine)), rate, true));
        ovr.SendSignal(context, 3, Math.Clamp(s.Smoothing, 1, 100), 0);
        (built, frame, filled) = ((rate, size, s.Engine, s.Smoothing), new float[size], 0);
    }

    static void Check(int result)
    {
        if (result != 0) throw new IOException(Loc.Format("OVRLipSyncError", result));
    }

    void Frame(Settings s, int rate)
    {
        var energy = 0f;
        foreach (var x in frame) energy += x * x;
        Level = 10 * MathF.Log10(energy / frame.Length + 1e-10f);
        if (!gate.Update(Level, (float)frame.Length / rate, s.GateMargin)) Array.Clear(frame);
        if (ovr!.ProcessFrameEx(context, frame, (uint)frame.Length, 2, ref frameNumber, ref frameDelay, raw, raw.Length, ref laughter, null, 0) != 0) return;
        LatencyMs = 10 + frame.Length * 1000 / rate + frameDelay;
        Mix(s, raw, faceTracking(), Output);
        Ease(s, Output, eased, (float)frame.Length / rate);
        Send(false);
    }

    internal static void Ease(Settings s, float[] output, float[] state, float seconds)
    {
        var rise = 1 - MathF.Exp(-seconds * 1000 / Math.Max(1, s.FadeInMs));
        var fall = 1 - MathF.Exp(-seconds * 1000 / Math.Max(1, s.FadeOutMs));
        for (var v = 0; v < output.Length; v++)
        {
            state[v] += (output[v] > state[v] ? rise : fall) * (output[v] - state[v]);
            output[v] = state[v];
        }
    }

    internal static void Mix(Settings s, float[] raw, bool tracking, float[] output)
    {
        output[0] = raw[0];
        for (var v = 1; v < Visemes.Length; v++)
        {
            var viseme = s.Of(Visemes[v]);
            output[v] = !viseme.On || tracking && !viseme.WithTracking ? 0 : Math.Clamp(raw[v] * (float)viseme.Strength, 0, 1);
        }
    }

    internal sealed class NoiseGate
    {
        const float Hysteresis = 6, HoldSeconds = .1f, FallSeconds = .1f, RisePerSecond = 1, Lowest = -90;
        float background = float.NaN, held;
        internal bool Open { get; private set; }

        internal bool Update(float level, float seconds, double margin)
        {
            background = float.IsNaN(background) ? level
                : level < background ? background + (level - background) * Math.Min(1, seconds / FallSeconds)
                : Math.Min(level, background + RisePerSecond * seconds);
            background = Math.Max(background, Lowest);
            var threshold = background + (float)margin;
            if (level >= threshold) (Open, held) = (true, HoldSeconds);
            else if (level < threshold - Hysteresis && (held -= seconds) <= 0) Open = false;
            return Open;
        }
    }

    internal static int Binary(float value, int bits) => Math.Min((1 << bits) - 1, (int)(value * (1 << bits)));

    void Send(bool all)
    {
        var s = settings;
        var now = Environment.TickCount64;
        if (now - refreshed > 1000)
        {
            (all, refreshed) = (true, now);
            routes.Clear();
        }
        string[] Route(string name, string[] fallback) =>
            routes.TryGetValue(name, out var addresses) ? addresses : routes[name] = Addresses(name, avatar, fallback);
        var values = new List<(string Name, object Value)>();
        if (s.Index)
        {
            var (best, weight) = (0, 0f);
            for (var v = 1; v < Visemes.Length; v++) if (Output[v] > weight) (best, weight) = (v, Output[v]);
            values.Add((s.IndexName, best));
            values.Add((s.WeightName, weight));
        }
        else
            for (var v = 0; v < Visemes.Length; v++)
                if (v == 0 || s.Of(Visemes[v]).On)
                {
                    var name = Parameter(Visemes[v]);
                    values.Add((name, Output[v]));
                    if (v == 0) continue;
                    var depth = avatar is null ? 3 : Enumerable.Range(0, 8).TakeWhile(k => Route(name + (1 << k), DefaultPrefixes).Length > 0).Count();
                    var code = Binary(Output[v], depth);
                    for (var k = 0; k < depth; k++) values.Add((name + (1 << k), (code >> k & 1) != 0));
                }
        if (s.Laughter.Length > 0) values.Add((s.Laughter, laughter));
        var messages = new List<byte[]>();
        var matched = 0;
        void Add(string name, object value, string[] fallback)
        {
            var number = value switch { int i => i, bool b => b ? 1 : 0, _ => (float)value };
            var addresses = Route(name, fallback);
            matched += addresses.Length;
            foreach (var address in addresses)
            {
                if (!all && sent.TryGetValue(address, out var last) && Math.Abs(last - number) < .004f) continue;
                sent[address] = number;
                messages.Add(Message("/avatar/parameters/" + address, value));
            }
        }
        foreach (var (name, value) in values) Add(name, value, DefaultPrefixes);
        activeSent = Active(now);
        Matched = avatar is null ? null : matched;
        if (messages.Count == 0) return;
        try
        {
            udp ??= new UdpClient(AddressFamily.InterNetwork);
            var packet = Bundle(messages);
            udp.Send(packet, packet.Length, target ?? new IPEndPoint(IPAddress.Loopback, vrchatPort));
        }
        catch (SocketException) { udp?.Dispose(); udp = null; }
    }

    internal static byte[] Message(string address, object value)
    {
        using var stream = new MemoryStream();
        void Pad(string text)
        {
            var ascii = Encoding.ASCII.GetBytes(text);
            stream.Write(ascii);
            stream.Write(new byte[4 - ascii.Length % 4]);
        }
        Pad(address);
        Pad(value switch { int => ",i", bool on => on ? ",T" : ",F", _ => ",f" });
        if (value is bool) return stream.ToArray();
        Span<byte> payload = stackalloc byte[4];
        if (value is int number) BinaryPrimitives.WriteInt32BigEndian(payload, number);
        else BinaryPrimitives.WriteSingleBigEndian(payload, (float)value);
        stream.Write(payload);
        return stream.ToArray();
    }

    internal static byte[] Bundle(List<byte[]> messages)
    {
        using var stream = new MemoryStream();
        stream.Write("#bundle\0"u8);
        stream.Write([0, 0, 0, 0, 0, 0, 0, 1]);
        Span<byte> size = stackalloc byte[4];
        foreach (var message in messages)
        {
            BinaryPrimitives.WriteInt32BigEndian(size, message.Length);
            stream.Write(size);
            stream.Write(message);
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        retry.Dispose();
        lock (sync)
        {
            Stop();
            udp?.Dispose();
            if (context != 0) ovr?.DestroyContext(context);
            context = 0;
        }
    }

    sealed class Native
    {
        internal readonly InitializeFn Initialize;
        internal readonly ShutdownFn Shutdown;
        internal readonly CreateContextFn CreateContextEx;
        internal readonly DestroyContextFn DestroyContext;
        internal readonly SendSignalFn SendSignal;
        internal readonly ProcessFrameFn ProcessFrameEx;

        internal Native(string path)
        {
            var library = NativeLibrary.Load(path);
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, "ovrLipSyncDll_" + name));
            (Initialize, Shutdown, CreateContextEx) = (Export<InitializeFn>("Initialize"), Export<ShutdownFn>("Shutdown"), Export<CreateContextFn>("CreateContextEx"));
            (DestroyContext, SendSignal, ProcessFrameEx) = (Export<DestroyContextFn>("DestroyContext"), Export<SendSignalFn>("SendSignal"), Export<ProcessFrameFn>("ProcessFrameEx"));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int InitializeFn(int sampleRate, int bufferSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ShutdownFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateContextFn(ref uint context, int provider, int sampleRate, [MarshalAs(UnmanagedType.I1)] bool acceleration);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DestroyContextFn(uint context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SendSignalFn(uint context, int signal, int arg1, int arg2);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ProcessFrameFn(uint context, float[] audio, uint samples, int dataType, ref int frameNumber,
            ref int frameDelay, float[] visemes, int visemeCount, ref float laughter, float[]? laughterCategories, int laughterCategoryCount);
    }
}
