using System.Text.Json;

namespace GamePatchDepot.Tests;

public sealed class PrepareSelectionTests
{
    private static readonly Dictionary<string, string> NoReplacements = new();

    [Fact]
    public async Task WindowsStyleGameExecutablePathWorksAcrossMigrationAndRollback()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"], gameExecutable: @"bin\Game.exe");
        Assert.Equal("bin/Game.exe", project.Context.GameExecutable);
        string source = Path.Combine(project.Root, "client");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        await File.WriteAllBytesAsync(Path.Combine(source, "UI.nx"), [1]);
        string executable = Path.Combine(source, "bin", "Game.exe");
        await File.WriteAllBytesAsync(executable, [2]);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "base",
            fullClient: true, minimumLauncherVersion: "1.1.1");
        await builder.BuildAsync("2026.09.26.1");
        await project.MarkAsLegacyProtocolAsync("2026.09.26.1");
        await File.WriteAllBytesAsync(executable, [3]);
        await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "migration",
            baseReleaseId: "2026.09.26.1", fullClient: true, minimumLauncherVersion: "1.1.1");
        var (manifest, _) = await builder.BuildAsync("2026.09.26.2");
        Assert.Contains(manifest.Files, file => file.Path == "bin/Game.exe");
        await new LocalReleaseVerifier(project.Context, catalog).VerifyAsync(manifest.ReleaseId);
        ReleaseManifest rollback = await new RollbackBuilder(project.Context, project.Signer, catalog)
            .BuildAsync("2026.09.26.3", 3, manifest.ReleaseId);
        Assert.Contains(rollback.Files, file => file.Path == "bin/Game.exe");
    }

    [Fact]
    public async Task MinimumVersionCanRiseWithoutFileChangesButCannotDropBelowBase()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "base",
            fullClient: true, minimumLauncherVersion: "2.0.0");
        var (before, _) = await builder.BuildAsync("2026.09.26.1");
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync(
            "2026.09.26.2", 2, source, NoReplacements, "lower minimum", baseReleaseId: "2026.09.26.1",
            fullClient: true, minimumLauncherVersion: "1.1.1"));
        Assert.Contains("基準版本要求的 2.0.0", error.Message);
        string candidate = await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "raise minimum",
            baseReleaseId: "2026.09.26.1", fullClient: true, minimumLauncherVersion: "2.1.0");
        string path = Path.Combine(candidate, "release-info.json");
        var info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        info["minimumLauncherVersion"] = "1.1.1";
        await File.WriteAllTextAsync(path, info.ToJsonString());
        error = await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.26.2"));
        Assert.Contains("基準版本要求的 2.0.0", error.Message);
        info["minimumLauncherVersion"] = "2.1.0";
        await File.WriteAllTextAsync(path, info.ToJsonString());
        var (after, report) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal("2.1.0", after.MinimumLauncherVersion);
        Assert.Equal(before.Files.Select(file => file.Sha256), after.Files.Select(file => file.Sha256));
        Assert.Equal(0, report.NewLocalChunkCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.1.0", false)]
    [InlineData("invalid", false)]
    [InlineData("1.01.1", false)]
    [InlineData(null, true)]
    [InlineData("1.1.0", true)]
    public async Task TransitionRequiresCompatibleMinimumVersion(string? minimum, bool legacyBase)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        if (legacyBase)
        {
            await new CandidatePreparer(project.Context, catalog).PrepareAsync("2026.09.26.1", 1,
                source, NoReplacements, "base", fullClient: true, minimumLauncherVersion: "1.1.1");
            await builder.BuildAsync("2026.09.26.1");
            await project.MarkAsLegacyProtocolAsync("2026.09.26.1");
        }
        var error = await Assert.ThrowsAsync<PatchPublisherException>(() =>
            new CandidatePreparer(project.Context, catalog).PrepareAsync("2026.09.26.2", 2,
                source, NoReplacements, "transition", baseReleaseId: legacyBase ? "2026.09.26.1" : null,
                fullClient: true, minimumLauncherVersion: minimum));
        Assert.Contains("最低 NovaLink", error.Message);
        Assert.False(Directory.Exists(project.Context.CandidatePath("2026.09.26.2")));
        // Editing Candidate JSON must not bypass the same rule at Build.
        string candidate = await project.CreateCandidateAsync("2026.09.26.2", 2,
            new Dictionary<string, byte[]> { ["UI.nx"] = [1] });
        string infoPath = Path.Combine(candidate, "release-info.json");
        var info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(infoPath))!.AsObject();
        if (minimum is null) info.Remove("minimumLauncherVersion");
        else info["minimumLauncherVersion"] = minimum;
        await File.WriteAllTextAsync(infoPath, info.ToJsonString());
        await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.26.2"));
    }

    [Fact]
    public async Task CliMigrationRequiresAnUpdatedExecutableAndBuildRechecksItsHash()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        await File.WriteAllBytesAsync(Path.Combine(source, "Helper.exe"), [1]);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true, minimumLauncherVersion: "1.1.1",
            launcherTicketProtocol: 2);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        var (before, _) = await builder.BuildAsync("2026.09.26.1");
        await project.MarkAsLegacyProtocolAsync("2026.09.26.1");
        await File.WriteAllBytesAsync(Path.Combine(source, "Helper.exe"), [2]);
        await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync(
            "2026.09.26.2", 2, source, NoReplacements, "unchanged", baseReleaseId: "2026.09.26.1",
            fullClient: true, minimumLauncherVersion: "1.1.1"));
        byte[] oldExecutable = await File.ReadAllBytesAsync(Path.Combine(source, "Game.exe"));
        await File.AppendAllTextAsync(Path.Combine(source, "Game.exe"), "ready handoff implementation");
        string config = Path.Combine(project.Root, "config", "product.json");
        Assert.Equal(0, await Program.Main(["prepare", "--minimum-launcher-version", "1.1.1", "--config", config, "--full-client",
            "--release", "2026.09.26.2", "--content-version", "2", "--source", source,
            "--base", "2026.09.26.1", "--launcher-ticket-protocol", "2"]));
        var info = JsonSerializer.Deserialize<ReleaseInfo>(
            await File.ReadAllTextAsync(Path.Combine(project.Context.CandidatePath("2026.09.26.2"), "release-info.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Contains(info.Changes, change => change.Path == "Game.exe");
        string candidateExecutable = Path.Combine(project.Context.CandidatePath("2026.09.26.2"), "files", "Game.exe");
        await File.WriteAllBytesAsync(candidateExecutable, oldExecutable);
        var failure = await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.26.2"));
        Assert.Contains("已更新的遊戲 EXE", failure.Message);
        File.Copy(Path.Combine(source, "Game.exe"), candidateExecutable, true);
        var (after, report) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(2, after.LauncherTicketProtocol);
        Assert.Equal(before.Files.Where(file => !file.Path.EndsWith(".exe")).Select(file => file.Sha256),
            after.Files.Where(file => !file.Path.EndsWith(".exe")).Select(file => file.Sha256));
        Assert.Equal(1, report.NewLocalChunkCount);
        // An explicitly unchanged protocol must not bypass the no-change check.
        await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync(
            "2026.09.26.3", 3, source, NoReplacements, "no change", baseReleaseId: "2026.09.26.2",
            fullClient: true, minimumLauncherVersion: "1.1.1", launcherTicketProtocol: 2));
    }

    [Fact]
    public async Task FollowUpKeepsReadyProtocolAndRejectsManualDowngrade()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string source = await CreateSourceAsync(project);
        ReleaseCatalog catalog = new(project.Context, project.Signer);
        CandidatePreparer preparer = new(project.Context, catalog);
        ReleaseBuilder builder = new(project.Context, project.Signer);
        string first = await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true, minimumLauncherVersion: "1.1.1");
        string firstInfo = Path.Combine(first, "release-info.json");
        var info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(firstInfo))!;
        info["launcherTicketProtocol"] = 2;
        info["minimumLauncherVersion"] = "1.1.1";
        await File.WriteAllTextAsync(firstInfo, info.ToJsonString());
        await builder.BuildAsync("2026.09.26.1");
        await File.AppendAllTextAsync(Path.Combine(source, "Game.exe"), "changed");
        string next = await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "follow-up",
            baseReleaseId: "2026.09.26.1", fullClient: true);
        string nextInfo = Path.Combine(next, "release-info.json");
        info = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(nextInfo))!;
        Assert.Equal(2, info["launcherTicketProtocol"]!.GetValue<int>());
        Assert.Equal("1.1.1", info["minimumLauncherVersion"]!.GetValue<string>());
        info["launcherTicketProtocol"] = 1;
        await File.WriteAllTextAsync(nextInfo, info.ToJsonString());
        await Assert.ThrowsAsync<PatchPublisherException>(() => builder.BuildAsync("2026.09.26.2"));
        info["launcherTicketProtocol"] = 2;
        await File.WriteAllTextAsync(nextInfo, info.ToJsonString());
        var (manifest, _) = await builder.BuildAsync("2026.09.26.2");
        Assert.Equal(2, manifest.LauncherTicketProtocol);
        Assert.Equal("1.1.1", manifest.MinimumLauncherVersion);
        await Assert.ThrowsAsync<PatchPublisherException>(() => preparer.PrepareAsync(
            "2026.09.26.3", 3, source, NoReplacements, "downgrade", baseReleaseId: "2026.09.26.2",
            fullClient: true, minimumLauncherVersion: "1.1.1", launcherTicketProtocol: 1));
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
        Assert.Equal(0, await Program.Main(["prepare", "--minimum-launcher-version", "1.1.1", "--config", config, "--full-client", "--release", "2026.09.26.1",
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
            "2026.09.26.1", 1, source, NoReplacements, "selected", fullClient: true, minimumLauncherVersion: "1.1.1", selection: selection);
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
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true, minimumLauncherVersion: "1.1.1");
        await builder.BuildAsync("2026.09.26.1");
        PrepareSelectionService service = new(project.Context, catalog);
        PrepareSelection selection = await service.ScanAsync(source, "2026.09.26.1", NoReplacements);
        SelectionFile setting = selection.Files.Single(file => file.Path == "settings.ini");
        setting.Include = false;
        setting.Remove = remove;
        await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "choose", baseReleaseId: "2026.09.26.1",
            fullClient: true, minimumLauncherVersion: "1.1.1", selection: selection);
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
        await preparer.PrepareAsync("2026.09.26.1", 1, source, NoReplacements, "first", fullClient: true, minimumLauncherVersion: "1.1.1");
        await builder.BuildAsync("2026.09.26.1");
        File.Delete(Path.Combine(source, "optional.dll"));
        PrepareSelection selection = await new PrepareSelectionService(project.Context, catalog).ScanAsync(source, "2026.09.26.1", NoReplacements);
        SelectionFile missing = selection.Files.Single(file => file.Path == "optional.dll");
        Assert.False(missing.Include);
        Assert.False(missing.Remove);
        Assert.True(missing.CanRemove);
        await preparer.PrepareAsync("2026.09.26.2", 2, source, NoReplacements, "missing", baseReleaseId: "2026.09.26.1",
            fullClient: true, minimumLauncherVersion: "1.1.1", selection: selection);
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
            "2026.09.26.1", 1, source, NoReplacements, "drift", fullClient: true, minimumLauncherVersion: "1.1.1", selection: selection));
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
            "2026.09.26.1", 1, source, NoReplacements, "invalid", fullClient: true, minimumLauncherVersion: "1.1.1", selection: selection));
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
