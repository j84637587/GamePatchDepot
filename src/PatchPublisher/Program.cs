using System.Globalization;
using System.Text.Json;

namespace GamePatchDepot;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
            _ = MonitorCancellationFileAsync(options.Get("cancel-file"), cancellation);
            string configPath = options.Get("config") ?? Path.Combine("config", "product.json");
            ProjectContext context = await ProjectContext.LoadAsync(configPath, cancellation.Token);
            context.EnsureDirectories();

            switch (command)
            {
                case "announcement-status":
                case "announcement-publish":
                case "announcement-withdraw":
                    await RunAnnouncementAsync(command, context, options, cancellation.Token);
                    break;
                case "launcher-verify":
                case "launcher-upload":
                case "launcher-publish":
                case "launcher-status":
                    await RunLauncherAsync(command, context, options, cancellation.Token);
                    break;
                case "preview":
                    await RunPreviewAsync(context, options, cancellation.Token);
                    break;
                case "keygen":
                    RunKeygen(context, options);
                    break;
                case "prepare":
                    await RunPrepareAsync(context, options, cancellation.Token);
                    break;
                case "build":
                    await RunBuildAsync(context, options, cancellation.Token);
                    break;
                case "rollback":
                    await RunRollbackAsync(context, options, cancellation.Token);
                    break;
                case "discard-candidate":
                    await RunDiscardCandidateAsync(context, options, cancellation.Token);
                    break;
                case "cancel":
                    await RunCancelAsync(context, options, cancellation.Token);
                    break;
                case "diff":
                    await RunDiffAsync(context, options, cancellation.Token);
                    break;
                case "verify":
                    await RunVerifyAsync(context, options, cancellation.Token);
                    break;
                case "upload":
                    await RunUploadAsync(context, options, cancellation.Token);
                    break;
                case "promote":
                    await RunPromoteAsync(context, options, cancellation.Token);
                    break;
                case "status":
                    await RunStatusAsync(context, cancellation.Token);
                    break;
                case "gc":
                    await RunGcAsync(context, options, cancellation.Token);
                    break;
                default:
                    throw new PatchPublisherException($"未知命令：{command}");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 2;
        }
        catch (PatchPublisherException exception)
        {
            Console.Error.WriteLine($"錯誤：{exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"未預期錯誤：{exception}");
            return 1;
        }
    }

    private static void RunKeygen(ProjectContext context, CliOptions options)
    {
        string privatePath = Path.GetFullPath(options.Require("private"));
        string publicPath = Path.GetFullPath(options.Get("public") ?? context.PublicKeyPath);
        SigningService.GenerateKeyPair(privatePath, publicPath);
        Console.WriteLine($"已建立私鑰：{privatePath}");
        Console.WriteLine($"已建立公鑰：{publicPath}");
        Console.WriteLine("請離線備份私鑰，且絕對不要提交到 Git。");
    }

    private static Dictionary<string, string> ReadReplacements(ProjectContext context, CliOptions options, bool fullClient)
    {
        Dictionary<string, string> replacements = new(StringComparer.OrdinalIgnoreCase);
        foreach (string replacement in options.GetMany("replace"))
        {
            int separator = replacement.IndexOf('=');
            if (separator <= 0 || separator == replacement.Length - 1)
            {
                throw new PatchPublisherException("--replace 格式必須是 安裝檔名=來源路徑。");
            }
            string installName = PathPolicy.NormalizeInstallPath(replacement[..separator]);
            if (!fullClient && !context.AllowedFiles.Contains(installName))
            {
                throw new PatchPublisherException($"--replace 指定了不允許的檔案：{installName}");
            }
            replacements[installName] = replacement[(separator + 1)..];
        }
        return replacements;
    }

    private static async Task RunPreviewAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        PrepareSelection selection = await new PrepareSelectionService(context, new ReleaseCatalog(context, CreateSigner(context, false)))
            .ScanAsync(options.Require("source"), options.Get("base"), ReadReplacements(context, options, true), cancellationToken);
        string output = Path.GetFullPath(options.Require("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, selection, PrepareSelection.JsonOptions, cancellationToken);
        Console.WriteLine($"已掃描 {selection.Files.Count} 個檔案，等待確認；尚未複製或建立 Candidate。");
    }

    private static async Task RunPrepareAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        long contentVersion = options.RequireLong("content-version");
        string source = options.Require("source");
        Dictionary<string, string> replacements = ReadReplacements(context, options, options.Has("full-client"));
        PrepareSelection? selection = options.Get("selection") is string selectionPath
            ? JsonSerializer.Deserialize<PrepareSelection>(await File.ReadAllBytesAsync(selectionPath, cancellationToken), PrepareSelection.JsonOptions)
                ?? throw new PatchPublisherException("無法讀取檔案確認清單。")
            : null;

        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        CandidatePreparer preparer = new(context, catalog);
        string path = await preparer.PrepareAsync(
            releaseId,
            contentVersion,
            source,
            replacements,
            options.Get("description"),
            cancellationToken,
            options.Get("base"),
            options.Has("full-client"),
            selection,
            options.Get("launcher-ticket-protocol") switch
            {
                null => null,
                "2" => 2,
                _ => throw new PatchPublisherException("舊票證協定已移除，--launcher-ticket-protocol 僅支援 2。")
            });
        Console.WriteLine($"Candidate 已建立：{path}");
        ReleaseInfo info = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(path, "release-info.json"), cancellationToken),
            PatchJsonContext.Default.ReleaseInfo)!;
        Console.WriteLine($"模式：{(info.SchemaVersion == 2 ? "完整客戶端" : "舊版 NX")}");
        Console.WriteLine($"Candidate 僅保存 {info.Changes.Count} 個變更檔案。");
        if (info.FullClientFiles is not null)
            Console.WriteLine($"完整版本收錄 {info.FullClientFiles.Count} 個檔案，移除 {info.Delete.Count} 個，排除 {info.ExcludedFiles.Count} 個。");
        foreach (ReleaseChange change in info.Changes) Console.WriteLine($"  變更：{change.Path}");
        foreach (string deleted in info.Delete) Console.WriteLine($"  移除：{deleted}");
        foreach (string excluded in info.ExcludedFiles) Console.WriteLine($"  排除：{excluded}");
        Console.WriteLine("請檢查內容，確認後執行 build。");
    }

    private static async Task RunBuildAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        SigningService signer = CreateSigner(context, true);
        ReleaseBuilder builder = new(context, signer);
        (ReleaseManifest manifest, BuildReport report) = await builder.BuildAsync(releaseId, cancellationToken);
        Console.WriteLine($"Build 完成：{report.ReleaseId}");
        Console.WriteLine($"額外差異補丁：{manifest.Files.Count(file => file.Patch is not null)} 個，" +
            $"總大小 {FormatBytes(manifest.Files.Sum(file => file.Patch?.Size ?? 0))}；完整分塊仍保留作為備援。");
        Console.WriteLine($"檔案：{report.FileCount}，總大小：{FormatBytes(report.TotalFileBytes)}");
        Console.WriteLine($"唯一 chunks：{report.UniqueChunkCount}，大小：{FormatBytes(report.UniqueChunkBytes)}");
        Console.WriteLine($"本次新增本機 chunks：{report.NewLocalChunkCount}，大小：{FormatBytes(report.NewLocalChunkBytes)}");
        Console.WriteLine($"Manifest SHA-256：{report.ManifestSha256}");
    }

    private static async Task RunRollbackAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        string target = options.Require("to");
        long contentVersion = options.RequireLong("content-version");
        SigningService signer = CreateSigner(context, true);
        ReleaseCatalog catalog = new(context, signer);
        RollbackBuilder rollback = new(context, signer, catalog);
        ReleaseManifest manifest = await rollback.BuildAsync(releaseId, contentVersion, target, cancellationToken);
        Console.WriteLine($"已建立回滾版本 {manifest.ReleaseId}，內容來源 {target}。");
        Console.WriteLine("下一步請依序執行本機 verify、upload、遠端深度 verify、promote test、promote stable。");
    }

    private static async Task RunDiscardCandidateAsync(
        ProjectContext context,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        ReleaseCancellationService service = new(context, catalog);
        await service.DiscardCandidateAsync(releaseId, cancellationToken);
        Console.WriteLine($"已捨棄 Candidate：{releaseId}");
    }

    private static async Task RunCancelAsync(
        ProjectContext context,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        ReleaseCancellationService service = new(context, catalog);
        CancelledRelease cancelled = await service.CancelAsync(
            releaseId,
            options.Get("reason"),
            cancellationToken);
        Console.WriteLine($"版本已取消：{cancelled.ReleaseId} / content {cancelled.ContentVersion}");
        Console.WriteLine("Release ID 與 Content Version 已保留，不會重新使用。");
    }

    private static async Task RunDiffAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        string against = options.Get("against") ?? "stable";
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        ManifestDiffService diffService = new(context, catalog);
        DiffReport report = await diffService.CreateAsync(releaseId, against, cancellationToken);
        Console.WriteLine($"比較版本：{report.ReleaseId} ← {report.AgainstReleaseId ?? "無"}");
        PrintDifferences("新增", report.Added);
        PrintDifferences("變更", report.Changed);
        PrintDifferences("移除", report.Removed);
        Console.WriteLine($"未變更：{report.Unchanged.Count}");
        Console.WriteLine($"預估需下載完整變更檔：{FormatBytes(report.EstimatedDownloadBytes)}");
        Console.WriteLine($"相較前版新增唯一 chunk：{FormatBytes(report.NewUniqueChunkBytes)}");
    }

    private static async Task RunVerifyAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        if (options.Has("local"))
        {
            LocalReleaseVerifier verifier = new(context, catalog);
            if (options.Has("changed"))
            {
                (int verified, int reused) = await verifier.VerifyChangesAsync(releaseId, cancellationToken);
                Console.WriteLine($"本機變更驗證通過：{releaseId}（驗證 {verified} 個檔案，沿用基準 {reused} 個檔案）");
            }
            else
            {
                await verifier.VerifyAsync(releaseId, cancellationToken);
                Console.WriteLine($"本機完整驗證通過：{releaseId}");
            }
            return;
        }

        await using R2ObjectStore store = CreateStore(context);
        RemoteReleaseService remote = new(context, signer, catalog, store);
        bool deep = options.Has("deep");
        await remote.VerifyRemoteAsync(releaseId, deep, cancellationToken);
        Console.WriteLine("R2 Manifest 與本機 Build 清單完全一致。");
        Console.WriteLine($"R2 驗證通過：{releaseId}（深度驗證：{deep}）");
    }

    private static async Task RunUploadAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        await using R2ObjectStore store = CreateStore(context);
        RemoteReleaseService remote = new(context, signer, catalog, store);
        await remote.UploadAsync(releaseId, cancellationToken);
        Console.WriteLine($"R2 上傳完成：{releaseId}");
    }

    private static async Task RunPromoteAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        string releaseId = options.Require("release");
        string channel = options.Require("channel");
        SigningService signer = CreateSigner(context, true);
        ReleaseCatalog catalog = new(context, signer);
        await using R2ObjectStore store = CreateStore(context);
        RemoteReleaseService remote = new(context, signer, catalog, store);
        ChannelPayload payload = await remote.PromoteAsync(releaseId, channel, cancellationToken);
        Console.WriteLine($"已發布至 {payload.Channel}：{payload.ReleaseId}");
        Console.WriteLine($"Generation：{payload.Generation}，Content Version：{payload.ContentVersion}");
    }

    private static async Task RunStatusAsync(ProjectContext context, CancellationToken cancellationToken)
    {
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        await using R2ObjectStore store = CreateStore(context);
        RemoteReleaseService remote = new(context, signer, catalog, store);
        foreach (string channel in new[] { "test", "stable" })
        {
            ChannelPayload? payload = await remote.LoadRemoteChannelAsync(channel, cancellationToken);
            Console.WriteLine(payload is null
                ? $"{channel}: 尚未發布"
                : $"{channel}: {payload.ReleaseId} / content {payload.ContentVersion} / generation {payload.Generation}");
        }
    }

    private static async Task RunGcAsync(ProjectContext context, CliOptions options, CancellationToken cancellationToken)
    {
        SigningService signer = CreateSigner(context, false);
        ReleaseCatalog catalog = new(context, signer);
        await using R2ObjectStore store = CreateStore(context);
        RemoteReleaseService remote = new(context, signer, catalog, store);
        GarbageCollector collector = new(context, signer, store, remote);

        if (options.Has("dry-run"))
        {
            (GcReport report, string path) = await collector.DryRunAsync(cancellationToken);
            Console.WriteLine($"GC Dry Run 完成：{path}");
            Console.WriteLine($"候選物件：{report.Candidates.Count}，可釋放：{FormatBytes(report.Candidates.Sum(item => item.Size))}");
            return;
        }

        string reportPath = options.Require("apply");
        GcReport applied = await collector.ApplyAsync(reportPath, cancellationToken);
        Console.WriteLine($"GC 完成：刪除 {applied.DeletedObjectCount} 個物件，釋放 {FormatBytes(applied.DeletedBytes ?? 0)}");
    }

    private static async Task RunAnnouncementAsync(string command, ProjectContext context, CliOptions options, CancellationToken token)
    {
        SigningService signer = CreateSigner(context, command != "announcement-status");
        await using R2ObjectStore store = CreateStore(context);
        AnnouncementService service = new(store, signer);
        AnnouncementFeed feed;
        if (command == "announcement-status") feed = await service.LoadAsync(token);
        else
        {
            if (!options.Has("confirm")) throw new PatchPublisherException("發布／下架公告需要確認。");
            AnnouncementDraft draft = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(options.Require("draft"), token),
                AnnouncementJsonContext.Default.AnnouncementDraft) ?? throw new PatchPublisherException("公告草稿格式錯誤。");
            long expected = long.Parse(options.Require("expected-revision"), CultureInfo.InvariantCulture);
            feed = await service.PublishAsync(draft, expected, command == "announcement-withdraw", token);
        }
        await FileUtilities.WriteReplaceAtomicAsync(options.Require("output"),
            JsonSerializer.SerializeToUtf8Bytes(feed, AnnouncementJsonContext.Default.AnnouncementFeed), token);
        Console.WriteLine($"公告清單已確認：版次 {feed.Revision}，共 {feed.Items.Count} 則。取消或網路中斷後請重新整理確認結果。");
    }

    private static SigningService CreateSigner(ProjectContext context, bool requirePrivate)
    {
        string? privatePath = Environment.GetEnvironmentVariable("PATCH_SIGNING_PRIVATE_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(privatePath))
        {
            privatePath = Path.GetFullPath(privatePath);
        }
        if (requirePrivate && string.IsNullOrWhiteSpace(privatePath))
        {
            throw new PatchPublisherException("此命令需要 PATCH_SIGNING_PRIVATE_KEY_FILE 環境變數。");
        }
        return new SigningService(context.PublicKeyPath, privatePath);
    }

    private static R2ObjectStore CreateStore(ProjectContext context) => new(R2Settings.FromEnvironment(context.Config));

    private static async Task MonitorCancellationFileAsync(
        string? path,
        CancellationTokenSource cancellation)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        string absolutePath = Path.GetFullPath(path);
        while (!cancellation.IsCancellationRequested)
        {
            if (File.Exists(absolutePath))
            {
                cancellation.Cancel();
                return;
            }
            try
            {
                await Task.Delay(250, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static void PrintDifferences(string title, IReadOnlyCollection<FileDifference> differences)
    {
        Console.WriteLine($"{title}：{differences.Count}");
        foreach (FileDifference difference in differences)
        {
            Console.WriteLine($"  {difference.Path}: {FormatBytes(difference.OldSize ?? 0)} → {FormatBytes(difference.NewSize ?? 0)}");
        }
    }

    private static async Task RunLauncherAsync(string command, ProjectContext context, CliOptions options, CancellationToken token)
    {
        if (command == "launcher-status")
        {
            await using R2ObjectStore statusStore = CreateStore(context);
            string? version = await new LauncherReleaseService(statusStore).StatusAsync(token);
            Console.WriteLine($"launcher-stable:{version ?? "尚未發布"}");
            return;
        }
        LauncherPackage package = await LauncherPackage.LoadAsync(options.Require("bundle"), token);
        string? expected = command == "launcher-publish" ? options.Require("expected-bundle") : options.Get("expected-bundle");
        if (expected is not null && expected != package.Digest)
            throw new PatchPublisherException("套件在確認後已變更，請重新選擇、驗證及上傳，不發布未確認的內容。");
        Console.WriteLine("launcher-package:" + JsonSerializer.Serialize(new
        {
            package.Manifest.Version, package.Manifest.Size, package.Manifest.Sha256,
            package.Manifest.Changelog, BundleSha256 = package.Digest
        }));
        Console.WriteLine($"本機啟動器套件驗證通過：{package.Manifest.Version}（{FormatBytes(package.Manifest.Size)}）");
        if (command == "launcher-verify") return;
        await using R2ObjectStore store = CreateStore(context);
        LauncherReleaseService service = new(store, (percent, detail) => Console.WriteLine($"[{percent}/100] 啟動器：{detail}"));
        if (command == "launcher-upload")
        {
            await service.UploadAsync(package, token);
            Console.WriteLine("啟動器上傳及 R2 回驗通過；尚未切換 Stable。");
        }
        else
        {
            if (!options.Has("confirm")) throw new PatchPublisherException("正式發布需加上 --confirm；請先完成啟動器測試。");
            await service.PublishAsync(package, token);
            Console.WriteLine($"launcher-stable:{package.Manifest.Version}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Game Patch Depot - PatchPublisher

共用選項：
  --config <path>                 設定檔，預設 config/product.json
  --cancel-file <path>            檔案出現時安全取消目前操作

命令：
  launcher-verify --bundle <dir>
  launcher-upload --bundle <dir>
  launcher-publish --bundle <dir> --expected-bundle <verified-envelope-sha256> --confirm
  launcher-status
  keygen --private <path> [--public <path>]
  preview --source <dir> --output <selection.json> [--base <release-id>] [--replace UI.nx=<path>]
  prepare --release <id> --content-version <n> --source <dir>
          [--full-client] [--selection <selection.json>] [--base <release-id>] [--replace UI.nx=<path>] [--description <text>]
          [--launcher-ticket-protocol 2]
  build --release <id>
  rollback --release <new-id> --content-version <n> --to <old-id>
  discard-candidate --release <id>
  cancel --release <id> [--reason <text>]
  diff --release <id> [--against stable|test|none|<release-id>]
  verify --release <id> --local
  verify --release <id> [--deep]
  upload --release <id>
  promote --release <id> --channel test|stable
  status
  gc --dry-run
  gc --apply <report-path>

必要環境變數：
  PATCH_SIGNING_PRIVATE_KEY_FILE   build、rollback、promote 使用
  PATCH_R2_ACCOUNT_ID             或改用 PATCH_R2_ENDPOINT
  PATCH_R2_ACCESS_KEY_ID
  PATCH_R2_SECRET_ACCESS_KEY
""");
    }
}

public sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        CliOptions options = new();
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                throw new PatchPublisherException($"無法解析參數：{argument}");
            }

            string key = argument[2..];
            string value = "true";
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }
            if (!options._values.TryGetValue(key, out List<string>? values))
            {
                values = [];
                options._values[key] = values;
            }
            values.Add(value);
        }
        return options;
    }

    public string? Get(string key) => _values.TryGetValue(key, out List<string>? values) ? values[^1] : null;
    public IReadOnlyList<string> GetMany(string key) => _values.TryGetValue(key, out List<string>? values) ? values : [];
    public bool Has(string key) => _values.ContainsKey(key);
    public string Require(string key) => Get(key) ?? throw new PatchPublisherException($"缺少必要參數：--{key}");

    public long RequireLong(string key)
    {
        string value = Require(key);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : throw new PatchPublisherException($"--{key} 必須是整數。");
    }
}
