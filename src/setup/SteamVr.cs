using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

internal static class SteamVr
{
    internal static bool IsSteamLink
    {
        get
        {
            var processes = Process.GetProcessesByName("vrserver");
            try
            {
                if (processes.Length == 0) return false;
                var paths = JsonNode.Parse(File.ReadAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr/openvrpaths.vrpath")));
                var config = paths?["config"]?[0]?.GetValue<string>();
                return config is not null && JsonNode.Parse(File.ReadAllText(Path.Combine(config, "steamvr.vrsettings")))?
                    ["LastKnown"]?["ActualHMDDriver"]?.GetValue<string>() == "vrlink";
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return false; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    internal static void ConfigureSteamLink()
    {
        if (!IsSteamLink) return;
        WithSettings("Steam Link settings", table =>
        {
            int error = 0;
            foreach (var key in new[] { "useOSC", "useOSCFace", "shareEyeTrackingData" })
            {
                Function<SetBool>(table, 1)("driver_vrlink", key, true, ref error);
                if (error != 0) throw new IOException($"Steam Link could not enable {key} ({error}).");
            }
            Function<SetInt>(table, 2)("driver_vrlink", "OSCOutPort", 9015, ref error);
            if (error != 0) throw new IOException($"Steam Link could not select OSC port 9015 ({error}).");
        });
    }

    internal static bool SetThumbrest(IReadOnlyDictionary<string, JsonValue> values)
    {
        if (Running())
        {
            WithSettings("The thumbrest setting", table =>
            {
                foreach (var (key, value) in values)
                {
                    int error = 0;
                    if (value.TryGetValue<string>(out var text)) Function<SetString>(table, 4)("driver_qftplus", key, text, ref error);
                    else Function<SetFloat>(table, 3)("driver_qftplus", key, (float)value.GetValue<double>(), ref error);
                    if (error != 0) throw new IOException($"SteamVR didn’t accept the thumbrest setting {key} ({error}).");
                }
            });
            return true;
        }
        var file = SettingsFile() ?? throw new IOException("SteamVR isn’t set up yet. Start SteamVR once, then try again.");
        var settings = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject();
        if (settings["driver_qftplus"] is not JsonObject driver) settings["driver_qftplus"] = driver = new JsonObject();
        foreach (var (key, value) in values) driver[key] = value.DeepClone();
        Save(file, settings);
        return false;
    }

    internal static bool Unblock()
    {
        try
        {
            var file = SettingsFile();
            var settings = file is not null && File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
            if (settings?["driver_qftplus"] is not JsonObject driver || driver["blocked_by_safe_mode"] is not JsonValue blocked
                || blocked.GetValueKind() != System.Text.Json.JsonValueKind.True) return false;
            if (Running())
                WithSettings("Unblocking the driver", table =>
                {
                    int error = 0;
                    Function<RemoveKey>(table, 10)("driver_qftplus", "blocked_by_safe_mode", ref error);
                    if (error != 0) throw new IOException($"SteamVR didn’t lift its safe-mode block on the driver ({error}).");
                });
            else { driver.Remove("blocked_by_safe_mode"); Save(file!, settings); }
            return true;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new IOException("SteamVR’s settings can’t be read: " + error.Message, error); }
    }

    internal static bool Running()
    {
        var processes = Process.GetProcessesByName("vrserver");
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    static string? SettingsFile()
    {
        var paths = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr/openvrpaths.vrpath")));
        return paths?["config"]?[0]?.GetValue<string>() is { } config ? Path.Combine(config, "steamvr.vrsettings") : null;
    }

    static void Save(string file, JsonObject settings)
    {
        File.WriteAllText(file + ".tmp", settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }

    static void WithSettings(string what, Action<nint> use)
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            var directory = Path.GetDirectoryName(processes.First().MainModule!.FileName)!;
            nint library = NativeLibrary.Load(Path.Combine(directory, "openvr_api.dll"));
            bool initialized = false;
            try
            {
                T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
                int error = 0;
                Export<Init>("VR_InitInternal")(ref error, 3);
                if (error != 0) throw new IOException($"{what} could not connect to SteamVR ({error}).");
                initialized = true;
                nint table = Export<GetInterface>("VR_GetGenericInterface")("FnTable:IVRSettings_003", ref error);
                if (table == 0 || error != 0) throw new IOException($"SteamVR settings API is unavailable ({error}).");
                use(table);
            }
            finally
            {
                if (initialized) Marshal.GetDelegateForFunctionPointer<Shutdown>(NativeLibrary.GetExport(library, "VR_ShutdownInternal"))();
                NativeLibrary.Free(library);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static T Function<T>(nint table, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Init(ref int error, int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetInterface([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetBool([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.I1)] bool value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetInt([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, int value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetFloat([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, float value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RemoveKey([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetString([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string value, ref int error);
}
