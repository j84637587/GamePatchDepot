using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace GamePatchDepot.Tests;

public sealed partial class RemoteVerificationTests
{
    private const string ReleaseId = "2026.09.26.1";

    [Fact]
    public async Task CurrentLegacyChannelCanRepairHistoryWithoutSwitchingOrReenablingIt()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        await project.MarkAsLegacyProtocolAsync(ReleaseId, nxOnly: true);
        byte[] manifest = await File.ReadAllBytesAsync(project.Context.ManifestPath(ReleaseId));
        client.Objects[ObjectKeys.ForManifest(ReleaseId)] = manifest;
        client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)] = await File.ReadAllBytesAsync(project.Context.ManifestSignaturePath(ReleaseId));
        ChannelPayload current = new()
        {
            Channel = "test", Generation = 1, ReleaseId = ReleaseId, ContentVersion = 1,
            ManifestKey = ObjectKeys.ForManifest(ReleaseId), ManifestSha256 = FileUtilities.Sha256Hex(manifest),
            PublishedAt = DateTimeOffset.UtcNow
        };
        byte[] envelope = SignedEnvelopeService.Create(current, PatchJsonContext.Default.ChannelPayload, project.Signer);
        client.Objects[ObjectKeys.ForChannel("test")] = envelope;
        await using R2ObjectStore store = new(client, "test-bucket");
        RemoteReleaseService service = Service(project, store);
        var repaired = await service.PromoteAsync(ReleaseId, "test");
        Assert.Equal(1, repaired.Generation);
        Assert.Equal(envelope, client.Objects[ObjectKeys.ForChannel("test")]);
        Assert.Equal(envelope, client.Objects[ObjectKeys.ForChannelHistory("test", 1)]);
        await service.PromoteAsync(ReleaseId, "test");
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() => service.PromoteAsync(ReleaseId, "stable"));
        Assert.Contains("舊票證協定", error.Message);
        Assert.False(client.Objects.ContainsKey(ObjectKeys.ForChannel("stable")));
    }

    [Fact]
    public async Task LegacyHistoryCanBeVerifiedButCannotBePromotedOrRolledBack()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        await project.MarkAsLegacyProtocolAsync(ReleaseId);
        client.Objects[ObjectKeys.ForManifest(ReleaseId)] = await File.ReadAllBytesAsync(project.Context.ManifestPath(ReleaseId));
        client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)] = await File.ReadAllBytesAsync(project.Context.ManifestSignaturePath(ReleaseId));
        await using R2ObjectStore store = new(client, "test-bucket");
        RemoteReleaseService service = Service(project, store);
        var (legacy, _) = await service.VerifyRemoteAsync(ReleaseId, false);
        Assert.Equal(1, legacy.LauncherTicketProtocol);
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() => service.PromoteAsync(ReleaseId, "test"));
        Assert.Contains("舊票證協定", error.Message);
        Assert.False(client.Objects.ContainsKey(ObjectKeys.ForChannel("test")));
        var rollback = new RollbackBuilder(project.Context, project.Signer, new ReleaseCatalog(project.Context, project.Signer));
        await Assert.ThrowsAsync<PatchPublisherException>(() => rollback.BuildAsync("2026.09.26.2", 2, ReleaseId));
    }

    [Fact]
    public async Task UploadIncludesAdditionalPatchAndRemoteDeepVerificationChecksIt()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        FileDeltaPatch patch = await AddPatchFixtureAsync(project, client);
        client.Objects.Clear();
        await using R2ObjectStore store = new(client, "test-bucket");
        RemoteReleaseService service = Service(project, store);
        await service.UploadAsync(ReleaseId);
        Assert.Equal(new byte[] { 8 }, client.Objects[patch.Key]);
        await service.VerifyRemoteAsync(ReleaseId, true);
        client.Objects[patch.Key][0] ^= 1;
        PatchPublisherException error = await Assert.ThrowsAsync<PatchPublisherException>(() => service.VerifyRemoteAsync(ReleaseId, true));
        Assert.Contains("差異補丁", error.Message);
        Assert.False(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
    }

    [Fact]
    public async Task GcKeepsReferencedPatchAndFindsOrphanPatchWithoutTouchingOtherPrefixes()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        FileDeltaPatch patch = await AddPatchFixtureAsync(project, client);
        ChannelPayload channel = new()
        {
            Channel = "test", Generation = 1, ReleaseId = ReleaseId, ContentVersion = 1,
            ManifestKey = ObjectKeys.ForManifest(ReleaseId),
            ManifestSha256 = FileUtilities.Sha256Hex(client.Objects[ObjectKeys.ForManifest(ReleaseId)]),
            PublishedAt = DateTimeOffset.UtcNow
        };
        client.Objects[ObjectKeys.ForChannel("test")] = SignedEnvelopeService.Create(channel, PatchJsonContext.Default.ChannelPayload, project.Signer);
        string orphan = ObjectKeys.ForPatch(FileUtilities.Sha256Hex([9]));
        client.Objects[orphan] = [9];
        client.Objects["objects/unrelated/example"] = [5];
        await using R2ObjectStore store = new(client, "test-bucket");
        GarbageCollector collector = new(project.Context, project.Signer, store, Service(project, store));
        (GcReport report, string reportPath) = await collector.DryRunAsync();
        Assert.Equal(orphan, Assert.Single(report.Candidates).Key);
        Assert.DoesNotContain(report.Candidates, item => item.Key == patch.Key);
        Assert.Equal(2, report.ReferencedObjectCount);
        GcReport applied = await collector.ApplyAsync(reportPath);
        Assert.Equal(1, applied.DeletedObjectCount);
        Assert.True(client.Objects.ContainsKey(patch.Key));
        Assert.False(client.Objects.ContainsKey(orphan));
        Assert.True(client.Objects.ContainsKey("objects/unrelated/example"));
    }

    private static async Task<FileDeltaPatch> AddPatchFixtureAsync(TestProject project, FakeS3Client client)
    {
        // 這裡只測試傳輸／GC；真實補丁格式與套用由 DeltaPatchTests 驗證。
        string hash = FileUtilities.Sha256Hex([8]);
        FileDeltaPatch patch = new() { BaseSha256 = new string('a', 64), BaseSize = 3, Sha256 = hash, Size = 1, Key = ObjectKeys.ForPatch(hash) };
        RewriteManifest(project, client, manifest => manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == "UI.nx")!["patch"] = JsonSerializer.SerializeToNode(
            patch, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await File.WriteAllBytesAsync(project.Context.ManifestPath(ReleaseId), client.Objects[ObjectKeys.ForManifest(ReleaseId)]);
        await File.WriteAllBytesAsync(project.Context.ManifestSignaturePath(ReleaseId), client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)]);
        string path = ObjectKeys.LocalPatchPath(project.Context, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [8]);
        client.Objects[patch.Key] = [8];
        return patch;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingManifestsPassAndDeepReportUsesVerifiedHash(bool deep)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        await using R2ObjectStore store = new(client, "test-bucket");
        var result = await Service(project, store).VerifyRemoteAsync(ReleaseId, deep);
        string expectedHash = FileUtilities.Sha256Hex(client.Objects[ObjectKeys.ForManifest(ReleaseId)]);
        Assert.Equal(expectedHash, result.ManifestSha256);
        Assert.Equal(deep, File.Exists(project.Context.VerificationReportPath(ReleaseId)));
        if (deep)
        {
            VerificationReport report = JsonSerializer.Deserialize(
                await File.ReadAllBytesAsync(project.Context.VerificationReportPath(ReleaseId)), PatchJsonContext.Default.VerificationReport)!;
            Assert.Equal(2, report.SchemaVersion);
            Assert.Equal(expectedHash, report.ManifestSha256);
            Assert.True(report.Deep);
            Assert.Equal(ReleaseId, report.ReleaseId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentManifestWithValidSignatureIsRejectedBeforeReadingChunks(bool deep)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        RewriteManifest(project, client, manifest => manifest["minimumLauncherVersion"] = "9.9.9");
        await using R2ObjectStore store = new(client, "test-bucket");
        PatchPublisherException error = await Assert.ThrowsAsync<PatchPublisherException>(() => Service(project, store).VerifyRemoteAsync(ReleaseId, deep));
        Assert.Contains("本機 Build", error.Message);
        Assert.Equal(0, client.HeadCount);
        Assert.DoesNotContain(client.ReadKeys, key => key.StartsWith("objects/", StringComparison.Ordinal));
        Assert.False(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("chunk")]
    [InlineData("cancel")]
    public async Task FailedOrCancelledReverificationRemovesPreviousSuccess(string failure)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        await using R2ObjectStore store = new(client, "test-bucket");
        RemoteReleaseService service = Service(project, store);
        await service.VerifyRemoteAsync(ReleaseId, true);
        Assert.True(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
        using CancellationTokenSource cancellation = new();
        if (failure == "manifest")
            RewriteManifest(project, client, manifest => manifest["minimumLauncherVersion"] = "9.9.9");
        else if (failure == "chunk")
            client.Objects[client.Objects.Keys.Single(key => key.StartsWith("objects/", StringComparison.Ordinal))][0] ^= 1;
        else
            client.OnGet = key => { if (key.StartsWith("objects/", StringComparison.Ordinal)) cancellation.Cancel(); };
        if (failure == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyRemoteAsync(ReleaseId, true, cancellation.Token));
        else
            await Assert.ThrowsAsync<PatchPublisherException>(() => service.VerifyRemoteAsync(ReleaseId, true));
        Assert.False(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
    }

    [Theory]
    [InlineData("missing-local")]
    [InlineData("bad-local-signature")]
    [InlineData("bad-remote-signature")]
    public async Task BothLocalAndRemoteSignedManifestsAreRequired(string failure)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        if (failure == "missing-local") File.Delete(project.Context.ManifestPath(ReleaseId));
        else if (failure == "bad-local-signature")
            await File.WriteAllTextAsync(project.Context.ManifestSignaturePath(ReleaseId), "invalid signature");
        else client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)] = Encoding.UTF8.GetBytes("invalid signature");
        await using R2ObjectStore store = new(client, "test-bucket");
        await Assert.ThrowsAsync<PatchPublisherException>(() => Service(project, store).VerifyRemoteAsync(ReleaseId, true));
        Assert.Equal(0, client.HeadCount);
        Assert.False(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
    }

    [Fact]
    public async Task LocalManifestChangedDuringDownloadCannotProduceSuccessReport()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        client.OnGet = key =>
        {
            if (!key.StartsWith("objects/", StringComparison.Ordinal)) return;
            JsonNode manifest = JsonNode.Parse(client.Objects[ObjectKeys.ForManifest(ReleaseId)])!;
            manifest["minimumLauncherVersion"] = "9.9.9";
            byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            File.WriteAllBytes(project.Context.ManifestPath(ReleaseId), bytes);
            File.WriteAllText(project.Context.ManifestSignaturePath(ReleaseId), project.Signer.Sign(bytes));
        };
        await using R2ObjectStore store = new(client, "test-bucket");
        await Assert.ThrowsAsync<PatchPublisherException>(() => Service(project, store).VerifyRemoteAsync(ReleaseId, true));
        Assert.False(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
    }

    [Fact]
    public async Task FullFileHashIsCheckedEvenWhenAllChunksMatch()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        RewriteManifest(project, client, manifest => manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == "UI.nx")!["sha256"] = new string('a', 64));
        await File.WriteAllBytesAsync(project.Context.ManifestPath(ReleaseId), client.Objects[ObjectKeys.ForManifest(ReleaseId)]);
        await File.WriteAllBytesAsync(project.Context.ManifestSignaturePath(ReleaseId), client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)]);
        await using R2ObjectStore store = new(client, "test-bucket");
        PatchPublisherException error = await Assert.ThrowsAsync<PatchPublisherException>(() => Service(project, store).VerifyRemoteAsync(ReleaseId, true));
        Assert.Contains("重組檔案", error.Message);
        Assert.False(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
    }

    private static RemoteReleaseService Service(TestProject project, R2ObjectStore store) =>
        new(project.Context, project.Signer, new ReleaseCatalog(project.Context, project.Signer), store);

    private static async Task<FakeS3Client> SeedAsync(TestProject project, byte[]? sourceBytes = null)
    {
        await project.CreateCandidateAsync(ReleaseId, 1, new Dictionary<string, byte[]> { ["UI.nx"] = sourceBytes ?? [1, 2, 3] });
        (ReleaseManifest manifest, _) = await new ReleaseBuilder(project.Context, project.Signer).BuildAsync(ReleaseId);
        FakeS3Client client = new();
        client.Objects[ObjectKeys.ForManifest(ReleaseId)] = await File.ReadAllBytesAsync(project.Context.ManifestPath(ReleaseId));
        client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)] = await File.ReadAllBytesAsync(project.Context.ManifestSignaturePath(ReleaseId));
        foreach (ManifestChunk chunk in manifest.Files.SelectMany(file => file.Chunks))
            client.Objects[chunk.Key] = await File.ReadAllBytesAsync(ObjectKeys.LocalChunkPath(project.Context.ChunkRoot, chunk.Sha256));
        return client;
    }

    private static void RewriteManifest(TestProject project, FakeS3Client client, Action<JsonNode> rewrite)
    {
        JsonNode manifest = JsonNode.Parse(client.Objects[ObjectKeys.ForManifest(ReleaseId)])!;
        rewrite(manifest);
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        client.Objects[ObjectKeys.ForManifest(ReleaseId)] = bytes;
        client.Objects[ObjectKeys.ForManifestSignature(ReleaseId)] = Encoding.UTF8.GetBytes(project.Signer.Sign(bytes));
    }

    // 全部資料留在記憶體，不連線至真實 R2，也不使用實際憑證。
    private sealed class FakeS3Client() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true, AuthenticationRegion = "auto"
    })
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> ReadKeys { get; } = new();
        public ConcurrentQueue<string> PutKeys { get; } = new();
        private int _headCount;
        public int HeadCount => _headCount;
        public Action<string>? OnGet { get; set; }
        public Func<string, CancellationToken, Task>? OnPut { get; set; }
        public Func<string, byte[], Stream>? ResponseStreamFactory { get; set; }

        public override Task<GetObjectResponse> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadKeys.Enqueue(key);
            OnGet?.Invoke(key);
            byte[] bytes = GetBytes(key);
            return Task.FromResult(new GetObjectResponse { ResponseStream = ResponseStreamFactory?.Invoke(key, bytes) ?? new MemoryStream(bytes, writable: false), ContentLength = bytes.Length });
        }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _headCount);
            byte[] bytes = GetBytes(request.Key);
            return Task.FromResult(new GetObjectMetadataResponse { ContentLength = bytes.Length, ETag = FileUtilities.Sha256Hex(bytes) });
        }

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            PutKeys.Enqueue(request.Key);
            if (OnPut is not null) await OnPut(request.Key, cancellationToken);
            using MemoryStream data = new();
            await request.InputStream.CopyToAsync(data, cancellationToken);
            byte[] bytes = data.ToArray();
            Objects[request.Key] = bytes;
            return new PutObjectResponse { ETag = FileUtilities.Sha256Hex(bytes) };
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
        {
            Objects.TryRemove(key, out _);
            return Task.FromResult(new DeleteObjectResponse());
        }

        public override Task<DeleteObjectsResponse> DeleteObjectsAsync(DeleteObjectsRequest request, CancellationToken cancellationToken = default)
        {
            foreach (KeyVersion item in request.Objects) Objects.TryRemove(item.Key, out _);
            return Task.FromResult(new DeleteObjectsResponse());
        }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListObjectsV2Response
            {
                IsTruncated = false,
                S3Objects = Objects.Where(item => item.Key.StartsWith(request.Prefix, StringComparison.Ordinal))
                    .Select(item => new S3Object { Key = item.Key, Size = item.Value.Length, LastModified = DateTime.UtcNow.AddDays(-100),
                        ETag = FileUtilities.Sha256Hex(item.Value) }).ToList()
            });

        private byte[] GetBytes(string key) => Objects.TryGetValue(key, out byte[]? bytes) ? bytes :
            throw new AmazonS3Exception("Missing test object") { StatusCode = HttpStatusCode.NotFound };
    }
}
