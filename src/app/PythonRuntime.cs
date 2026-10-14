using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QFTPlus;

internal static class PythonRuntime
{
    const string ArchiveHash = "d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf";
    const string Check = "import sys,cv2,numpy,onnxruntime,frida; assert sys.flags.no_site; assert sys.version_info[:3] == (3,13,15); assert hasattr(cv2,'namedWindow'); assert frida.__version__ == '17.18.0'; assert 'DmlExecutionProvider' in onnxruntime.get_available_providers()";

    internal static string Exe(string root) => Path.Combine(root, "runtime", "python.exe");
    internal static string Marker(string root) => Path.Combine(root, "runtime", "runtime-ready.json");

    internal static bool Ready(string root)
    {
        try { return File.Exists(Exe(root)) && CalibrationSettings.ReadJson(Marker(root))["lockHash"]?.GetValue<string>() == LockHash(root); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { return false; }
    }

    static string LockHash(string root) => WorkingCopy.Hash(Path.Combine(root, "requirements-runtime.lock.txt")).ToUpperInvariant();

    internal static async Task EnsureAsync(string root, CancellationToken token)
    {
        var runtime = Path.Combine(root, "runtime");
        var requirements = Path.Combine(root, "requirements-runtime.lock.txt");
        var archive = Path.Combine(root, "python-runtime", "python-3.13.15-embed-amd64.zip");
        var wheels = Path.Combine(root, "python-runtime", "wheels");
        var pip = Path.Combine(wheels, "pip-26.2.1-py3-none-any.whl");
        await Task.Run(() =>
        {
            if (!File.Exists(archive) || !Directory.Exists(wheels)) throw new IOException(Loc.Get("BundledComponentsAreMissingReinstallQFT"));
            if (WorkingCopy.Hash(archive) != ArchiveHash) throw new IOException(Loc.Get("ThePythonArchiveFailedVerificationReinstallQFT"));
            try { if (Directory.Exists(runtime)) Directory.Delete(runtime, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new IOException(Loc.Get("ThePreviousPCComponentsAreStillInUseWaitA"), error); }
            Directory.CreateDirectory(runtime);
            ZipFile.ExtractToDirectory(archive, runtime, true);
            WritePythonPath(runtime);
            var pipHash = Regex.Match(File.ReadAllText(requirements), @"(?m)^pip==\S+ --hash=sha256:([0-9a-f]{64})").Groups[1].Value;
            if (!File.Exists(pip) || WorkingCopy.Hash(pip) != pipHash) throw new IOException(Loc.Get("ThePackageInstallerFailedVerificationReinstallQFT"));
        }, token);
        if (await RunAsync(Exe(root), ["-c", "import sys,runpy; sys.path.insert(0,sys.argv.pop(1)); runpy.run_module('pip',run_name='__main__')", pip,
                "install", "--require-hashes", "--no-deps", "--no-index", "--no-compile", "--no-cache-dir", "--disable-pip-version-check", "--upgrade",
                "--find-links", wheels, "--target", Path.Combine(runtime, "Lib", "site-packages"), "-r", requirements], token) != 0)
            throw new IOException(Loc.Get("PackageInstallationFailedTheSetupLogHasTheDetails"));
        if (await RunAsync(Exe(root), ["-c", Check], token) != 0) throw new IOException(Loc.Get("ThePCComponentsDidnTInstallCorrectlyTheSetupLog"));
        File.WriteAllText(Marker(root), new JsonObject { ["format"] = "qft-portable-runtime-v2", ["lockHash"] = LockHash(root) }.ToJsonString());
    }

    static void WritePythonPath(string runtime) =>
        File.WriteAllLines(Path.Combine(runtime, "python313._pth"), ["python313.zip", ".", @"Lib\site-packages", "..", @"..\hybrid"]);

    internal static async Task<int> RunAsync(string exe, string[] args, CancellationToken token)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(exe))!;
        var info = Processes.Info(exe, args);
        info.Environment["TEMP"] = info.Environment["TMP"] = Directory.CreateDirectory(Path.Combine(Session.Temp(root), "setup")).FullName;
        var (code, text) = await Processes.RunAsync(info, token, 1800);
        if (text.Trim().Length > 0) Session.Log(Path.Combine(root, "setup.log"), $"{DateTimeOffset.Now:O} {text.Trim()}");
        return code;
    }
}
