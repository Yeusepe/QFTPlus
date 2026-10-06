using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace QFTPlus;

internal static class SteamVr
{
    static readonly Lock steamLinkLock = new();
    static (long Until, bool Value) steamLink;
    internal static bool IsSteamLink
    {
        get
        {
            lock (steamLinkLock)
            {
                if (Environment.TickCount64 < steamLink.Until) return steamLink.Value;
                bool value;
                try { value = Running() && Settings()?["LastKnown"]?["ActualHMDDriver"]?.GetValue<string>() == "vrlink"; }
                catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException) { value = false; }
                steamLink = (Environment.TickCount64 + 3000, value);
                return value;
            }
        }
    }

    internal static JsonNode? OpenVrPaths() =>
        CalibrationSettings.ReadJson(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath"));

    static readonly string SteamLinkBefore = Path.Combine(WorkingCopy.Home, "steamlink-before.json");
    static readonly (string Key, JsonValue Value)[] SteamLink =
        [("useOSC", JsonValue.Create(true)), ("useOSCFace", JsonValue.Create(true)), ("shareEyeTrackingData", JsonValue.Create(true)), ("OSCOutPort", JsonValue.Create(9015))];

    internal static void ConfigureSteamLink()
    {
        if (!IsSteamLink) return;
        if (!File.Exists(SteamLinkBefore))
        {
            var link = Settings()?["driver_vrlink"];
            var before = new JsonObject();
            foreach (var (key, _) in SteamLink) before[key] = link?[key]?.DeepClone();
            CalibrationSettings.WriteJson(SteamLinkBefore, before);
        }
        WithSettings("Steam Link settings", table => { foreach (var (key, value) in SteamLink) SetSteamLink(table, key, value); });
    }

    internal static void RestoreSteamLink()
    {
        if (!File.Exists(SteamLinkBefore)) return;
        var before = CalibrationSettings.ReadJson(SteamLinkBefore);
        if (Settings() is { } settings && settings["driver_vrlink"] is JsonObject link && RevertSteamLink(link, before) is { Count: > 0 } changed)
        {
            if (Running()) WithSettings("Restoring Steam Link settings", table => { foreach (var key in changed) SetSteamLink(table, key, link[key]); });
            else CalibrationSettings.WriteJson(SettingsFile()!, settings);
        }
        File.Delete(SteamLinkBefore);
    }

    internal static List<string> RevertSteamLink(JsonObject link, JsonObject before)
    {
        var changed = new List<string>();
        foreach (var (key, ours) in SteamLink)
        {
            if (!JsonNode.DeepEquals(link[key], ours)) continue;
            changed.Add(key);
            if (before[key] is { } value) link[key] = value.DeepClone();
            else link.Remove(key);
        }
        return changed;
    }

    static void SetSteamLink(nint table, string key, JsonNode? value)
    {
        int error = 0;
        if (value is null) Function<RemoveKey>(table, 10)("driver_vrlink", key, ref error);
        else if (value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) Function<SetBool>(table, 1)("driver_vrlink", key, value.GetValue<bool>(), ref error);
        else Function<SetInt>(table, 2)("driver_vrlink", key, value.GetValue<int>(), ref error);
        if (error != 0) throw new IOException($"Steam Link didn’t accept {key} ({error}).");
    }

    static JsonObject? Settings() => SettingsFile() is { } file ? CalibrationSettings.ReadJson(file) : null;

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
        var settings = CalibrationSettings.ReadJson(file);
        if (settings["driver_qftplus"] is not JsonObject driver) settings["driver_qftplus"] = driver = new JsonObject();
        foreach (var (key, value) in values) driver[key] = value.DeepClone();
        CalibrationSettings.WriteJson(file, settings);
        return false;
    }

    internal static bool Unblock()
    {
        try
        {
            var file = SettingsFile();
            var settings = file is null ? null : CalibrationSettings.ReadJson(file);
            if (settings?["driver_qftplus"] is not JsonObject driver || driver["blocked_by_safe_mode"] is not JsonValue blocked
                || blocked.GetValueKind() != System.Text.Json.JsonValueKind.True) return false;
            if (Running())
                WithSettings("Unblocking the driver", table =>
                {
                    int error = 0;
                    Function<RemoveKey>(table, 10)("driver_qftplus", "blocked_by_safe_mode", ref error);
                    if (error != 0) throw new IOException($"SteamVR didn’t lift its safe-mode block on the driver ({error}).");
                });
            else { driver.Remove("blocked_by_safe_mode"); CalibrationSettings.WriteJson(file!, settings); }
            return true;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or ArgumentOutOfRangeException
            or System.ComponentModel.Win32Exception)
        { throw new IOException("SteamVR’s settings can’t be read: " + error.Message, error); }
    }

    internal static void RemoveSettings()
    {
        if (Running())
        {
            WithSettings("Removing the QFT+ settings", table =>
            {
                int error = 0;
                Function<RemoveSection>(table, 9)("driver_qftplus", ref error);
                if (error != 0) throw new IOException($"SteamVR kept the QFT+ settings ({error}).");
            });
            return;
        }
        if (SettingsFile() is { } file && CalibrationSettings.ReadJson(file) is var settings && settings.Remove("driver_qftplus")) CalibrationSettings.WriteJson(file, settings);
    }

    internal static bool Running() => Processes.Running("vrserver");

    static string? SettingsFile() => OpenVrPaths()?["config"]?[0]?.GetValue<string>() is { } config ? Path.Combine(config, "steamvr.vrsettings") : null;

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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RemoveSection([MarshalAs(UnmanagedType.LPStr)] string section, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RemoveKey([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetString([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string value, ref int error);
}
