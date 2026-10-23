using System.Text.Json;

namespace GamePatchDepot.Tests;

public sealed class PrepareSelectionTests
{
    private static readonly Dictionary<string, string> NoReplacements = new();

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task CliCanPrepareProtocolOnlyReleaseWithoutChangingFiles(int previous, int desired)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true,
            launcherTicketProtocol: previous);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        var (before, _) = await builder.BuildAsync("2026.09.26.1");
        string config = Path.Combine(project.Root, "config", "product.json");
        Assert.Equal(0, await Program.Main(["prepare", "--config", config, "--full-client",
            "--release", "2026.09.26.2", "--content-version", "2", "--source", source,
            "--base", "2026.09.26.1", "--launcher-ticket-protocol", desired.ToString()]));
        var info = JsonSerializer.Deserialize<ReleaseInfo>(
            await File.ReadAllTextAsync(Path.Combine(project.Context.CandidatePath("2026.09.26.2"), "release-info.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(info.Changes);
        var (after, report) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(desired, after.LauncherTicketProtocol);
        Assert.Equal(before.Files.Select(file => file.Sha256), after.Files.Select(file => file.Sha256));
        Assert.Equal(0, report.NewLocalChunkCount);
        // An explicitly unchanged protocol must not bypass the no-change check.
        await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync(
            "2026.09.26.3", 3, source, NoReplacements, "no change", baseReleaseId: "2026.09.26.2",
            fullClient: true, launcherTicketProtocol: desired));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowUpInheritsTicketProtocolAndCanExplicitlyDowngrade(bool downgrade)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        string first = await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true);
        string firstInfo = Path.Combine(first, "release-info.json");
        var info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(firstInfo))!;
        info["launcherTicketProtocol"] = 2;
        info["minimumLauncherVersion"] = "1.1.0";
        await File.WriteAllTextAsync(firstInfo, info.ToJsonString());
        await builder.BuildAsync("2026.09.26.1");
        await File.AppendAllTextAsync(Path.Combine(source, "Game.exe"), "changed");
        string next = await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "follow-up",
            baseReleaseId: "2026.09.26.1", fullClient: true);
        string nextInfo = Path.Combine(next, "release-info.json");
        info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(nextInfo))!;
        Assert.Equal(2, info["launcherTicketProtocol"]!.GetValue<int>());
        Assert.Equal("1.1.0", info["minimumLauncherVersion"]!.GetValue<string>());
        if (downgrade)
        {
            info["launcherTicketProtocol"] = 1;
            await File.WriteAllTextAsync(nextInfo, info.ToJsonString());
        }
        var (manifest, _) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(downgrade ? 1 : 2, manifest.LauncherTicketProtocol);
        Assert.Equal("1.1.0", manifest.MinimumLauncherVersion);
    }

    [Fact]
    public async Task CliPreviewAndPrepareUseTheConfirmedSelection()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        string config = Path.Combine(project.Root, "config", "product.json");
        string output = Path.Combine(project.Root, "selection.json");
        Assert.Equal(0, await Program.Main(["preview", "--config", config, "--source", source, "--output", output]));
        PrepareSelection selection = JsonSerializer.Deserialize<PrepareSelection>(await File.ReadAllBytesAsync(output), PrepareSelection.JsonOptions)!;
        selection.Files.Single(file => file.Path == "settings.ini").Include = false;
        await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(selection, PrepareSelection.JsonOptions));
        Assert.Equal(0, await Program.Main(["prepare", "--config", config, "--full-client", "--release", "2026.09.26.1",
            "--content-version", "1", "--source", source, "--selection", output]));
        string candidate = project.Context.CandidatePath("2026.09.26.1");
        Assert.True(File.Exists(Path.Combine(candidate, "files", "Game.exe")));
        Assert.False(File.Exists(Path.Combine(candidate, "files", "settings.ini")));
    }

    [Fact]
    public async Task PreviewDoesNotCopyAndConfirmedSelectionBuildsOnlyIncludedFiles()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        PrepareSelection selection = await new PrepareSelectionService(project.Context, catalog).ScanAsync(source, null, NoReplacements);
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.26.1")));
        Assert.True(selection.Files.Single(file => file.Path == "Game.exe").Include);
        Assert.False(selection.Files.Single(file => file.Path == "secret.pem").CanInclude);
        selection.Files.Single(file => file.Path == "settings.ini").Include = false;
        // 跨 CLI／UI 的 JSON 往返必須保留選擇。
        selection = JsonSerializer.Deserialize<PrepareSelection>(
            JsonSerializer.Serialize(selection, PrepareSelection.JsonOptions), PrepareSelection.JsonOptions)!;
        string candidate = await new CandidatePreparer(project.Context, catalog).PrepareAsync(
            "2026.09.26.1", 1, source, NoReplacements, "selected", fullClient: true, selection: selection);
        Assert.False(File.Exists(Path.Combine(candidate, "files", "settings.ini")));
        (ReleaseManifest manifest, _) = await new ReleaseBuilder(project.Context, project.Signer).BuildAsync("2026.09.26.1");
        Assert.Equal(["Game.exe", "optional.dll"], manifest.Files.Select(file => file.Path).Order());
        Assert.Contains("settings.ini", manifest.Preserve!);
        Assert.Contains("secret.pem", manifest.Preserve!);
        Assert.Empty(manifest.Delete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyManagedFileCanBePreservedOrExplicitlyRemoved(bool remove)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true);
        await builder.BuildAsync("2026.09.26.1");
        PrepareSelectionService service = new(project.Context, catalog);
        PrepareSelection selection = await service.ScanAsync(source, "2026.09.26.1", NoReplacements);
        SelectionFile setting = selection.Files.Single(file => file.Path == "settings.ini");
        setting.Include = false;
        setting.Remove = remove;
        await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "choose", baseReleaseId: "2026.09.26.1",
            fullClient: true, selection: selection);
        (ReleaseManifest manifest, BuildReport report) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(0, report.NewLocalChunkCount);
        Assert.DoesNotContain(manifest.Files, file => file.Path == "settings.ini");
        Assert.Equal(remove, manifest.Delete.Contains("settings.ini"));
        Assert.Equal(!remove, manifest.Preserve!.Contains("settings.ini"));
        Assert.True(File.Exists(Path.Combine(source, "settings.ini")));
        if (!remove)
        {
            PrepareSelection next = await service.ScanAsync(source, "2026.09.26.2", NoReplacements);
            Assert.False(next.Files.Single(file => file.Path == "settings.ini").Include);
        }
    }

    [Fact]
    public async Task MissingOldFileDefaultsToPreserveInsteadOfDelete()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true);
        await builder.BuildAsync("2026.09.26.1");
        File.Delete(Path.Combine(source, "optional.dll"));
        PrepareSelection selection = await new PrepareSelectionService(project.Context, catalog).ScanAsync(source, "2026.09.26.1", NoReplacements);
        SelectionFile missing = selection.Files.Single(file => file.Path == "optional.dll");
        Assert.False(missing.Include);
        Assert.False(missing.Remove);
        Assert.True(missing.CanRemove);
        await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "missing", baseReleaseId: "2026.09.26.1",
            fullClient: true, selection: selection);
        (ReleaseManifest manifest, _) = await builder.BuildAsync("2026.09.26.2");
        Assert.Empty(manifest.Delete);
        Assert.Contains("optional.dll", manifest.Preserve!);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("changed")]
    [InlineData("deleted")]
    [InlineData("policy")]
    public async Task SourceOrPolicyDriftRequiresAnotherPreview(string change)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        PrepareSelection selection = await new PrepareSelectionService(project.Context, catalog).ScanAsync(source, null, NoReplacements);
        switch (change)
        {
            case "added": await File.WriteAllTextAsync(Path.Combine(source, "new.dat"), "new"); break;
            case "changed": await File.WriteAllTextAsync(Path.Combine(source, "Game.exe"), "changed contents"); break;
            case "deleted": File.Delete(Path.Combine(source, "optional.dll")); break;
            case "policy": await File.WriteAllTextAsync(Path.Combine(project.Root, "config", "client-files.json"), """{"requiredFiles":["Game.exe"]}"""); break;
        }
        await Assert.ThrowsAsync<PatchPublisherException>(() => new CandidatePreparer(project.Context, catalog).PrepareAsync(
            "2026.09.26.1", 1, source, NoReplacements, "drift", fullClient: true, selection: selection));
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.26.1")));
    }

    [Theory]
    [InlineData("Game.exe", false)]
    [InlineData("secret.pem", true)]
    [InlineData("optional.dll", false)]
    public async Task RequiredAndExcludedRulesCannotBeBypassed(string path, bool include)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        await File.WriteAllTextAsync(Path.Combine(project.Root, "config", "client-files.json"), """{"requiredFiles":["optional.dll"]}""");
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        PrepareSelection selection = await new PrepareSelectionService(project.Context, catalog).ScanAsync(source, null, NoReplacements);
        selection.Files.Single(file => file.Path == path).Include = include;
        await Assert.ThrowsAsync<PatchPublisherException>(() => new CandidatePreparer(project.Context, catalog).PrepareAsync(
            "2026.09.26.1", 1, source, NoReplacements, "invalid", fullClient: true, selection: selection));
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.26.1")));
    }

    private static async Task<string> CreateSourceAsync(TestProject project)
    {
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "Game.exe"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "optional.dll"), [2]);
        await File.WriteAllTextAsync(Path.Combine(source, "settings.ini"), "player settings");
        await File.WriteAllTextAsync(Path.Combine(source, "secret.pem"), "excluded fixture");
        return source;
    }
}
