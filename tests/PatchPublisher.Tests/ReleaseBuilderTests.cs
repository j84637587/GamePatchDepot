namespace GamePatchDepot.Tests;

public sealed class ReleaseBuilderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Build_PreservesAndValidatesSignedTicketProtocol(int protocol)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string candidate = await project.CreateCandidateAsync("2026.09.23.1", 1008,
            new Dictionary<string, byte[]> { ["UI.nx"] = [1, 2, 3] });
        string infoPath = Path.Combine(candidate, "release-info.json");
        var info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(infoPath))!;
        info["launcherTicketProtocol"] = protocol;
        await File.WriteAllTextAsync(infoPath, info.ToJsonString());
        ReleaseBuilder builder = new(project.Context, project.Signer);
        if (protocol == 3)
        {
            await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.23.1"));
            return;
        }
        (ReleaseManifest manifest, _) = await builder.BuildAsync("2026.09.23.1");
        Assert.Equal(protocol, manifest.LauncherTicketProtocol);
        var verified = await new ReleaseCatalog(project.Context, project.Signer).LoadVerifiedAsync("2026.09.23.1");
        Assert.Equal(protocol, verified.Manifest.LauncherTicketProtocol);
    }

    [Fact]
    public async Task Build_SplitsLargeFileAndCanVerifyLocally()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        byte[] data = TestProject.DeterministicBytes(2 * 1024 * 1024 + 12345, 42);
        await project.CreateCandidateAsync(
            "2026.09.23.1",
            1008,
            new Dictionary<string, byte[]> { ["UI.nx"] = data },
            [new ReleaseChange { Path = "UI.nx", SourceRevision = "UI40" }]);

        ReleaseBuilder builder = new(project.Context, project.Signer);
        (ReleaseManifest manifest, BuildReport firstReport) = await builder.BuildAsync("2026.09.23.1");

        ManifestFile file = Assert.Single(manifest.Files);
        Assert.Equal(3, file.Chunks.Count);
        Assert.Equal(1024 * 1024, file.Chunks[0].Size);
        Assert.Equal(1024 * 1024, file.Chunks[1].Size);
        Assert.Equal(12345, file.Chunks[2].Size);
        Assert.Equal(data.LongLength, file.Size);
        Assert.Equal("UI40", file.SourceRevision);
        Assert.Equal(3, firstReport.NewLocalChunkCount);

        ReleaseCatalog catalog = new(project.Context, project.Signer);
        LocalReleaseVerifier verifier = new(project.Context, catalog);
        await verifier.VerifyAsync("2026.09.23.1");

        (_, BuildReport secondReport) = await builder.BuildAsync("2026.09.23.1");
        Assert.Equal(0, secondReport.NewLocalChunkCount);
    }

    [Fact]
    public async Task Build_RejectsUnexpectedFile()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string candidate = await project.CreateCandidateAsync(
            "2026.09.23.1",
            1008,
            new Dictionary<string, byte[]> { ["UI.nx"] = [1, 2, 3] });
        await File.WriteAllTextAsync(Path.Combine(candidate, "notes.txt"), "不應出現在 Candidate");

        ReleaseBuilder builder = new(project.Context, project.Signer);
        PatchPublisherException exception = await Assert.ThrowsAsync<PatchPublisherException>(
            () => builder.BuildAsync("2026.09.23.1"));
        Assert.Contains("不允許", exception.Message);
    }

    [Fact]
    public async Task Build_DetectsCorruptedLocalChunk()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync(
            "2026.09.23.1",
            1008,
            new Dictionary<string, byte[]> { ["UI.nx"] = TestProject.DeterministicBytes(2000, 1) });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        (ReleaseManifest manifest, _) = await builder.BuildAsync("2026.09.23.1");
        ManifestChunk chunk = Assert.Single(Assert.Single(manifest.Files).Chunks);
        string chunkPath = ObjectKeys.LocalChunkPath(project.Context.ChunkRoot, chunk.Sha256);
        await File.WriteAllBytesAsync(chunkPath, TestProject.DeterministicBytes(chunk.Size, 9));

        ReleaseCatalog catalog = new(project.Context, project.Signer);
        LocalReleaseVerifier verifier = new(project.Context, catalog);
        await Assert.ThrowsAsync<PatchPublisherException>(() => verifier.VerifyAsync("2026.09.23.1"));
    }
}
