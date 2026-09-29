using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace QproFaceTracking.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--verify-payload", var result])
        {
            try { using var zip = Payload(); Verify(zip); File.WriteAllText(result, "Payload verified"); }
            catch (Exception error) { File.WriteAllText(result, error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args is ["--extract-payload", var destination, var report])
        {
            try { File.WriteAllText(report, InstallPayload(destination)); }
            catch (Exception error) { File.WriteAllText(report, error.ToString()); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        if (args is ["--render-preview", var image])
        {
            using var form = new Installer();
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(image);
            return;
        }
        Application.Run(new Installer(installImmediately: args.Contains("--install-now")));
    }

    internal static ZipArchive Payload() => new(Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
        ?? throw new IOException("The installer is incomplete. Download the complete setup file again."), ZipArchiveMode.Read);

    internal static void Verify(ZipArchive archive) => PackageVerifier.Verify(archive);

    internal static string InstallPayload(string appsDirectory)
    {
        var apps = Path.GetFullPath(appsDirectory);
        var stage = Path.GetFullPath(Path.Combine(apps, "install-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var archive = Payload();
            Verify(archive);
            Directory.CreateDirectory(stage);
            archive.ExtractToDirectory(stage);
            var source = Directory.GetDirectories(stage).Single();
            if (!File.Exists(Path.Combine(source, "QproFaceTracking.exe"))) throw new IOException("The app is missing from this installer.");
            var destination = Path.Combine(apps, Path.GetFileName(source));
            if (Directory.Exists(destination)) destination += "-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(source, destination);
            return destination;
        }
        finally
        {
            if (stage.StartsWith(apps + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(stage))
            {
                try { Directory.Delete(stage, true); } catch (IOException) { }
            }
        }
    }
}

internal sealed class Installer : Form
{
    private readonly Button _install = new() { Text = "Install", UseMnemonic = false, AutoSize = true, MinimumSize = new(100, 36), Padding = new(12, 4, 12, 4), UseVisualStyleBackColor = true };
    private readonly Label _status = new() { Text = "Install for your Windows account.", AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 0, 0, 20) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 8, AccessibleName = "Installation progress", MarqueeAnimationSpeed = SystemInformation.UIEffectsEnabled ? 30 : 0, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new(0, 0, 0, 20) };
    private bool _busy;
    private bool _complete;

    public Installer(bool installImmediately = false)
    {
        Text = "Install QFT+";
        ClientSize = new(470, 238);
        MinimumSize = new(450, 275);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Segoe UI", 11);
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new(28) };
        page.ColumnStyles.Add(new(SizeType.Percent, 100));
        page.Controls.Add(new Label { Text = "QFT+", Font = new("Segoe UI", 24, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 0, 0, 12) });
        page.Controls.Add(_status);
        page.Controls.Add(_progress);
        _install.Anchor = AnchorStyles.Right;
        page.Controls.Add(_install);
        Controls.Add(page);
        AcceptButton = _install;
        _install.Click += async (_, _) => { if (_complete) Close(); else await InstallAsync(); };
        if (installImmediately) Shown += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private async Task InstallAsync()
    {
        _busy = true;
        _install.Enabled = false;
        _status.Text = "Installing…";
        _progress.Visible = true;
        var apps = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "apps"));
        InstallationUpgrade? upgrade = null;
        var shortcuts = new Dictionary<string, byte[]?>();
        using var appLock = new Mutex(true, @"Local\QFTPlus", out var ownsLock);
        var ready = false;
        try
        {
            if (!ownsLock) throw new IOException("Quit QFT+ from its tray menu, then try again. Finish or cancel calibration first.");
            var installed = await Task.Run(() => Program.InstallPayload(apps));
            var executable = Path.Combine(installed, "QproFaceTracking.exe");
            if (!File.Exists(executable)) throw new IOException("The app is missing from this installer.");
            _status.Text = "Moving your settings, calibrations, and recordings…";
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "QproAutoStart.json");
            upgrade = new InstallationUpgrade(apps, installed, settings);
            await Task.Run(upgrade.Prepare);
            _status.Text = "Preparing tracking components…";
            await PrepareRuntime(installed);
            await Task.Run(upgrade.EnsureStopped);
            upgrade.Commit();
            foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            {
                var shortcutPath = Path.Combine(Environment.GetFolderPath(folder), "QFT+.lnk");
                shortcuts[shortcutPath] = File.Exists(shortcutPath) ? File.ReadAllBytes(shortcutPath) : null;
                var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                try { shortcut.TargetPath = executable; shortcut.Arguments = "--start"; shortcut.WorkingDirectory = installed; shortcut.Description = "Connect your Quest Pro and start face tracking"; shortcut.Save(); }
                finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
            }
            appLock.ReleaseMutex(); ownsLock = false; appLock.Dispose();
            using var app = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = installed, UseShellExecute = true })
                ?? throw new IOException("Windows couldn’t open QFT+. Your previous installation was kept.");
            if (!await Task.Run(() => app.WaitForInputIdle(15000)) || app.HasExited)
                throw new IOException("The new app didn’t become ready. Your previous installation was kept.");
            ready = true;
            _status.Text = "Removing old installations…";
            var kept = await Task.Run(upgrade.CleanOldInstallations);
            if (kept.Length == 0) { _busy = false; Close(); }
            else
            {
                File.WriteAllLines(Path.Combine(installed, "upgrade-cleanup.log"), kept);
                _status.Text = "QFT+ is ready. Some old files are in use or changed, so those versions were kept. Restart Windows and run setup again to finish cleanup.";
                _complete = true; _install.Text = "Close";
            }
        }
        catch (Exception error)
        {
            if (!ready)
            {
                try
                {
                    upgrade?.Rollback();
                    foreach (var (path, contents) in shortcuts)
                        if (contents is null) File.Delete(path); else File.WriteAllBytes(path, contents);
                }
                catch (Exception rollback) { error = new IOException(error.Message + " The previous settings backup is in the new installation’s upgrade-backup folder.", rollback); }
            }
            _status.Text = "Installation didn’t finish.\n" + error.Message;
            _install.Text = "Try again";
        }
        finally
        {
            _busy = false;
            if (ownsLock) appLock.ReleaseMutex();
            _progress.Visible = false;
            _install.Enabled = true;
        }
    }

    private static async Task PrepareRuntime(string root)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "setup-runtime.ps1") }) info.ArgumentList.Add(arg);
        info.Environment.Remove("PSModulePath");
        using var process = Process.Start(info) ?? throw new IOException("Couldn’t prepare tracking components.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        File.WriteAllText(Path.Combine(root, "upgrade-runtime.log"), await output + Environment.NewLine + await errors);
        if (process.ExitCode != 0) throw new IOException("Tracking components couldn’t be prepared. Your old installation is unchanged. See upgrade-runtime.log in the new installation.");
    }
}
