namespace GamePatchDepot.Tests;

public sealed class ActualNxSmokeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ui40_SplitsIntoThreeChunksAndReconstructsExactly()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PATCH_RUN_UI40_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const string source = @"F:\MSPrivate\NoLifeWzToNx\wz\UI40.nx";
        Assert.True(File.Exists(source), $"找不到實際測試檔：{source}");
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"], 256);
        string candidate = project.Context.CandidatePath("2026.09.23.1");
        Directory.CreateDirectory(candidate);
        File.Copy(source, Path.Combine(candidate, "UI.nx"));
        ReleaseInfo info = new()
        {
            LauncherTicketProtocol = 2,
            MinimumLauncherVersion = "1.1.1",
            ReleaseId = "2026.09.23.1",
            ContentVersion = 1008,
            GameVersion = "TEST",
            CreatedAt = DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            Changes = [new ReleaseChange { Path = "UI.nx", SourceRevision = "UI40" }]
        };
        await File.WriteAllBytesAsync(
            Path.Combine(candidate, "release-info.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(info, PatchJsonContext.Default.ReleaseInfo));

        ReleaseBuilder builder = new(project.Context, project.Signer);
        (ReleaseManifest manifest, _) = await builder.BuildAsync("2026.09.23.1");
        ManifestFile file = Assert.Single(manifest.Files);
        Assert.Equal(607_577_868, file.Size);
        Assert.Equal("fb20f1f4ead436401dc78a2254399573629a74d150cb64bc3c0076568eb000d4", file.Sha256);
        Assert.Equal(3, file.Chunks.Count);
        Assert.Equal(268_435_456, file.Chunks[0].Size);
        Assert.Equal(268_435_456, file.Chunks[1].Size);
        Assert.Equal(70_706_956, file.Chunks[2].Size);

        ReleaseCatalog catalog = new(project.Context, project.Signer);
        LocalReleaseVerifier verifier = new(project.Context, catalog);
        await verifier.VerifyAsync("2026.09.23.1");
    }
}
