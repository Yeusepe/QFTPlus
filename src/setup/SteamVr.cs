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
                if (error != 0) throw new IOException($"Steam Link settings could not connect to SteamVR ({error}).");
                initialized = true;
                nint table = Export<GetInterface>("VR_GetGenericInterface")("FnTable:IVRSettings_003", ref error);
                if (table == 0 || error != 0) throw new IOException($"SteamVR settings API is unavailable ({error}).");
                T Function<T>(int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
                foreach (var key in new[] { "useOSC", "useOSCFace", "shareEyeTrackingData" })
                {
                    Function<SetBool>(1)("driver_vrlink", key, true, ref error);
                    if (error != 0) throw new IOException($"Steam Link could not enable {key} ({error}).");
                }
                Function<SetInt>(2)("driver_vrlink", "OSCOutPort", 9015, ref error);
                if (error != 0) throw new IOException($"Steam Link could not select OSC port 9015 ({error}).");
            }
            finally
            {
                if (initialized) Marshal.GetDelegateForFunctionPointer<Shutdown>(NativeLibrary.GetExport(library, "VR_ShutdownInternal"))();
                NativeLibrary.Free(library);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Init(ref int error, int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetInterface([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetBool([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.I1)] bool value, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetInt([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, int value, ref int error);
}
