namespace GamePatchDepot.Tests;

public sealed class ReleaseCancellationTests
{
    [Fact]
    public async Task DiscardCandidate_RemovesOnlyUnbuiltCandidate()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync(
            "2026.09.24.1",
            1,
            new Dictionary<string, byte[]> { ["UI.nx"] = [1, 2, 3] });
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        ReleaseCancellationService service = new(project.Context, catalog);

        await service.DiscardCandidateAsync("2026.09.24.1");

        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.24.1")));
        Assert.False(File.Exists(project.Context.CancelledReleasePath("2026.09.24.1")));
    }

    [Fact]
    public async Task CancelBuiltRelease_PreservesManifestAndReservesVersion()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        await project.CreateCandidateAsync(
            "2026.09.24.1",
            1,
            new Dictionary<string, byte[]> { ["UI.nx"] = [1, 2, 3] });
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.24.1");
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        ReleaseCancellationService service = new(project.Context, catalog);

        CancelledRelease cancelled = await service.CancelAsync("2026.09.24.1", "內容不正確");

        Assert.Equal("built", cancelled.Stage);
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.24.1")));
        Assert.True(File.Exists(project.Context.ManifestPath("2026.09.24.1")));
        Assert.True(File.Exists(project.Context.ManifestSignaturePath("2026.09.24.1")));
        Assert.True(File.Exists(project.Context.CancelledReleasePath("2026.09.24.1")));
        await Assert.ThrowsAsync<PatchPublisherException>(() =>
            catalog.EnsureContentVersionAvailableAsync("2026.09.24.2", 1));
    }
}
