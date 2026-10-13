using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GamePatchDepot.Manager;

internal static class UploadSettings
{
    public static int Load(string path)
    {
        JsonObject config = ReadConfig(path);
        int value = config.ContainsKey("uploadConcurrency")
            ? config["uploadConcurrency"]?.GetValue<int>() ?? throw new InvalidDataException("同時上傳數不可空白。")
            : 4;
        Validate(value);
        return value;
    }

    public static void Save(string path, int value)
    {
        Validate(value);
        JsonObject config = ReadConfig(path);
        config["uploadConcurrency"] = value;
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static JsonObject ReadConfig(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("product.json 必須是 JSON 物件。");

    private static void Validate(int value)
    {
        if (value is < 1 or > 8) throw new InvalidDataException("同時上傳數必須介於 1 與 8 之間。");
    }
}
