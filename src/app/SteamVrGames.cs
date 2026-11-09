using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace QFTPlus;

internal static partial class SteamVr
{
    internal sealed record Game(string Key, string Name, string Binary);

    internal static List<Game> Games()
    {
        var games = new List<Game>();
        WithInterface(Loc.Get("GameProfilesTitle"), "IVRApplications_008", table =>
        {
            var count = Math.Min(Function<AppCount>(table, 3)(), 10000u);
            for (uint i = 0; i < count; i++)
            {
                var key = new StringBuilder(1024);
                if (Function<AppKey>(table, 4)(i, key, 1024) != 0) continue;
                int error = 0;
                if (Function<AppBool>(table, 15)(key.ToString(), 60, ref error)) continue;
                string Property(int property)
                {
                    var value = new StringBuilder(4096);
                    int err = 0;
                    Function<AppString>(table, 14)(key.ToString(), property, value, 4096, ref err);
                    return err == 0 ? value.ToString() : "";
                }
                var name = Property(0);
                if (name.Length > 0) games.Add(new(key.ToString(), name, Property(13)));
            }
        });
        return games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static string CurrentGame()
    {
        string key = "";
        WithInterface(Loc.Get("GameProfilesTitle"), "IVRApplications_008", table =>
        {
            var pid = Function<AppCount>(table, 30)();
            if (pid == 0) return;
            var value = new StringBuilder(1024);
            var error = Function<AppKey>(table, 5)(pid, value, 1024);
            if (error != 0) throw new IOException(Loc.Format("GameProfileReadError", error));
            key = value.ToString();
        });
        return key;
    }

    internal static void WithVideoSettings(Action<Func<GameProfiles.Setting, JsonNode>, Action<GameProfiles.Setting, JsonNode>> use)
    {
        WithSettings(Loc.Get("GameProfilesTitle"), table =>
        {
            JsonNode Read(GameProfiles.Setting setting)
            {
                int error = 0;
                JsonNode value = setting.Kind switch
                {
                    "bool" => JsonValue.Create(Function<GetBool>(table, 5)(setting.Section, setting.Key, ref error)),
                    "int" => JsonValue.Create(Function<GetInt>(table, 6)(setting.Section, setting.Key, ref error)),
                    _ => JsonValue.Create(Function<GetFloat>(table, 7)(setting.Section, setting.Key, ref error))
                };
                if (error == 5 && setting.Key == "sharpening") return JsonValue.Create(false);
                if (error != 0) throw new IOException(Loc.Format("GameProfileSettingError", setting.Key, error));
                return value;
            }
            void Write(GameProfiles.Setting setting, JsonNode value)
            {
                int error = 0;
                if (setting.Kind == "bool") Function<SetBool>(table, 1)(setting.Section, setting.Key, value.GetValue<bool>(), ref error);
                else if (setting.Kind == "int") Function<SetInt>(table, 2)(setting.Section, setting.Key, value.GetValue<int>(), ref error);
                else Function<SetFloat>(table, 3)(setting.Section, setting.Key, value.GetValue<float>(), ref error);
                if (error != 0) throw new IOException(Loc.Format("GameProfileSettingError", setting.Key, error));
            }
            use(Read, Write);
        });
    }

    internal static void RestoreVideoSettings(Action<Func<GameProfiles.Setting, JsonNode>, Action<GameProfiles.Setting, JsonNode>> restore)
    {
        lock (apiLock)
        {
            if (Running()) { WithVideoSettings(restore); return; }
            var file = SettingsFile() ?? throw new IOException(Loc.Get("GameProfileStartSteamVr"));
            var settings = Session.Read(file);
            restore(setting => settings[setting.Section]?[setting.Key]?.DeepClone() ?? JsonValue.Create("unset"), (setting, value) =>
            {
                // A newly started runtime owns its file; leave the journal for the next live recovery.
                if (Running()) throw new IOException(Loc.Get("GameProfileRuntimeChanged"));
                if (settings[setting.Section] is not JsonObject section) settings[setting.Section] = section = new JsonObject();
                section[setting.Key] = value.DeepClone();
                CalibrationSettings.WriteJson(file, settings);
            });
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AppCount();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AppKey(uint index, StringBuilder buffer, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AppString([MarshalAs(UnmanagedType.LPStr)] string key, int property, StringBuilder buffer, uint size, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool AppBool([MarshalAs(UnmanagedType.LPStr)] string key, int property, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetBool([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetInt([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float GetFloat([MarshalAs(UnmanagedType.LPStr)] string section, [MarshalAs(UnmanagedType.LPStr)] string key, ref int error);
}
