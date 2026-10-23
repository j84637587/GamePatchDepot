using System.Text;

namespace GamePatchDepot.Tests;

public sealed class DeltaPatchTests
{
    [Fact]
    public async Task InsertedPrefixProducesSmallPatchRebuildsAndKeepsFullFallback()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"], enableDeltaPatches: true);
        InstallTools(project);
        byte[] old = TestProject.DeterministicBytes(4 * 1024 * 1024, 9);
        await project.CreateCandidateAsync("2026.09.27.1", 1, new Dictionary<string, byte[]> { ["UI.nx"] = old });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        (ReleaseManifest first, _) = await builder.BuildAsync("2026.09.27.1");
        Assert.Null(first.Files.Single(file => file.Path == "UI.nx").Patch);
        byte[] next = [.. Encoding.UTF8.GetBytes("insert at front to shift all chunk boundaries"), .. old];
        next[2 * 1024 * 1024] ^= 1;
        await project.CreateCandidateAsync("2026.09.27.2", 2, new Dictionary<string, byte[]> { ["UI.nx"] = next },
            [new ReleaseChange { Path = "UI.nx" }], baseReleaseId: first.ReleaseId);
        (ReleaseManifest second, _) = await builder.BuildAsync("2026.09.27.2");
        ManifestFile target = Assert.Single(second.Files, file => file.Path == "UI.nx");
        FileDeltaPatch patch = Assert.IsType<FileDeltaPatch>(target.Patch);
        Assert.True(patch.Size < next.Length / 10, $"patch={patch.Size}; full={next.Length}");
        Assert.Equal(first.Files.Single(file => file.Path == "UI.nx").Sha256, patch.BaseSha256);
        Assert.Equal((long)old.Length, patch.BaseSize);
        Assert.NotEmpty(target.Chunks);
        Assert.Equal(next.LongLength, target.Chunks.Sum(chunk => (long)chunk.Size));
        Assert.Contains(ObjectKeys.Payloads(second), payload => payload.Key == patch.Key);
        await new LocalReleaseVerifier(project.Context, new ReleaseCatalog(project.Context, project.Signer)).VerifyAsync(second.ReleaseId);
        byte[] manifestBefore = await File.ReadAllBytesAsync(project.Context.ManifestPath(second.ReleaseId));
        await builder.BuildAsync(second.ReleaseId);
        Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(project.Context.ManifestPath(second.ReleaseId)));
        string restored = Path.Combine(project.Root, "restored.nx");
        await new HdiffPatchTool(Path.Combine(project.Root, "tools", "hdiffpatch")).ApplyAsync(
            Path.Combine(project.Context.CandidatePath(first.ReleaseId), "files", "UI.nx"),
            ObjectKeys.LocalPatchPath(project.Context, patch.Sha256), restored, CancellationToken.None);
        Assert.Equal(next, await File.ReadAllBytesAsync(restored));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(project.Root, "work", "staging")));
        Assert.DoesNotContain("\"patch\"", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(project.Context.ManifestPath(first.ReleaseId))));
        string cachedPatch = ObjectKeys.LocalPatchPath(project.Context, patch.Sha256);
        byte[] corruptPatch = await File.ReadAllBytesAsync(cachedPatch);
        corruptPatch[0] ^= 1;
        await File.WriteAllBytesAsync(cachedPatch, corruptPatch);
        await Assert.ThrowsAsync<PatchPublisherException>(() =>
            new LocalReleaseVerifier(project.Context, new ReleaseCatalog(project.Context, project.Signer)).VerifyAsync(second.ReleaseId));
    }

    [Fact]
    public async Task TinyChangedFileDoesNotKeepLargerPatch()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"], enableDeltaPatches: true);
        InstallTools(project);
        await project.CreateCandidateAsync("2026.09.27.1", 1, new Dictionary<string, byte[]> { ["UI.nx"] = [1] });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.27.1");
        await project.CreateCandidateAsync("2026.09.27.2", 2, new Dictionary<string, byte[]> { ["UI.nx"] = [2] },
            [new ReleaseChange { Path = "UI.nx" }], baseReleaseId: "2026.09.27.1");
        (ReleaseManifest next, _) = await builder.BuildAsync("2026.09.27.2");
        Assert.Null(next.Files.Single(file => file.Path == "UI.nx").Patch);
        Assert.NotEmpty(next.Files.Single(file => file.Path == "UI.nx").Chunks);
    }

    [Fact]
    public async Task NewAndUnchangedFilesHaveNoPatchAndOldPatchDoesNotCarryForward()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"], enableDeltaPatches: true);
        InstallTools(project);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [1, 2]);
        byte[] old = TestProject.DeterministicBytes(1024 * 1024, 13);
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), old);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.27.1", 1, source, new Dictionary<string, string>(), "first", fullClient: true, minimumLauncherVersion: "1.1.1");
        await builder.BuildAsync("2026.09.27.1");
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [5, .. old]);
        await File.WriteAllBytesAsync(Path.Combine(source, "new.dll"), [4]);
        await preparer.PrepareAsync("2026.09.27.2", 2, source, new Dictionary<string, string>(), "second", baseReleaseId: "2026.09.27.1", fullClient: true, minimumLauncherVersion: "1.1.1");
        (ReleaseManifest second, _) = await builder.BuildAsync("2026.09.27.2");
        Assert.NotNull(second.Files.Single(file => file.Path == "UI.nx").Patch);
        Assert.Null(second.Files.Single(file => file.Path == "Game.exe").Patch);
        Assert.Null(second.Files.Single(file => file.Path == "new.dll").Patch);
        await File.WriteAllBytesAsync(Path.Combine(source, "new.dll"), [9]);
        await preparer.PrepareAsync("2026.09.27.3", 3, source, new Dictionary<string, string>(), "third", baseReleaseId: "2026.09.27.2", fullClient: true, minimumLauncherVersion: "1.1.1");
        (ReleaseManifest third, _) = await builder.BuildAsync("2026.09.27.3");
        Assert.Null(third.Files.Single(file => file.Path == "UI.nx").Patch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptBaseCannotProducePatchOrSignedManifest(bool missing)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"], enableDeltaPatches: true);
        InstallTools(project);
        await project.CreateCandidateAsync("2026.09.27.1", 1, new Dictionary<string, byte[]> { ["UI.nx"] = [1, 2, 3] });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        (ReleaseManifest first, _) = await builder.BuildAsync("2026.09.27.1");
        ManifestChunk chunk = first.Files.Single(file => file.Path == "UI.nx").Chunks[0];
        string baseChunk = ObjectKeys.LocalChunkPath(project.Context.ChunkRoot, chunk.Sha256);
        if (missing) File.Delete(baseChunk);
        else await File.WriteAllBytesAsync(baseChunk, [3, 2, 1]);
        await project.CreateCandidateAsync("2026.09.27.2", 2, new Dictionary<string, byte[]> { ["UI.nx"] = [9, 8, 7] },
            [new ReleaseChange { Path = "UI.nx" }], baseReleaseId: first.ReleaseId);
        PatchPublisherException error = await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.27.2"));
        Assert.Contains(missing ? "本機保留基準版本" : "基準 chunk SHA-256", error.Message);
        Assert.False(File.Exists(project.Context.ManifestPath("2026.09.27.2")));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("base")]
    [InlineData("size")]
    [InlineData("key")]
    public async Task InvalidPatchMetadataIsRejected(string invalid)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync("2026.09.27.1", 1, new Dictionary<string, byte[]> { ["UI.nx"] = [1, 2, 3, 4] });
        (ReleaseManifest manifest, _) = await new ReleaseBuilder(project.Context, project.Signer).BuildAsync("2026.09.27.1");
        ManifestFile file = manifest.Files.Single(file => file.Path == "UI.nx");
        string hash = FileUtilities.Sha256Hex([8]);
        manifest.Files[manifest.Files.FindIndex(item => item.Path == "UI.nx")] = new ManifestFile
        {
            Path = file.Path, Size = file.Size, Sha256 = file.Sha256, Chunks = file.Chunks,
            Patch = new FileDeltaPatch
            {
                Format = invalid == "format" ? "unknown" : "hdiffpatch-sf20-zstd",
                BaseSha256 = invalid == "base" ? file.Sha256 : new string('a', 64), BaseSize = 3,
                Size = invalid == "size" ? 4 : 1, Sha256 = hash,
                Key = invalid == "key" ? "objects/other" : ObjectKeys.ForPatch(hash)
            }
        };
        Assert.Throws<PatchPublisherException>(() => new ReleaseCatalog(project.Context, project.Signer).ValidateManifest(manifest, manifest.ReleaseId));
    }

    private static void InstallTools(TestProject project)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "setup-patch-tools.ps1"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string target = Path.Combine(project.Root, "tools", "hdiffpatch");
        Directory.CreateDirectory(target);
        foreach (string name in new[] { "hdiffz.exe", "hpatchz.exe" })
        {
            string source = Path.Combine(directory.FullName, "tools", "hdiffpatch", name);
            Assert.True(File.Exists(source), "請先執行 setup-patch-tools.ps1，再執行補丁整合測試。");
            File.Copy(source, Path.Combine(target, name));
        }
    }
}
