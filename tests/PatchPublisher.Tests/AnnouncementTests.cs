using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace GamePatchDepot.Tests;

public sealed class AnnouncementTests
{
    private static AnnouncementDraft Draft() => new() { Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Title = "維護公告", Body = "第一行\n第二行 <script>不執行</script>", Category = "maintenance", Popup = true };

    [Fact]
    public void EditsKeepIdentityAndReadStateUntilExplicitlyReminded()
    {
        AnnouncementFeed first = AnnouncementService.Edit(new(), Draft(), false);
        AnnouncementFeed typo = AnnouncementService.Edit(first, Draft() with { Body = "修正錯字" }, false);
        Assert.Equal(2, typo.Revision);
        Assert.Equal(2, typo.Items[0].Revision);
        Assert.Equal(1, typo.Items[0].ReadRevision);
        Assert.Equal(first.Items[0].Id, typo.Items[0].Id);
        AnnouncementFeed reminded = AnnouncementService.Edit(typo, Draft() with { RemindAgain = true }, false);
        Assert.Equal(3, reminded.Items[0].ReadRevision);
        AnnouncementFeed withdrawn = AnnouncementService.Edit(reminded, Draft(), true);
        Assert.True(withdrawn.Items[0].Withdrawn);
        Assert.Single(withdrawn.Items);
        Assert.False(first.Items[0].Withdrawn);
        Assert.Throws<PatchPublisherException>(() => AnnouncementService.Edit(new(), Draft(), true));
        Assert.Throws<PatchPublisherException>(() => AnnouncementService.Edit(first, Draft() with { Title = "" }, false));
    }

    [Fact]
    public async Task PublicationIsSignedReadBackAndConditionalWithoutGameWrites()
    {
        await using TestProject project = await TestProject.CreateAsync(["file.dat"]);
        using FakeStore memory = new();
        await using R2ObjectStore store = new(memory, "test");
        AnnouncementService service = new(store, project.Signer);
        Assert.Equal(0, (await service.LoadAsync(default)).Revision);
        AnnouncementFeed first = await service.PublishAsync(Draft(), 0, false, default);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, memory.Objects.Count);
        Assert.All(memory.Objects.Keys, key => Assert.StartsWith("announcements/", key));
        Assert.Equal(AnnouncementService.CurrentKey, memory.Puts.Last());
        AnnouncementFeed signed = SignedEnvelopeService.Open(memory.Objects[AnnouncementService.CurrentKey], AnnouncementJsonContext.Default.AnnouncementFeed, project.Signer);
        Assert.Equal(Draft().Body, signed.Items[0].Body);
        byte[] before = memory.Objects[AnnouncementService.CurrentKey];
        await Assert.ThrowsAsync<PatchPublisherException>(() => service.PublishAsync(Draft(), 0, false, default));
        Assert.Equal(before, memory.Objects[AnnouncementService.CurrentKey]);
        memory.RejectCurrent = true;
        await Assert.ThrowsAsync<PatchPublisherException>(() => service.PublishAsync(Draft(), 1, false, default));
        Assert.Equal(before, memory.Objects[AnnouncementService.CurrentKey]);
        memory.RejectCurrent = false;
        Assert.True((await service.PublishAsync(Draft(), 1, true, default)).Items[0].Withdrawn);
    }

    [Fact]
    public async Task CSharpAnnouncementSignatureFixture()
    {
        await using TestProject project = await TestProject.CreateAsync(["file.dat"]);
        AnnouncementFeed feed = AnnouncementService.Edit(new(), Draft(), false);
        byte[] envelope = SignedEnvelopeService.Create(feed, AnnouncementJsonContext.Default.AnnouncementFeed, project.Signer);
        Assert.Equal(feed.Revision, SignedEnvelopeService.Open(envelope, AnnouncementJsonContext.Default.AnnouncementFeed, project.Signer).Revision);
        string? output = Environment.GetEnvironmentVariable("ANNOUNCEMENT_TEST_FIXTURE_PATH");
        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new {
                publicKey = await File.ReadAllTextAsync(project.Context.PublicKeyPath), envelopeBase64 = Convert.ToBase64String(envelope)
            }));
        }
    }

    private sealed class FakeStore() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true, AuthenticationRegion = "auto" })
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public List<string> Puts { get; } = [];
        public bool RejectCurrent { get; set; }
        private byte[] Get(string key) => Objects.TryGetValue(key, out byte[]? bytes) ? bytes : throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken token = default)
        {
            byte[] bytes = Get(request.Key);
            return Task.FromResult(new GetObjectMetadataResponse { ContentLength = bytes.Length, ETag = FileUtilities.Sha256Hex(bytes) });
        }
        public override Task<GetObjectResponse> GetObjectAsync(string bucket, string key, CancellationToken token = default)
        {
            byte[] bytes = Get(key);
            return Task.FromResult(new GetObjectResponse { ContentLength = bytes.Length, ResponseStream = new MemoryStream(bytes) });
        }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken token = default)
        {
            bool exists = Objects.TryGetValue(request.Key, out byte[]? old);
            if ((RejectCurrent && request.Key == AnnouncementService.CurrentKey) ||
                (request.Headers["If-None-Match"] == "*" && exists) ||
                (request.Headers["If-Match"] is { Length: > 0 } etag && (!exists || etag.Trim('"') != FileUtilities.Sha256Hex(old!))))
                throw new AmazonS3Exception("conflict") { StatusCode = HttpStatusCode.PreconditionFailed };
            using MemoryStream buffer = new(); await request.InputStream.CopyToAsync(buffer, token);
            byte[] bytes = buffer.ToArray(); Objects[request.Key] = bytes; Puts.Add(request.Key);
            return new PutObjectResponse { ETag = FileUtilities.Sha256Hex(bytes) };
        }
    }
}
