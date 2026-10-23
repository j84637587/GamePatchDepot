namespace GamePatchDepot.Tests;

public sealed class CandidateAndRollbackTests
{
    [Fact]
    public async Task Build_RejectsManuallyRelabeledNxMigration()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync("2026.09.23.1", 1,
            new Dictionary<string, byte[]> { ["UI.nx"] = [1] });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.23.1");
        await project.MarkAsLegacyProtocolAsync("2026.09.23.1");
        await project.CreateCandidateAsync("2026.09.23.2", 2,
            new Dictionary<string, byte[]> { ["UI.nx"] = [2] },
            changes: [new ReleaseChange { Path = "UI.nx" }], baseReleaseId: "2026.09.23.1");
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.23.2"));
        Assert.Contains("完整客戶端", error.Message);
        Assert.False(File.Exists(project.Context.ManifestPath("2026.09.23.2")));
    }

    [Fact]
    public async Task Prepare_CopiesCanonicalSetAndAppliesReplacement()
    {
        await using TestProject project = await TestProject.CreateAsync(["A.nx", "UI.nx"]);
        string source = Path.Combine(project.Root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "A.nx"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [7, 8]);
        string replacement = Path.Combine(project.Root, "UI40.nx");
        await File.WriteAllBytesAsync(replacement, [9, 9, 9]);

        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        string candidate = await preparer.PrepareAsync(
            "2026.09.23.1",
            1008,
            source,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UI.nx"] = replacement },
            "更新 UI",
            CancellationToken.None, fullClient: true, minimumLauncherVersion: "1.1.1");

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(candidate, "files", "A.nx")));
        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(Path.Combine(candidate, "files", "UI.nx")));
        Assert.True(File.Exists(Path.Combine(candidate, "release-info.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_NxOnlyCannotIntroduceReadyProtocol(bool legacyBase)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = Path.Combine(project.Root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [9]);
        if (legacyBase)
        {
            await project.CreateCandidateAsync("2026.09.23.1", 1,
                new Dictionary<string, byte[]> { ["UI.nx"] = [1] });
            await new ReleaseBuilder(project.Context, project.Signer).BuildAsync("2026.09.23.1");
            await project.MarkAsLegacyProtocolAsync("2026.09.23.1");
        }
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() =>
            new CandidatePreparer(project.Context, new ReleaseCatalog(project.Context, project.Signer)).PrepareAsync(
                "2026.09.23.2", 2, source, new Dictionary<string, string>(), "NX only",
                baseReleaseId: legacyBase ? "2026.09.23.1" : null));
        Assert.Contains("--full-client", error.Message);
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.23.2")));
    }

    [Fact]
    public async Task Prepare_WithBase_CopiesOnlyChangedFiles()
    {
        await using TestProject project = await TestProject.CreateAsync(["A.nx", "UI.nx"]);
        await project.CreateCandidateAsync(
            "2026.09.23.1",
            1008,
            new Dictionary<string, byte[]> { ["A.nx"] = [1, 2, 3], ["UI.nx"] = [4, 5, 6] });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.23.1");

        string source = Path.Combine(project.Root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "A.nx"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [9, 9, 9]);

        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        string candidate = await preparer.PrepareAsync(
            "2026.09.23.2",
            1009,
            source,
            new Dictionary<string, string>(),
            "更新 UI",
            CancellationToken.None,
            "2026.09.23.1");

        Assert.False(File.Exists(Path.Combine(candidate, "A.nx")));
        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(Path.Combine(candidate, "UI.nx")));

        (ReleaseManifest manifest, _) = await builder.BuildAsync("2026.09.23.2");
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(
            (await catalog.LoadVerifiedAsync("2026.09.23.1")).Manifest.Files.Single(file => file.Path == "A.nx").Sha256,
            manifest.Files.Single(file => file.Path == "A.nx").Sha256);
    }

    [Fact]
    public async Task Prepare_WithBase_RejectsWhenNothingChanged()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        byte[] content = [1, 2, 3];
        await project.CreateCandidateAsync(
            "2026.09.23.1",
            1008,
            new Dictionary<string, byte[]> { ["UI.nx"] = content });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.23.1");

        string source = Path.Combine(project.Root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), content);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);

        PatchPublisherException exception = await Assert.ThrowsAsync<PatchPublisherException>(() =>
            preparer.PrepareAsync(
                "2026.09.23.2",
                1009,
                source,
                new Dictionary<string, string>(),
                "沒有變更",
                CancellationToken.None,
                "2026.09.23.1"));

        Assert.Contains("沒有可建立的變更", exception.Message);
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.23.2")));
    }

    [Fact]
    public async Task Rollback_CreatesNewMonotonicManifestReferencingOldChunks()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync(
            "2026.09.23.1",
            1008,
            new Dictionary<string, byte[]> { ["UI.nx"] = TestProject.DeterministicBytes(4096, 1) });

        ReleaseBuilder builder = new(project.Context, project.Signer);
        (ReleaseManifest original, _) = await builder.BuildAsync("2026.09.23.1");
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        RollbackBuilder rollbackBuilder = new(project.Context, project.Signer, catalog);
        ReleaseManifest rollback = await rollbackBuilder.BuildAsync(
            "2026.09.23.2",
            1009,
            "2026.09.23.1");

        Assert.Equal(1009, rollback.ContentVersion);
        Assert.Equal(original.Files[0].Sha256, rollback.Files[0].Sha256);
        Assert.Equal(original.Files[0].Chunks[0].Key, rollback.Files[0].Chunks[0].Key);
        await catalog.LoadVerifiedAsync("2026.09.23.2");
    }
}
