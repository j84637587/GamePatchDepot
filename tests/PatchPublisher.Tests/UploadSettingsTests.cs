using System.Text.Json.Nodes;
using GamePatchDepot.Manager;

namespace GamePatchDepot.Tests;

public sealed class UploadSettingsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task SelectionPersistsAndPreservesOtherProductSettings(int value)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string path = Path.Combine(project.Root, "config", "product.json");
        JsonNode before = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        before["futureSetting"] = new JsonObject { ["enabled"] = true };
        await File.WriteAllTextAsync(path, before.ToJsonString());
        UploadSettings.Save(path, value);
        Assert.Equal(value, UploadSettings.Load(path));
        Assert.Equal(value, (await ProjectContext.LoadAsync(path)).Config.UploadConcurrency);
        before["uploadConcurrency"] = value;
        Assert.True(JsonNode.DeepEquals(before, JsonNode.Parse(await File.ReadAllTextAsync(path))));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task MissingValueUsesPublisherDefaultWithoutWritingConfig()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string path = Path.Combine(project.Root, "config", "product.json");
        JsonObject config = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        config.Remove("uploadConcurrency");
        await File.WriteAllTextAsync(path, config.ToJsonString());
        byte[] before = await File.ReadAllBytesAsync(path);
        Assert.Equal(4, UploadSettings.Load(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task InvalidSelectionDoesNotChangeConfig(int value)
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string path = Path.Combine(project.Root, "config", "product.json");
        byte[] before = await File.ReadAllBytesAsync(path);
        Assert.Throws<InvalidDataException>(() => UploadSettings.Save(path, value));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MalformedConfigIsNotOverwritten()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        string path = Path.Combine(project.Root, "config", "product.json");
        await File.WriteAllTextAsync(path, "invalid json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => UploadSettings.Save(path, 2));
        Assert.Equal("invalid json", await File.ReadAllTextAsync(path));
    }
}
