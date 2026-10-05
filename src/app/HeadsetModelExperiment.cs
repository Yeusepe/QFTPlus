namespace QFTPlus;

internal static class HeadsetModelExperiment
{
    internal const string Directory = "/data/local/tmp/qft-headset-model-v1";
    internal const string Worker = Directory + "/qft-headset-model";
    internal const string Key = "experimentalHeadsetModel";
    internal static bool Enabled(Session session) => session.Config[Key]?.GetValue<bool>() == true;
    internal static bool Pending(Session session) => session.Config["headsetModelCleanupPending"]?.GetValue<bool>() == true;
    internal static bool Standalone(Session session) => Enabled(session) && session.Config["headsetStandalone"]?.GetValue<bool>() == true;

    internal static async Task CleanupOnUninstallAsync(Session session)
    {
        if (!Enabled(session) && !Pending(session)) return;
        session.Save("headsetModelCleanupPending", true);
        session.Save(Key, false);
        session.Save("headsetReceiverEnabled", false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await EnsureAsync(session, session.Config["adbTarget"]?.GetValue<string>() ?? "", timeout.Token);
    }

    internal static async Task ConfigureAsync(Session session, bool enabled, CancellationToken token)
    {
        var updatingApp = enabled && Standalone(session);
        var previousDevice = session.Config["headsetModelDevice"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(previousDevice)) session.Save("headsetModelCleanupPending", true);
        session.Save(Key, false);
        if (session.Running) await session.Stop();
        if (!enabled && string.IsNullOrEmpty(previousDevice))
        {
            session.Save("headsetModelCleanupPending", false);
            session.Notify("Ready", "Tracking runs on this PC.");
            return;
        }
        session.Notify(enabled ? "Installing QFT+ Headset" : "Removing QFT+ Headset", "This takes a minute or two. Keep the headset awake and connected.");
        await Adb.EnsureAsync(Adb.Exe(session.Root), token: token);
        var config = session.Config;
        var device = await new SetupService(session.Root).ConnectQuestAsync(token,
            config["connectionMode"]?.GetValue<string>() ?? "auto", config["headsetSerial"]?.GetValue<string>() ?? "");
        if (config["headsetModelDevice"]?.GetValue<string>() is { Length: > 0 } owner && owner != device.Serial)
            throw new IOException("Connect the headset that has the experiment installed to finish removing it.");
        session.Save("adbTarget", device.Target);
        session.Save("headsetModelDevice", device.Serial);
        session.Save("headsetModelCleanupPending", true);
        await PythonRuntime.EnsureAsync(session.Root, token);
        try
        {
            await RunAsync(session, device.Target, "cleanup", token);
            if (enabled && session.Config["headsetPairKey"]?.GetValue<string>() is not { Length: 64 })
                session.Save("headsetPairKey", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            await RunAppAsync(session, device.Target, enabled ? "setup" : "cleanup", token);
            if (enabled)
            {
                SetupService.ValidateModuleFiles(session.Root, true);
                await new SetupService(session.Root).InstallModule(SetupService.ModuleSource(session.Root), SetupService.InstalledModule);
            }
            session.Save("headsetModelCleanupPending", false);
            session.Save(Key, enabled);
            session.Save("headsetStandalone", enabled);
            session.Save("headsetReceiverEnabled", enabled);
            if (!enabled) session.Save("headsetModelDevice", null);
            if (!enabled) session.Save("headsetPairKey", null);
            session.Notify("Ready", enabled ? "QFT+ Headset is installed and paired. Put on the headset and allow its root request the first time, then start tracking here." : "QFT+ Headset and its calibration are removed from the headset. Tracking runs on this PC.");
        }
        catch
        {
            if (updatingApp)
            {
                session.Save(Key, true);
                session.Save("headsetModelCleanupPending", false);
                throw;
            }
            try
            {
                await RunAsync(session, device.Target, "cleanup", CancellationToken.None);
                await RunAppAsync(session, device.Target, "cleanup", CancellationToken.None);
                session.Save("headsetModelCleanupPending", false);
                session.Save("headsetModelDevice", null);
            }
            catch (Exception error) { Session.Log(Path.Combine(session.Root, "headset-model.log"), "Cleanup pending: " + error.Message); }
            throw;
        }
    }

    internal static async Task EnsureAsync(Session session, string target, CancellationToken token)
    {
        if (!Enabled(session) && !Pending(session)) return;
        var serial = (await Processes.RunAsync(Adb.Exe(session.Root), ["-s", target, "shell", "getprop", "ro.serialno"], token, 10)).Text.Trim();
        if (serial.Length == 0 || serial != session.Config["headsetModelDevice"]?.GetValue<string>())
            throw new IOException("Connect the headset associated with the model experiment, or turn the experiment off on that headset first.");
        if (Pending(session))
        {
            await RunAsync(session, target, "cleanup", token);
            await RunAppAsync(session, target, "cleanup", token);
            session.Save("headsetModelCleanupPending", false);
            session.Save("headsetModelDevice", null);
            session.Save(Key, false);
            session.Save("headsetStandalone", false);
            session.Save("headsetReceiverEnabled", false);
            session.Save("headsetPairKey", null);
            return;
        }
        if (!Standalone(session)) await RunAsync(session, target, "ensure", token);
    }

    static async Task RunAppAsync(Session session, string target, string action, CancellationToken token)
    {
        var info = session.Info(session.Python, [Path.Combine(session.Root, "headset_app.py"), action, "--config", session.ConfigPath]);
        info.Environment["ANDROID_SERIAL"] = target;
        var result = await Processes.RunAsync(info, token, 240);
        Session.Log(Path.Combine(session.Root, "headset-model.log"), result.Text);
        if (result.Code != 0 || !result.Text.Contains(action == "cleanup" ? "HEADSET_APP_REMOVED" : "HEADSET_APP_READY", StringComparison.Ordinal))
            throw new IOException(result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "Headset app setup failed.");
    }

    static async Task RunAsync(Session session, string target, string action, CancellationToken token)
    {
        var info = session.Info(session.Python, [Path.Combine(session.Root, "headset_model.py"), action, "--config", session.ConfigPath]);
        info.Environment["ANDROID_SERIAL"] = target;
        var result = await Processes.RunAsync(info, token, 180);
        Session.Log(Path.Combine(session.Root, "headset-model.log"), result.Text);
        if (result.Code != 0)
            throw new IOException(result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                ?? "Headset model setup failed. See headset-model.log.");
        var expected = action == "cleanup" ? "HEADSET_MODEL_REMOVED" : "HEADSET_MODEL_READY";
        if (!result.Text.Contains(expected, StringComparison.Ordinal))
            throw new IOException("The headset did not confirm " + (action == "cleanup" ? "cleanup." : "model setup."));
    }
}
