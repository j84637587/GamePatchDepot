using System.Text;

namespace GamePatchDepot.Tests;

public sealed class SecurityAndGcTests
{
    [Fact]
    public async Task SignedEnvelope_RejectsTampering()
    {
        await using TestProject project = await TestProject.CreateAsync(["UI.nx"]);
        ChannelPayload payload = new()
        {
            Channel = "stable",
            Generation = 1,
            ReleaseId = "2026.09.23.1",
            ContentVersion = 1,
            ManifestKey = "manifests/2026.09.23.1.json",
            ManifestSha256 = new string('a', 64),
            PublishedAt = DateTimeOffset.Parse("2026-09-23T00:00:00Z")
        };
        byte[] envelope = SignedEnvelopeService.Create(payload, PatchJsonContext.Default.ChannelPayload, project.Signer);
        ChannelPayload opened = SignedEnvelopeService.Open(envelope, PatchJsonContext.Default.ChannelPayload, project.Signer);
        Assert.Equal(payload.ReleaseId, opened.ReleaseId);

        string text = Encoding.UTF8.GetString(envelope);
        int payloadIndex = text.IndexOf("payloadBase64", StringComparison.Ordinal);
        Assert.True(payloadIndex >= 0);
        int characterIndex = text.IndexOf(':', payloadIndex) + 3;
        char replacement = text[characterIndex] == 'A' ? 'B' : 'A';
        byte[] tampered = Encoding.UTF8.GetBytes(text[..characterIndex] + replacement + text[(characterIndex + 1)..]);
        Assert.Throws<PatchPublisherException>(
            () => SignedEnvelopeService.Open(tampered, PatchJsonContext.Default.ChannelPayload, project.Signer));
    }

    [Fact]
    public void GcSelection_KeepsReferencedAndYoungObjects()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
        ObjectMetadata[] objects =
        [
            new("objects/sha256/aa/kept", 1, now.AddDays(-100), "1"),
            new("objects/sha256/bb/young", 2, now.AddDays(-2), "2"),
            new("objects/sha256/cc/orphan", 3, now.AddDays(-30), "3")
        ];
        HashSet<string> referenced = new(StringComparer.Ordinal) { "objects/sha256/aa/kept" };

        HashSet<string> result = GarbageCollector.SelectDeletableKeys(objects, referenced, now.AddDays(-14));

        Assert.Single(result);
        Assert.Contains("objects/sha256/cc/orphan", result);
    }

    [Theory]
    [InlineData("../evil.nx")]
    [InlineData("C:\\evil.nx")]
    [InlineData("folder/../../evil.nx")]
    public void PathPolicy_RejectsUnsafePaths(string path)
    {
        Assert.Throws<PatchPublisherException>(() => PathPolicy.ValidateInstallPath(path));
    }
}
