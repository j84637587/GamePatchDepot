using System.Text.Json;

namespace GamePatchDepot;

public sealed class ProjectContext
{
    private ProjectContext(string projectRoot, ProductConfig config, HashSet<string> allowedFiles)
    {
        ProjectRoot = projectRoot;
        Config = config;
        GameExecutable = PathPolicy.NormalizeInstallPath(config.GameExecutable ?? "");
        if (!GameExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new PatchPublisherException("gameExecutable 必須是遊戲主程式的相對 EXE 路徑。");
        AllowedFiles = allowedFiles;
    }

    public string ProjectRoot { get; }
    public ProductConfig Config { get; }
    public string GameExecutable { get; }
    public HashSet<string> AllowedFiles { get; }
    public string CandidateRoot => Resolve(Config.CandidateRoot);
    public string ChunkRoot => Resolve(Config.ChunkRoot);
    public string PatchRoot => Path.Combine(ProjectRoot, "work", "patches");
    public string CatalogRoot => Resolve(Config.CatalogRoot);
    public string ReportRoot => Resolve(Config.ReportRoot);
    public string PublicKeyPath => Resolve(Config.SigningPublicKeyFile);
    public string ReleaseCatalogRoot => Path.Combine(CatalogRoot, "releases");
    public string ChannelCatalogRoot => Path.Combine(CatalogRoot, "channels");
    public string CancelledCatalogRoot => Path.Combine(CatalogRoot, "cancelled");

    public static async Task<ProjectContext> LoadAsync(string configPath, CancellationToken cancellationToken = default)
    {
        string absoluteConfig = Path.GetFullPath(configPath);
        if (!File.Exists(absoluteConfig))
        {
            throw new PatchPublisherException($"找不到設定檔：{absoluteConfig}");
        }

        string? configDirectory = Path.GetDirectoryName(absoluteConfig);
        if (configDirectory is null)
        {
            throw new PatchPublisherException("無法從設定檔位置判斷 config 目錄。");
        }
        string? projectRoot = Directory.GetParent(configDirectory)?.FullName;
        if (projectRoot is null)
        {
            throw new PatchPublisherException("無法從設定檔位置判斷專案根目錄。");
        }

        await using FileStream configStream = File.OpenRead(absoluteConfig);
        ProductConfig config = await JsonSerializer.DeserializeAsync(
            configStream,
            PatchJsonContext.Default.ProductConfig,
            cancellationToken) ?? throw new PatchPublisherException("product.json 格式錯誤。");

        string allowedPath = Path.Combine(configDirectory, "allowed-files.json");
        await using FileStream allowedStream = File.OpenRead(allowedPath);
        AllowedFilesConfig allowed = await JsonSerializer.DeserializeAsync(
            allowedStream,
            PatchJsonContext.Default.AllowedFilesConfig,
            cancellationToken) ?? throw new PatchPublisherException("allowed-files.json 格式錯誤。");

        ValidateConfig(config, allowed);
        return new ProjectContext(
            projectRoot,
            config,
            allowed.Files.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public string CandidatePath(string releaseId) => Path.Combine(CandidateRoot, releaseId);
    public string ManifestPath(string releaseId) => Path.Combine(ReleaseCatalogRoot, $"{releaseId}.json");
    public string ManifestSignaturePath(string releaseId) => Path.Combine(ReleaseCatalogRoot, $"{releaseId}.sig");
    public string CancelledReleasePath(string releaseId) => Path.Combine(CancelledCatalogRoot, $"{releaseId}.json");
    public string BuildReportPath(string releaseId) => Path.Combine(ReportRoot, "builds", $"{releaseId}.json");
    public string DiffReportPath(string releaseId, string against) =>
        Path.Combine(ReportRoot, "builds", $"{releaseId}-against-{SanitizeFileName(against)}.diff.json");
    public string VerificationReportPath(string releaseId) =>
        Path.Combine(ReportRoot, "verifications", $"{releaseId}-remote-deep.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(CandidateRoot);
        Directory.CreateDirectory(ChunkRoot);
        Directory.CreateDirectory(ReleaseCatalogRoot);
        Directory.CreateDirectory(ChannelCatalogRoot);
        Directory.CreateDirectory(CancelledCatalogRoot);
        Directory.CreateDirectory(Path.Combine(ReportRoot, "builds"));
        Directory.CreateDirectory(Path.Combine(ReportRoot, "publishes"));
        Directory.CreateDirectory(Path.Combine(ReportRoot, "verifications"));
        Directory.CreateDirectory(Path.Combine(ReportRoot, "garbage-collection"));
    }

    private string Resolve(string configuredPath) => Path.GetFullPath(
        Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(ProjectRoot, configuredPath));

    private static void ValidateConfig(ProductConfig config, AllowedFilesConfig allowed)
    {
        if (config.SchemaVersion != 1)
        {
            throw new PatchPublisherException($"不支援的設定 Schema：{config.SchemaVersion}");
        }

        if (config.ChunkSizeMiB is < 1 or > 480)
        {
            throw new PatchPublisherException("chunkSizeMiB 必須介於 1 與 480 之間。");
        }

        if (config.KeepStableReleases < 1)
        {
            throw new PatchPublisherException("keepStableReleases 至少必須是 1。");
        }

        if (config.UploadConcurrency is < 1 or > 8)
        {
            throw new PatchPublisherException("uploadConcurrency 必須介於 1 與 8 之間。");
        }

        if (config.OrphanGraceDays < 1 || config.OperationLeaseMinutes < 5)
        {
            throw new PatchPublisherException("寬限期與操作鎖時間設定不合理。");
        }

        if (allowed.Files.Count == 0 || allowed.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allowed.Files.Count)
        {
            throw new PatchPublisherException("允許檔案清單為空或含有重複項目。");
        }

        foreach (string file in allowed.Files)
        {
            PathPolicy.ValidateInstallPath(file);
        }
    }

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

public static class PathPolicy
{
    public static string NormalizeInstallPath(string value)
    {
        string normalized = value.Replace('\\', '/').Trim();
        ValidateInstallPath(normalized);
        return normalized;
    }

    public static void ValidateInstallPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new PatchPublisherException($"安裝路徑必須是相對路徑：{value}");
        }

        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)) ||
            System.Text.RegularExpressions.Regex.IsMatch(segment.Split('.')[0],
                @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
        {
            throw new PatchPublisherException($"安裝路徑含有不允許的區段：{value}");
        }
    }

    public static void ValidateFileSet(IEnumerable<string> paths)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            ValidateInstallPath(path);
            if (!files.Add(path.Replace('\\', '/')))
                throw new PatchPublisherException($"檔案路徑重複：{path}");
        }
        foreach (string path in files)
        {
            for (int separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
            {
                if (files.Contains(path[..separator]))
                    throw new PatchPublisherException($"檔案與目錄路徑衝突：{path}");
            }
        }
    }
}
