using System.Text.Json;

namespace GamePatchDepot.Tests;

public sealed class ManifestDiffTests
{
    [Fact]
    public async Task Diff_ReportsOnlyChangedFileAndNewChunks()
    {
        await using TestProject project = await TestProject.CreateAsync(["A.nx", "B.nx"]);
        byte[] unchanged = TestProject.DeterministicBytes(1000, 1);
        await project.CreateCandidateAsync(
            "2026.09.23.1",
            1,
            new Dictionary<string, byte[]>
            {
                ["A.nx"] = unchanged,
                ["B.nx"] = TestProject.DeterministicBytes(2000, 2)
            });
        await project.CreateCandidateAsync(
            "2026.09.23.2",
            2,
            new Dictionary<string, byte[]>
            {
                ["A.nx"] = unchanged,
                ["B.nx"] = TestProject.DeterministicBytes(3000, 3)
            });

        ReleaseBuilder builder = new(project.Context, project.Signer);
        await builder.BuildAsync("2026.09.23.1");
        await builder.BuildAsync("2026.09.23.2");
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        ManifestDiffService diff = new(project.Context, catalog);
        DiffReport report = await diff.CreateAsync("2026.09.23.2", "2026.09.23.1");

        Assert.Single(report.Changed);
        Assert.Equal("B.nx", report.Changed[0].Path);
        Assert.Single(report.Unchanged);
        Assert.Equal("A.nx", report.Unchanged[0]);
        Assert.Equal(3000, report.NewUniqueChunkBytes);
    }

    [Fact]
    public async Task Diff_SumsChunkBytesAboveInt32Limit()
    {
        await using TestProject project = await TestProject.CreateAsync(["Large.nx"]);
        const string releaseId = "2026.09.23.1";
        const int chunkSize = 1_500_000_000;
        string firstHash = new('a', 64);
        string secondHash = new('b', 64);
        ReleaseManifest manifest = new()
        {
            Product = project.Context.Config.Product,
            ReleaseId = releaseId,
            ContentVersion = 1,
            GameVersion = project.Context.Config.GameVersion,
            MinimumLauncherVersion = "1.0.0",
            CreatedAt = DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            Files =
            [
                new ManifestFile
                {
                    Path = "Large.nx",
                    Size = 3_000_000_000,
                    Sha256 = new string('c', 64),
                    Chunks =
                    [
                        new ManifestChunk
                        {
                            Offset = 0,
                            Size = chunkSize,
                            Sha256 = firstHash,
                            Key = ObjectKeys.ForChunk(firstHash)
                        },
                        new ManifestChunk
                        {
                            Offset = chunkSize,
                            Size = chunkSize,
                            Sha256 = secondHash,
                            Key = ObjectKeys.ForChunk(secondHash)
                        }
                    ]
                }
            ]
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PatchJsonContext.Default.ReleaseManifest);
        await File.WriteAllBytesAsync(project.Context.ManifestPath(releaseId), bytes);
        await File.WriteAllTextAsync(project.Context.ManifestSignaturePath(releaseId), project.Signer.Sign(bytes));

        ReleaseCatalog catalog = new(project.Context, project.Signer);
        ManifestDiffService diff = new(project.Context, catalog);
        DiffReport report = await diff.CreateAsync(releaseId, "none");

        Assert.Equal(3_000_000_000, report.NewUniqueChunkBytes);
    }
}
