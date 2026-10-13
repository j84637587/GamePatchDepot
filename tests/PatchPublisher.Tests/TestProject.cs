using System.Security.Cryptography;
using System.Text.Json;

namespace GamePatchDepot.Tests;

internal sealed class TestProject : IAsyncDisposable
{
    private TestProject(string root, ProjectContext context, string privateKeyPath)
    {
        Root = root;
        Context = context;
        PrivateKeyPath = privateKeyPath;
    }

    public string Root { get; }
    public ProjectContext Context { get; }
    public string PrivateKeyPath { get; }
    public SigningService Signer => new(Context.PublicKeyPath, PrivateKeyPath);

    public static async Task<TestProject> CreateAsync(string[] allowedFiles, int chunkSizeMiB = 1, bool enableDeltaPatches = false, int uploadConcurrency = 4)
    {
        string root = Path.Combine(Path.GetTempPath(), "GamePatchDepot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "work", "candidates"));

        ProductConfig config = new()
        {
            Product = "test-product",
            GameVersion = "TEST",
            CandidateRoot = "work/candidates",
            ChunkRoot = "work/chunks",
            CatalogRoot = "catalog",
            ReportRoot = "reports",
            Bucket = "test-bucket",
            PublicBaseUrl = "https://patch.invalid",
            SigningPublicKeyFile = "config/signing-public.pem",
            ChunkSizeMiB = chunkSizeMiB,
            KeepStableReleases = 3,
            OrphanGraceDays = 14,
            TemporaryRetentionDays = 7,
            OperationLeaseMinutes = 30,
            UploadConcurrency = uploadConcurrency,
            EnableDeltaPatches = enableDeltaPatches
        };
        AllowedFilesConfig allowed = new() { Files = allowedFiles.ToList() };
        await File.WriteAllBytesAsync(
            Path.Combine(root, "config", "product.json"),
            JsonSerializer.SerializeToUtf8Bytes(config, PatchJsonContext.Default.ProductConfig));
        await File.WriteAllBytesAsync(
            Path.Combine(root, "config", "allowed-files.json"),
            JsonSerializer.SerializeToUtf8Bytes(allowed, PatchJsonContext.Default.AllowedFilesConfig));

        string privateKeyPath = Path.Combine(root, "private", "signing-private.pem");
        string publicKeyPath = Path.Combine(root, "config", "signing-public.pem");
        SigningService.GenerateKeyPair(privateKeyPath, publicKeyPath);
        ProjectContext context = await ProjectContext.LoadAsync(Path.Combine(root, "config", "product.json"));
        context.EnsureDirectories();
        return new TestProject(root, context, privateKeyPath);
    }

    public async Task<string> CreateCandidateAsync(
        string releaseId,
        long contentVersion,
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyList<ReleaseChange>? changes = null,
        string? baseReleaseId = null)
    {
        string candidate = Context.CandidatePath(releaseId);
        Directory.CreateDirectory(candidate);
        foreach ((string name, byte[] data) in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(candidate, name), data);
        }
        ReleaseInfo info = new()
        {
            ReleaseId = releaseId,
            ContentVersion = contentVersion,
            GameVersion = "TEST",
            CreatedAt = DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            BaseReleaseId = baseReleaseId,
            Changes = changes?.ToList() ?? []
        };
        await File.WriteAllBytesAsync(
            Path.Combine(candidate, "release-info.json"),
            JsonSerializer.SerializeToUtf8Bytes(info, PatchJsonContext.Default.ReleaseInfo));
        return candidate;
    }

    public ValueTask DisposeAsync()
    {
        string absoluteRoot = Path.GetFullPath(Root);
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GamePatchDepot.Tests"));
        if (absoluteRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(absoluteRoot))
        {
            Directory.Delete(absoluteRoot, true);
        }
        return ValueTask.CompletedTask;
    }

    public static byte[] DeterministicBytes(int length, byte seed)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        random.NextBytes(data);
        return data;
    }
}
