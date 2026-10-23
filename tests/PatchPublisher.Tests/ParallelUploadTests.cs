using System.Text.Json.Nodes;

namespace GamePatchDepot.Tests;

public sealed partial class RemoteVerificationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task UploadRespectsConcurrencyWaitsForPayloadsAndSkipsOnRerun(int concurrency)
    {
        await using TestProject project = await TestProject.CreateAsync(UploadNames(), uploadConcurrency: concurrency);
        ReleaseManifest manifest = await BuildUploadFixtureAsync(project);
        FakeS3Client client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource firstWave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        object gate = new();
        client.OnPut = async (key, token) =>
        {
            if (!key.StartsWith("objects/", StringComparison.Ordinal)) return;
            lock (gate)
            {
                maximum = Math.Max(maximum, ++active);
                if (active == concurrency) firstWave.TrySetResult();
            }
            try { await releaseWave.Task.WaitAsync(token); }
            finally { lock (gate) active--; }
        };
        await using R2ObjectStore store = new(client, "test-bucket");
        RemoteReleaseService service = Service(project, store);
        Task upload = service.UploadAsync(ReleaseId, timeout.Token);
        try
        {
            await firstWave.Task.WaitAsync(timeout.Token);
            Assert.Equal(concurrency, maximum);
            Assert.DoesNotContain(client.PutKeys, key => key.StartsWith("manifests/", StringComparison.Ordinal));
        }
        finally
        {
            releaseWave.TrySetResult();
            await upload;
        }
        Assert.Equal(0, active);
        Assert.Equal(concurrency, maximum);
        Assert.Equal(10, client.PutKeys.Count(key => key.StartsWith("objects/", StringComparison.Ordinal)));
        foreach (var payload in ObjectKeys.Payloads(manifest))
            Assert.Equal(payload.Sha256, FileUtilities.Sha256Hex(client.Objects[payload.Key]));
        Assert.True(client.Objects.ContainsKey(ObjectKeys.ForManifest(ReleaseId)));
        Assert.True(client.Objects.ContainsKey(ObjectKeys.ForManifestSignature(ReleaseId)));
        Assert.False(client.Objects.ContainsKey(R2OperationLease.LeaseKey));
        await service.UploadAsync(ReleaseId, timeout.Token);
        Assert.Equal(10, client.PutKeys.Count(key => key.StartsWith("objects/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadFailureOrCancellationDrainsWorkersAndDoesNotPublishManifest(bool userCancel)
    {
        await using TestProject project = await TestProject.CreateAsync(UploadNames());
        await BuildUploadFixtureAsync(project);
        FakeS3Client client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource firstWave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        int active = 0;
        client.OnPut = async (key, token) =>
        {
            if (!key.StartsWith("objects/", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref active);
            int index = Interlocked.Increment(ref started);
            if (index == 4) firstWave.TrySetResult();
            try
            {
                await firstWave.Task.WaitAsync(token);
                if (!userCancel && index == 1) throw new IOException("模擬上傳失敗");
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        await using R2ObjectStore store = new(client, "test-bucket");
        Task upload = Service(project, store).UploadAsync(ReleaseId, timeout.Token);
        if (userCancel)
        {
            await firstWave.Task.WaitAsync(timeout.Token);
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
        else
            await Assert.ThrowsAsync<IOException>(() => upload);
        Assert.Equal(0, active);
        Assert.Equal(4, started);
        Assert.DoesNotContain(client.PutKeys, key => key.StartsWith("manifests/", StringComparison.Ordinal));
        Assert.False(client.Objects.ContainsKey(R2OperationLease.LeaseKey));
    }

    [Theory]
    [InlineData("local-corrupt")]
    [InlineData("remote-size")]
    [InlineData("lease-lost")]
    public async Task ParallelUploadStillEnforcesIntegrityAndLease(string failure)
    {
        await using TestProject project = await TestProject.CreateAsync(UploadNames());
        ReleaseManifest manifest = await BuildUploadFixtureAsync(project);
        FakeS3Client client = new();
        ManifestChunk chunk = manifest.Files.Single(file => file.Path == "UI.nx").Chunks[0];
        if (failure == "local-corrupt")
            await File.WriteAllBytesAsync(ObjectKeys.LocalChunkPath(project.Context.ChunkRoot, chunk.Sha256), [9, 9, 9]);
        else if (failure == "remote-size") client.Objects[chunk.Key] = [9];
        else client.OnPut = (key, token) =>
        {
            if (key.StartsWith("objects/", StringComparison.Ordinal)) client.Objects.TryRemove(R2OperationLease.LeaseKey, out _);
            return Task.CompletedTask;
        };
        await using R2ObjectStore store = new(client, "test-bucket");
        await Assert.ThrowsAsync<PatchPublisherException>(() => Service(project, store).UploadAsync(ReleaseId));
        Assert.DoesNotContain(client.PutKeys, key => key.StartsWith("manifests/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task InvalidUploadConcurrencyIsRejected(int concurrency)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string path = Path.Combine(project.Root, "config", "product.json");
        JsonNode config = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        config["uploadConcurrency"] = concurrency;
        await File.WriteAllTextAsync(path, config.ToJsonString());
        PatchPublisherException error = await Assert.ThrowsAsync<PatchPublisherException>(() => ProjectContext.LoadAsync(path));
        Assert.Contains("uploadConcurrency", error.Message);
    }

    private static string[] UploadNames() => Enumerable.Range(0, 10).Select(index => $"File{index}.nx").ToArray();

    private static async Task<ReleaseManifest> BuildUploadFixtureAsync(TestProject project)
    {
        await project.CreateCandidateAsync(ReleaseId, 1, UploadNames().Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => new byte[] { (byte)item.index, 2, 3 }));
        return (await new ReleaseBuilder(project.Context, project.Signer).BuildAsync(ReleaseId)).Manifest;
    }
}
