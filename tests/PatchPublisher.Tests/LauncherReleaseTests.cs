using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace GamePatchDepot.Tests;

public sealed class LauncherReleaseTests
{
    [Fact]
    public void RealTauriFixtureMatchesRustMinisignRules()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "launcher-r2.json")));
        JsonElement f = doc.RootElement;
        string key = f.GetProperty("publicKey").GetString()!;
        byte[] payload = Convert.FromBase64String(f.GetProperty("manifestBase64").GetString()!);
        string signature = f.GetProperty("signature").GetString()!;
        LauncherPackage.VerifySignature(payload, signature, key);
        byte[] exe = Convert.FromBase64String(f.GetProperty("exeBase64").GetString()!);
        LauncherPackage.VerifySignature(exe, f.GetProperty("exeSignature").GetString()!, key);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, algorithm = "minisign", payloadBase64 = Convert.ToBase64String(payload), signature });
        Assert.Equal("0.7.0", LauncherPackage.ParseChannel(envelope, key).Version);
        payload[0] ^= 1;
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.VerifySignature(payload, signature, key));
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.VerifySignature(exe, signature, key));
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.VerifySignature(exe, f.GetProperty("exeSignature").GetString()!, LauncherPackage.PublicKey));
        string text = Encoding.UTF8.GetString(Convert.FromBase64String(signature)).Replace("file:stable.json", "file:changed.json", StringComparison.Ordinal);
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.VerifySignature(Convert.FromBase64String(f.GetProperty("manifestBase64").GetString()!), Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), key));
    }

    [Theory]
    [InlineData("product", "Other")]
    [InlineData("target", "linux-x86_64")]
    [InlineData("channel", "test")]
    [InlineData("version", "0.6.2-beta")]
    [InlineData("version", "01.2.3")]
    [InlineData("version", "9999999999999999999999.0.0")]
    [InlineData("key", "../../outside.exe")]
    [InlineData("signatureKey", "launcher/releases/old/NovaLink.exe.sig")]
    [InlineData("sha256", "bad")]
    [InlineData("publishedAt", "invalid")]
    [InlineData("publishedAt", "2026-10-03")]
    public void ValidSignatureCannotOverrideManifestPolicy(string field, string value)
    {
        using BundleFixture fixture = new();
        JsonObject manifest = fixture.Manifest("0.6.2");
        manifest[field] = value;
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.ParseChannel(fixture.Envelope(manifest), fixture.PublicKey));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("manifest")]
    [InlineData("sig")]
    [InlineData("channel")]
    public async Task ModifiedLocalFileIsRejected(string kind)
    {
        using BundleFixture fixture = new();
        string root = fixture.Write("0.6.2");
        string file = kind switch
        {
            "exe" => "launcher/releases/0.6.2/NovaLink.exe",
            "manifest" => "launcher/releases/0.6.2/manifest.json",
            "sig" => "launcher/releases/0.6.2/NovaLink.exe.sig",
            _ => LauncherPackage.ChannelKey
        };
        byte[] data = File.ReadAllBytes(Path.Combine(root, file));
        data[^2] ^= 1;
        File.WriteAllBytes(Path.Combine(root, file), data);
        await Assert.ThrowsAnyAsync<Exception>(() => LauncherPackage.LoadAsync(root, fixture.PublicKey, default));
    }

    [Fact]
    public void OversizedAndDuplicateMetadataAreRejected()
    {
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.ParseJson(new byte[LauncherPackage.MetadataLimit + 1]));
        Assert.Throws<PatchPublisherException>(() => LauncherPackage.ParseJson("{\"schemaVersion\":1,\"schemaVersion\":2}"u8.ToArray()));
    }

    [Fact]
    public async Task UploadReadbackRetryAndPublishStayInsideLauncherPrefix()
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load("0.6.2");
        FakeStore client = new();
        await using R2ObjectStore store = new(client, "test-only");
        List<int> progress = [];
        LauncherReleaseService service = new(store, (p, _) => progress.Add(p));
        await service.UploadAsync(bundle);
        Assert.Equal(4, client.Puts.Count);
        Assert.False(client.Objects.ContainsKey(LauncherPackage.ChannelKey));
        await service.UploadAsync(bundle);
        Assert.Equal(4, client.Puts.Count);
        int reads = client.Reads.Count;
        await service.PublishAsync(bundle);
        Assert.Equal(bundle.ChannelBytes, client.Objects[LauncherPackage.ChannelKey]);
        Assert.All(bundle.Files.Keys, key => Assert.Contains(key, client.Reads.Skip(reads)));
        Assert.Equal(LauncherPackage.ChannelKey, client.Puts[^1]);
        Assert.Contains("no-store", client.Cache[LauncherPackage.ChannelKey]);
        Assert.All(client.Puts, key => Assert.StartsWith("launcher/", key));
        Assert.Contains(100, progress);
        int puts = client.Puts.Count;
        await service.PublishAsync(bundle);
        Assert.Equal(puts, client.Puts.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("oversized")]
    [InlineData("cancel")]
    public async Task PublishRechecksAllObjectsAndNeverSwitchesOnFailure(string failure)
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load("0.6.2");
        FakeStore client = new();
        await using R2ObjectStore store = new(client, "test-only");
        LauncherReleaseService service = new(store);
        await service.UploadAsync(bundle);
        string key = bundle.Manifest.Key;
        using CancellationTokenSource cancel = new();
        if (failure == "missing") client.Objects.Remove(key);
        if (failure == "corrupt") client.Objects[key][0] ^= 1;
        if (failure == "oversized") client.Objects[key] = [.. client.Objects[key], 1];
        if (failure == "cancel") client.OnGet = k => { if (k == key) cancel.Cancel(); };
        await Assert.ThrowsAnyAsync<Exception>(() => service.PublishAsync(bundle, cancel.Token));
        Assert.False(client.Objects.ContainsKey(LauncherPackage.ChannelKey));
    }

    [Fact]
    public async Task SameSizeConflictIsNotSkippedOrOverwritten()
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load("0.6.2");
        FakeStore client = new();
        client.Objects[bundle.Manifest.Key] = new byte[bundle.Manifest.Size];
        await using R2ObjectStore store = new(client, "test-only");
        await Assert.ThrowsAsync<PatchPublisherException>(() => new LauncherReleaseService(store).UploadAsync(bundle));
        Assert.Empty(client.Puts);
    }

    [Theory]
    [InlineData("0.6.1")]
    [InlineData("0.6.2")]
    public async Task DowngradeAndSameVersionDifferentManifestAreRejected(string version)
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load(version);
        JsonObject current = fixture.Manifest("0.6.2");
        current["changelog"] = "different";
        byte[] channel = fixture.Envelope(current);
        FakeStore client = new();
        client.Objects[LauncherPackage.ChannelKey] = channel;
        await using R2ObjectStore store = new(client, "test-only");
        await Assert.ThrowsAsync<PatchPublisherException>(() => new LauncherReleaseService(store).PublishAsync(bundle));
        Assert.Empty(client.Puts);
        Assert.Equal(channel, client.Objects[LauncherPackage.ChannelKey]);
    }

    [Fact]
    public async Task ConcurrentPublishCannotOverwriteNewerPointer()
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load("0.6.2");
        FakeStore client = new();
        client.Objects[LauncherPackage.ChannelKey] = fixture.Envelope(fixture.Manifest("0.6.1"));
        await using R2ObjectStore store = new(client, "test-only");
        LauncherReleaseService service = new(store);
        await service.UploadAsync(bundle);
        byte[] newer = fixture.Envelope(fixture.Manifest("0.6.3"));
        client.OnPut = key => { if (key == LauncherPackage.ChannelKey) client.Objects[key] = newer; };
        await Assert.ThrowsAsync<ConditionalWriteException>(() => service.PublishAsync(bundle));
        Assert.Equal(newer, client.Objects[LauncherPackage.ChannelKey]);
    }

    [Fact]
    public async Task CancellationDuringUploadCanResumeWithoutPublishing()
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load("0.6.2");
        FakeStore client = new();
        using CancellationTokenSource cancel = new();
        client.OnPut = _ => { if (client.Puts.Count == 1) cancel.Cancel(); };
        await using R2ObjectStore store = new(client, "test-only");
        LauncherReleaseService service = new(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(bundle, cancel.Token));
        Assert.Single(client.Objects);
        Assert.False(client.Objects.ContainsKey(LauncherPackage.ChannelKey));
        client.OnPut = null;
        await service.UploadAsync(bundle);
        Assert.Equal(4, client.Objects.Count);
        Assert.Equal(4, client.Puts.Count);
    }

    [Fact]
    public async Task SnapshotIsNotAffectedByLaterChangesToSelectedFolder()
    {
        using BundleFixture fixture = new();
        string root = fixture.Write("0.6.2");
        LauncherPackage bundle = await LauncherPackage.LoadAsync(root, fixture.PublicKey, default);
        File.WriteAllText(Path.Combine(root, bundle.Manifest.Key), "changed");
        FakeStore client = new();
        await using R2ObjectStore store = new(client, "test-only");
        await new LauncherReleaseService(store).UploadAsync(bundle);
        Assert.Equal(bundle.Manifest.Sha256, FileUtilities.Sha256Hex(client.Objects[bundle.Manifest.Key]));
        await Assert.ThrowsAsync<PatchPublisherException>(() => LauncherPackage.LoadAsync(root, fixture.PublicKey, default));
    }

    [Fact]
    public async Task BoundedChannelReadAndInvalidRemoteSignatureStopPublishing()
    {
        using BundleFixture fixture = new();
        LauncherPackage bundle = await fixture.Load("0.6.2");
        FakeStore client = new();
        await using R2ObjectStore store = new(client, "test-only");
        LauncherReleaseService service = new(store);
        client.Objects[LauncherPackage.ChannelKey] = new byte[LauncherPackage.MetadataLimit + 1];
        await Assert.ThrowsAsync<PatchPublisherException>(() => service.PublishAsync(bundle));
        client.Objects[LauncherPackage.ChannelKey] = "{}"u8.ToArray();
        await Assert.ThrowsAsync<PatchPublisherException>(() => service.PublishAsync(bundle));
        Assert.Empty(client.Puts);
    }

    private sealed class BundleFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "depot-launcher-test-" + Guid.NewGuid().ToString("N"));
        private readonly Ed25519PrivateKeyParameters _private = new(RandomNumberGenerator.GetBytes(32), 0);
        private readonly byte[] _id = RandomNumberGenerator.GetBytes(8);
        public byte[] Exe { get; } = "MZ test data only"u8.ToArray();
        public string PublicKey => Encode("untrusted comment: test key\n" + Convert.ToBase64String([0x45, 0x64, .. _id, .. _private.GeneratePublicKey().GetEncoded()]) + "\n");
        public JsonObject Manifest(string version) => new()
        {
            ["schemaVersion"] = 1, ["product"] = "NovaLink", ["channel"] = "stable", ["target"] = "windows-x86_64",
            ["version"] = version, ["changelog"] = "test", ["publishedAt"] = "2026-10-03T00:00:00Z",
            ["key"] = $"launcher/releases/{version}/NovaLink.exe", ["signatureKey"] = $"launcher/releases/{version}/NovaLink.exe.sig",
            ["size"] = Exe.Length, ["sha256"] = FileUtilities.Sha256Hex(Exe)
        };
        public byte[] Envelope(JsonObject manifest)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, algorithm = "minisign", payloadBase64 = Convert.ToBase64String(bytes), signature = Sign(bytes) });
        }
        public string Write(string version)
        {
            string root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            string release = Path.Combine(root, "launcher", "releases", version);
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(Path.Combine(root, "launcher", "channels"));
            JsonObject manifest = Manifest(version);
            byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            File.WriteAllBytes(Path.Combine(root, LauncherPackage.ChannelKey), Envelope(manifest));
            File.WriteAllBytes(Path.Combine(release, "manifest.json"), bytes);
            File.WriteAllText(Path.Combine(release, "manifest.json.sig"), Sign(bytes));
            File.WriteAllBytes(Path.Combine(release, "NovaLink.exe"), Exe);
            File.WriteAllText(Path.Combine(release, "NovaLink.exe.sig"), Sign(Exe));
            return root;
        }
        public Task<LauncherPackage> Load(string version) => LauncherPackage.LoadAsync(Write(version), PublicKey, default);
        private string Sign(byte[] bytes)
        {
            Blake2bDigest digest = new(512);
            digest.BlockUpdate(bytes, 0, bytes.Length);
            byte[] hash = new byte[64]; digest.DoFinal(hash, 0);
            byte[] SignRaw(byte[] message)
            {
                Ed25519Signer signer = new(); signer.Init(true, _private);
                signer.BlockUpdate(message, 0, message.Length); return signer.GenerateSignature();
            }
            byte[] sig = SignRaw(hash);
            byte[] global = SignRaw([.. sig, .. Encoding.UTF8.GetBytes("test")]);
            return Encode("untrusted comment: test\n" + Convert.ToBase64String([0x45, 0x44, .. _id, .. sig]) + "\ntrusted comment: test\n" + Convert.ToBase64String(global) + "\n");
        }
        private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class FakeStore() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1", ForcePathStyle = true, AuthenticationRegion = "auto" })
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public List<string> Puts { get; } = [];
        public List<string> Reads { get; } = [];
        public Dictionary<string, string> Cache { get; } = new();
        public Action<string>? OnGet { get; set; }
        public Action<string>? OnPut { get; set; }
        private byte[] Get(string key) => Objects.TryGetValue(key, out byte[]? value) ? value : throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); byte[] bytes = Get(request.Key);
            return Task.FromResult(new GetObjectMetadataResponse { ContentLength = bytes.Length, ETag = FileUtilities.Sha256Hex(bytes) });
        }
        public override Task<GetObjectResponse> GetObjectAsync(string bucket, string key, CancellationToken token = default)
        {
            OnGet?.Invoke(key); token.ThrowIfCancellationRequested(); Reads.Add(key); byte[] bytes = Get(key);
            return Task.FromResult(new GetObjectResponse { ContentLength = bytes.Length, ResponseStream = new MemoryStream(bytes, false) });
        }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken token = default)
        {
            OnPut?.Invoke(request.Key); token.ThrowIfCancellationRequested();
            bool exists = Objects.TryGetValue(request.Key, out byte[]? old);
            if (request.Headers["If-None-Match"] == "*" && exists || request.Headers["If-Match"] is { Length: > 0 } etag && (!exists || etag.Trim('"') != FileUtilities.Sha256Hex(old!)))
                throw new AmazonS3Exception("conflict") { StatusCode = HttpStatusCode.PreconditionFailed };
            using MemoryStream buffer = new(); await request.InputStream.CopyToAsync(buffer, token);
            byte[] bytes = buffer.ToArray(); Objects[request.Key] = bytes; Puts.Add(request.Key); Cache[request.Key] = request.Headers["Cache-Control"];
            return new PutObjectResponse { ETag = FileUtilities.Sha256Hex(bytes) };
        }
    }
}
