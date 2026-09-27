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
        _install.Click += async (_, _) => await InstallAsync();
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
        try
        {
            var installed = await Task.Run(() => Program.InstallPayload(apps));
            var executable = Path.Combine(installed, "QproFaceTracking.exe");
            if (!File.Exists(executable)) throw new IOException("The app is missing from this installer.");
            foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(folder), "QFT+.lnk"));
                try { shortcut.TargetPath = executable; shortcut.Arguments = "--start"; shortcut.WorkingDirectory = installed; shortcut.Description = "Connect your Quest Pro and start face tracking"; shortcut.Save(); }
                finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
            }
            Process.Start(new ProcessStartInfo(executable, "--start") { WorkingDirectory = installed, UseShellExecute = true });
            _busy = false;
            Close();
        }
        catch (Exception error)
        {
            _status.Text = "Installation didn’t finish. Check free disk space and try again.\n" + error.Message;
            _install.Text = "Try again";
        }
        finally
        {
            _busy = false;
            _progress.Visible = false;
            _install.Enabled = true;
        }
    }
}
