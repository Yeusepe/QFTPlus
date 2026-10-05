using System.Text.Json;
using System.Text.Json.Nodes;

namespace QFTPlus;

internal static class CalibrationSettings
{
    internal static readonly (string Kind, string Title)[] FaceGroups = [("puff", "Cheeks"), ("brows", "Brows")];
    internal static string FaceOutputKey(string kind) => "extraFaceOutput-" + kind;

    static readonly string[] LegacyKinds = ["tongue", "puff", "brows", "pucker", "corners", "mouth", "nose", "jaw", "cheeks"];

    internal static void RemoveLegacy(string root, string configPath)
    {
        var config = ReadJson(configPath);
        var keys = config.Select(item => item.Key).Where(key => key is "faceEngine" or "gpuTraining" or "runtimePython" or "showPreviews"
            || key.StartsWith("extraFaceModel") || key.StartsWith("tongueModelPath") || key.StartsWith("tongueDirectionModelPath")
            || key.StartsWith(FaceOutputKey("")) && !FaceGroups.Any(group => key == FaceOutputKey(group.Kind))).ToList();
        if (keys.Count > 0)
        {
            foreach (var key in keys) config.Remove(key);
            WriteJson(configPath, config);
        }
        var models = Path.Combine(root, "models");
        var files = (Directory.Exists(models) ? Directory.GetFiles(models).Where(file => !Path.GetFileName(file).StartsWith("universal-face")) : [])
            .Concat(Directory.Exists(Path.Combine(root, "captures")) ? Directory.GetFiles(Path.Combine(root, "captures"))
                .Where(file => LegacyKinds.Any(kind => Path.GetFileName(file).StartsWith(kind + "-"))) : [])
            .Concat(new[] { "qft-trainer.exe", "qft-trainer-licenses.txt", "legacy-components.json" }.Select(name => Path.Combine(root, name)));
        foreach (var file in files) File.Delete(file);
        foreach (var folder in new[] { "training", "downloads" }.Select(name => Path.Combine(root, name)).Where(Directory.Exists))
            Directory.Delete(folder, true);
        if (File.Exists(Path.Combine(root, "runtime", "legacy-ready.json"))) Directory.Delete(Path.Combine(root, "runtime"), true);
    }

    internal static void SaveCalibration(string root, string kind, string result, string configPath)
    {
        var config = ReadJson(configPath);
        var pupilPath = Path.Combine(root, "calibration/qpro-pupil-dilation.json");
        byte[]? previousPupil = null;
        if (kind == "enroll")
        {
            if (!File.Exists(result)) throw new IOException("The face setup file is missing.");
            string[] earlier = [config["faceEnrollment"]?.GetValue<string>() ?? "",
                .. config["faceEnrollmentHistory"]?.AsArray().Select(node => node?.GetValue<string>() ?? "") ?? []];
            config["faceEnrollmentHistory"] = new JsonArray([.. earlier.Where(path => path.Length > 0 && path != result).Distinct().Take(4)
                .Select(path => (JsonNode)path)]);
            config["faceEnrollment"] = result;
        }
        else if (kind == "pupils")
        {
            if (JsonNode.Parse(File.ReadAllText(result))?["format"]?.GetValue<string>() != "qpro-relative-pupil-v1") throw new IOException("The pupil calibration is invalid.");
            previousPupil = File.Exists(pupilPath) ? File.ReadAllBytes(pupilPath) : null;
            config["pupilDilation"] = true;
        }
        else throw new ArgumentException("Unknown calibration type.");
        try
        {
            if (kind == "pupils")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pupilPath)!);
                File.Copy(result, pupilPath + ".tmp", true);
                File.Move(pupilPath + ".tmp", pupilPath, true);
            }
            WriteJson(configPath, config);
        }
        catch
        {
            if (kind == "pupils")
            {
                if (previousPupil is not null) File.WriteAllBytes(pupilPath, previousPupil);
                else File.Delete(pupilPath);
            }
            throw;
        }
    }

    internal static JsonObject ReadJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("The file doesn't hold settings.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(); }
    }

    internal static void WriteJson(string path, JsonObject data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path+".tmp", data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(path+".tmp", path, true); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(10); }
        }
    }
}
