using System.Text.Json;
using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

internal static class CalibrationSettings
{
    internal static readonly (string Kind, string Title)[] FaceGroups =
        [("puff", "Cheeks"), ("brows", "Brows"), ("pucker", "Lip pucker"), ("corners", "Mouth corners"),
         ("mouth", "Lip shift"), ("nose", "Nostrils"), ("jaw", "Jaw")];
    internal static bool IsFaceGroup(string kind) => Array.Exists(FaceGroups, group => group.Kind == kind);
    internal static string FaceModelKey(string kind) => kind == "puff" ? "extraFaceModel" : "extraFaceModel-" + kind;
    internal static string FaceOutputKey(string kind) => "extraFaceOutput-" + kind;

    internal static void SaveCalibration(string root, string kind, string result, string configPath)
    {
        var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath))!.AsObject() : new JsonObject();
        var pupilPath = Path.Combine(root, "calibration/qpro-pupil-dilation.json");
        byte[]? previousPupil = null;
        if (kind == "tongue")
        {
            if (!File.Exists(result + "-direction.pt")) throw new IOException("The new tongue model is missing.");
            config.Remove("tongueModelPath");
            config["tongueDirectionModelPath"] = result + "-direction.pt";
        }
        else if (IsFaceGroup(kind))
        {
            if (!File.Exists(result)) throw new IOException("The expression model is missing.");
            config[FaceModelKey(kind)] = result; config["extraFaceOutput"] = true; config[FaceOutputKey(kind)] = true;
        }
        else if (kind == "enroll")
        {
            if (!File.Exists(result)) throw new IOException("The face setup file is missing.");
            config["faceEnrollment"] = result;
        }
        else if (kind == "pupils")
        {
            if (JsonNode.Parse(File.ReadAllText(result))?["format"]?.GetValue<string>() != "qpro-relative-pupil-v1") throw new IOException("The pupil calibration is invalid.");
            previousPupil = File.Exists(pupilPath) ? File.ReadAllBytes(pupilPath) : null;
            config["pupilDilation"] = true;
        }
        else throw new ArgumentException("Unknown calibration type.");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".before-calibration", true);
        try
        {
            if (kind == "pupils")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pupilPath)!);
                if (previousPupil is not null) File.WriteAllBytes(pupilPath + ".previous", previousPupil);
                File.Copy(result, pupilPath + ".tmp", true);
                File.Move(pupilPath + ".tmp", pupilPath, true);
            }
            File.WriteAllText(configPath + ".tmp", config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(configPath + ".tmp", configPath, true);
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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)!.AsObject();
    }
}
