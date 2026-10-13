namespace GamePatchDepot.Tests;

public sealed partial class RemoteVerificationTests
{
    [Fact]
    public async Task DeepProgressCountsRepeatedChunksAndPatchAndCompletesOnlyAfterReportIsWritten()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        // 前兩個分塊相同，但完整檔案驗證仍會讀取兩次，分母不可只算去重容量。
        byte[] data = new byte[2 * 1024 * 1024 + 3];
        FakeS3Client client = await SeedAsync(project, data);
        await AddPatchFixtureAsync(project, client);
        List<RemoteVerificationProgress> events = [];
        await using R2ObjectStore store = new(client, "test-bucket");
        await Service(project, store).VerifyRemoteAsync(ReleaseId, true, onProgress: progress =>
        {
            if (progress.Permille == 1000) Assert.True(File.Exists(project.Context.VerificationReportPath(ReleaseId)));
            events.Add(progress);
        });
        Assert.Equal(0, events[0].Permille);
        Assert.All(events, progress => Assert.Equal(data.LongLength + 1, progress.TotalBytes));
        Assert.Contains(events, progress => progress.BytesRead == 1 && progress.Stage == "補丁核對完成");
        Assert.Contains(events, progress => progress.Permille is > 0 and < 999);
        Assert.All(events.SkipLast(1), progress => Assert.InRange(progress.Permille, 0, 999));
        Assert.Equal(1000, events[^1].Permille);
        Assert.Equal(data.LongLength + 1, events[^1].BytesRead);
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.BytesRead <= pair.Second.BytesRead && pair.First.Permille <= pair.Second.Permille));
        Assert.Contains("[1000/1000] R2 深度驗證：100.0%", events[^1].ToLogLine());
    }

    [Fact]
    public async Task ProgressUpdatesWhileOneObjectIsStillDownloading()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project, new byte[300]);
        client.ResponseStreamFactory = (key, bytes) => key.StartsWith("objects/", StringComparison.Ordinal)
            ? new SlowDownloadStream(bytes) : new MemoryStream(bytes);
        List<RemoteVerificationProgress> events = [];
        await using R2ObjectStore store = new(client, "test-bucket");
        await Service(project, store).VerifyRemoteAsync(ReleaseId, true, onProgress: events.Add);
        Assert.Contains(events, progress => progress.Stage == "下載並核對" && progress.BytesRead == 200 && progress.Permille is > 0 and < 999);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("cancel")]
    [InlineData("manifest-changed")]
    [InlineData("report-write")]
    public async Task UnsuccessfulDeepVerificationNeverReportsOneHundredPercent(string failure)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project);
        List<RemoteVerificationProgress> events = [];
        using CancellationTokenSource cancellation = new();
        string reportPath = project.Context.VerificationReportPath(ReleaseId);
        if (failure == "corrupt") client.Objects[client.Objects.Keys.Single(key => key.StartsWith("objects/", StringComparison.Ordinal))][0] ^= 1;
        client.OnGet = key =>
        {
            if (!key.StartsWith("objects/", StringComparison.Ordinal)) return;
            if (failure == "cancel") cancellation.Cancel();
            else if (failure == "manifest-changed") File.Delete(project.Context.ManifestPath(ReleaseId));
            else if (failure == "report-write") Directory.CreateDirectory(reportPath);
        };
        await using R2ObjectStore store = new(client, "test-bucket");
        await Assert.ThrowsAnyAsync<Exception>(() => Service(project, store).VerifyRemoteAsync(ReleaseId, true, cancellation.Token, events.Add));
        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, progress => progress.Permille == 1000);
        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public async Task EmptyFileCompletesWithoutDividingByZeroAndShallowVerificationHasNoDeepProgress()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        FakeS3Client client = await SeedAsync(project, []);
        List<RemoteVerificationProgress> events = [];
        await using R2ObjectStore store = new(client, "test-bucket");
        RemoteReleaseService service = Service(project, store);
        await service.VerifyRemoteAsync(ReleaseId, false, onProgress: events.Add);
        Assert.Empty(events);
        await service.VerifyRemoteAsync(ReleaseId, true, onProgress: events.Add);
        Assert.All(events, progress => Assert.Equal(0, progress.TotalBytes));
        Assert.Equal(1000, events[^1].Permille);
    }

    private sealed class SlowDownloadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            byte[] content = ToArray();
            await destination.WriteAsync(content.AsMemory(0, 100), cancellationToken);
            await Task.Delay(1100, cancellationToken);
            await destination.WriteAsync(content.AsMemory(100, 100), cancellationToken);
            await destination.WriteAsync(content.AsMemory(200), cancellationToken);
        }
    }
}
