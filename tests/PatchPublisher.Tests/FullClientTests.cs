namespace GamePatchDepot.Tests;

public sealed class FullClientTests
{
    [Fact]
    public async Task FullClientBuildsNestedFilesAndEmptyFilesThenUpdatesDeletesAndRollsBack()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(source, "data", "client.dll"), [3, 4]);
        await File.WriteAllBytesAsync(Path.Combine(source, "data", "UI.nx"), [5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(source, "data", "empty.dat"), []);
        await File.WriteAllTextAsync(Path.Combine(source, "release-info.json"), "client-owned file");
        await File.WriteAllTextAsync(Path.Combine(source, "logs", "session.log"), "not for publishing");
        await File.WriteAllTextAsync(Path.Combine(source, "secret.pem"), "not for publishing");
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, new Dictionary<string, string>(), "first", fullClient: true, minimumLauncherVersion: "1.1.1");
        (ReleaseManifest first, _) = await builder.BuildAsync("2026.09.26.1");
        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(5, first.Files.Count);
        Assert.Empty(first.Files.Single(file => file.Path == "data/empty.dat").Chunks);
        Assert.Contains("secret.pem", first.Preserve!);
        await new LocalReleaseVerifier(project.Context, catalog).VerifyAsync(first.ReleaseId);
        Assert.Equal((5, 0), await new LocalReleaseVerifier(project.Context, catalog).VerifyChangesAsync(first.ReleaseId));
        foreach (ManifestFile file in first.Files)
        {
            using MemoryStream restored = new();
            foreach (ManifestChunk chunk in file.Chunks)
                await restored.WriteAsync(await File.ReadAllBytesAsync(ObjectKeys.LocalChunkPath(project.Context.ChunkRoot, chunk.Sha256)));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, file.Path)), restored.ToArray());
        }

        await File.WriteAllBytesAsync(Path.Combine(source, "data", "client.dll"), [7, 8, 9]);
        File.Delete(Path.Combine(source, "data", "empty.dat"));
        string candidate = await preparer.PrepareAsync("2026.09.26.2", 2, source, new Dictionary<string, string>(), "delta",
            baseReleaseId: first.ReleaseId, fullClient: true, minimumLauncherVersion: "1.1.1");
        Assert.Single(Directory.EnumerateFiles(Path.Combine(candidate, "files"), "*", SearchOption.AllDirectories));
        (ReleaseManifest second, _) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(4, second.Files.Count);
        Assert.Equal(["data/empty.dat"], second.Delete);
        Assert.Equal(first.Files.Single(file => file.Path == "Game.exe").Sha256,
            second.Files.Single(file => file.Path == "Game.exe").Sha256);
        DiffReport diff = await new ManifestDiffService(project.Context, catalog).CreateAsync(second.ReleaseId, first.ReleaseId);
        Assert.Single(diff.Changed);
        Assert.Single(diff.Removed);
        await new LocalReleaseVerifier(project.Context, catalog).VerifyAsync(second.ReleaseId);
        LocalReleaseVerifier verifier = new(project.Context, catalog);
        Assert.Equal((1, 3), await verifier.VerifyChangesAsync(second.ReleaseId));
        string unchangedChunk = ObjectKeys.LocalChunkPath(project.Context.ChunkRoot,
            first.Files.Single(file => file.Path == "Game.exe").Chunks.Single().Sha256);
        byte[] unchangedBytes = await File.ReadAllBytesAsync(unchangedChunk);
        await File.WriteAllBytesAsync(unchangedChunk, [0, 0]);
        Assert.Equal((1, 3), await verifier.VerifyChangesAsync(second.ReleaseId));
        await Assert.ThrowsAsync<PatchPublisherException>(() => verifier.VerifyAsync(second.ReleaseId));
        await File.WriteAllBytesAsync(unchangedChunk, unchangedBytes);
        string changedChunk = ObjectKeys.LocalChunkPath(project.Context.ChunkRoot,
            second.Files.Single(file => file.Path == "data/client.dll").Chunks.Single().Sha256);
        byte[] changedBytes = await File.ReadAllBytesAsync(changedChunk);
        await File.WriteAllBytesAsync(changedChunk, [0, 0, 0]);
        await Assert.ThrowsAsync<PatchPublisherException>(() => verifier.VerifyChangesAsync(second.ReleaseId));
        await File.WriteAllBytesAsync(changedChunk, changedBytes);

        // 只有刪除的更新仍可建立，不需要任何新 payload。
        File.Delete(Path.Combine(source, "data", "UI.nx"));
        await preparer.PrepareAsync("2026.09.26.3", 3, source, new Dictionary<string, string>(), "remove",
            baseReleaseId: second.ReleaseId, fullClient: true, minimumLauncherVersion: "1.1.1");
        (ReleaseManifest third, BuildReport report) = await builder.BuildAsync("2026.09.26.3");
        Assert.Equal(0, report.NewLocalChunkCount);
        Assert.DoesNotContain(third.Files, file => file.Path == "data/UI.nx");

        ReleaseManifest rollback = await new RollbackBuilder(project.Context, project.Signer, catalog)
            .BuildAsync("2026.09.26.4", 4, first.ReleaseId);
        Assert.Equal(2, rollback.SchemaVersion);
        Assert.Equal(first.Files.Select(file => file.Path), rollback.Files.Select(file => file.Path));
        await new LocalReleaseVerifier(project.Context, catalog).VerifyAsync(rollback.ReleaseId);
    }

    [Fact]
    public async Task PolicyPreservesPreviouslyManagedSettingsAndRequiresNamedFiles()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [1]);
        await File.WriteAllTextAsync(Path.Combine(source, "settings.ini"), "player settings");
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, new Dictionary<string, string>(), "first", fullClient: true, minimumLauncherVersion: "1.1.1");
        await builder.BuildAsync("2026.09.26.1");
        await File.WriteAllTextAsync(Path.Combine(project.Root, "config", "client-files.json"),
            """{"preservedPaths":["settings.ini"],"requiredFiles":["Game.exe"]}""");
        await preparer.PrepareAsync("2026.09.26.2", 2, source, new Dictionary<string, string>(), "preserve",
            baseReleaseId: "2026.09.26.1", fullClient: true, minimumLauncherVersion: "1.1.1");
        (ReleaseManifest next, _) = await builder.BuildAsync("2026.09.26.2");
        Assert.Contains("settings.ini", next.Preserve!);
        Assert.Empty(next.Delete);
        Assert.DoesNotContain(next.Files, file => file.Path == "settings.ini");

        await File.WriteAllTextAsync(Path.Combine(project.Root, "config", "client-files.json"),
            """{"requiredFiles":["missing.dll"]}""");
        await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync("2026.09.26.3", 3, source,
            new Dictionary<string, string>(), "missing", fullClient: true, minimumLauncherVersion: "1.1.1"));
    }

    [Fact]
    public async Task NxOnlySourceIsRejectedForFullClientAndUnknownCandidatePayloadIsRejected()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [1]);
        CandidatePreparer preparer = new(project.Context, new ReleaseCatalog(project.Context, project.Signer));
        await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync("2026.09.26.1", 1, source,
            new Dictionary<string, string>(), "invalid", fullClient: true, minimumLauncherVersion: "1.1.1"));
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.26.1")));
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [2]);
        string candidate = await preparer.PrepareAsync("2026.09.26.1", 1, source,
            new Dictionary<string, string>(), "valid", fullClient: true, minimumLauncherVersion: "1.1.1");
        await File.WriteAllBytesAsync(Path.Combine(candidate, "files", "undeclared.dll"), [3]);
        await Assert.ThrowsAsync<PatchPublisherException>(() => new ReleaseBuilder(project.Context, project.Signer).BuildAsync("2026.09.26.1"));
    }

    [Fact]
    public async Task LegacyNxCannotBeMigrationBaseAndFreshBaselineReusesChunks()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync("2026.09.26.1", 1, new Dictionary<string, byte[]> { ["UI.nx"] = [1] });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.26.1");
        await project.MarkAsLegacyProtocolAsync("2026.09.26.1", nxOnly: true);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [2]);
        CandidatePreparer preparer = new(project.Context, new ReleaseCatalog(project.Context, project.Signer));
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync("2026.09.26.2", 2, source,
            new Dictionary<string, string>(), "upgrade", baseReleaseId: "2026.09.26.1", fullClient: true, minimumLauncherVersion: "1.1.1"));
        Assert.Contains("未記錄遊戲主程式", error.Message);
        string candidate = await preparer.PrepareAsync("2026.09.26.2", 2, source,
            new Dictionary<string, string>(), "fresh baseline", fullClient: true, minimumLauncherVersion: "1.1.1");
        string infoPath = Path.Combine(candidate, "release-info.json");
        var info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(infoPath))!;
        info["baseReleaseId"] = "2026.09.26.1";
        await File.WriteAllTextAsync(infoPath, info.ToJsonString());
        error = await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.26.2"));
        Assert.Contains("未記錄遊戲主程式", error.Message);
        info["baseReleaseId"] = null;
        await File.WriteAllTextAsync(infoPath, info.ToJsonString());
        var (manifest, report) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(1, report.NewLocalChunkCount); // unchanged NX reuses its existing chunk.
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:/game.exe")]
    [InlineData("dir/file:stream")]
    [InlineData("dir/CON.txt")]
    [InlineData("dir/file.")]
    [InlineData("dir//file")]
    [InlineData("dir/file ")]
    public void InvalidWindowsPathsAreRejected(string path) =>
        Assert.Throws<PatchPublisherException>(() => PathPolicy.ValidateInstallPath(path));

    [Fact]
    public async Task DiscardFullClientCandidatePreservesSourceAndCanPrepareAgain()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "data", "client.dll"), [2]);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        string candidate = await preparer.PrepareAsync("2026.09.26.1", 1, source, new Dictionary<string, string>(), "test", fullClient: true, minimumLauncherVersion: "1.1.1");
        await new ReleaseCancellationService(project.Context, catalog).DiscardCandidateAsync("2026.09.26.1");
        Assert.False(Directory.Exists(candidate));
        Assert.True(File.Exists(Path.Combine(source, "data", "client.dll")));
        await preparer.PrepareAsync("2026.09.26.1", 1, source, new Dictionary<string, string>(), "retry", fullClient: true, minimumLauncherVersion: "1.1.1");
        await new ReleaseBuilder(project.Context, project.Signer).BuildAsync("2026.09.26.1");
    }

    [Fact]
    public void FileDirectoryAndCaseCollisionsAreRejected()
    {
        Assert.Throws<PatchPublisherException>(() => PathPolicy.ValidateFileSet(["data", "data/a.dll"]));
        Assert.Throws<PatchPublisherException>(() => PathPolicy.ValidateFileSet(["Game.exe", "game.exe"]));
    }
}
