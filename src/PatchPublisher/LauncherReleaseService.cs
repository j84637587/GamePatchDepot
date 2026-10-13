using System.Security.Cryptography;

namespace GamePatchDepot;

// Launcher objects live outside the game's catalog and GC prefixes. No game signing key is used.
public sealed class LauncherReleaseService(R2ObjectStore store, Action<int, string>? progress = null)
{
    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private const string NoCache = "no-store, no-cache, max-age=0";

    public async Task UploadAsync(LauncherPackage package, CancellationToken token = default)
    {
        int index = 0;
        foreach ((string key, byte[] bytes) in package.Files)
        {
            token.ThrowIfCancellationRequested();
            int current = index++;
            progress?.Invoke(current * 100 / 4, $"上傳：{key}");
            ObjectMetadata? existing = await store.HeadAsync(key, token);
            if (existing is null)
            {
                try
                {
                    await store.PutBytesConditionalAsync(key, bytes, key.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream",
                        ImmutableCache, null, true, token,
                        (sent, total) => progress?.Invoke((int)((current + (total > 0 ? Math.Clamp((double)sent / total, 0, 1) : 0)) * 25), $"上傳：{Path.GetFileName(key)}（{sent / 1048576d:F2} MiB）"));
                }
                catch (ConditionalWriteException) { /* A racing upload must still match byte-for-byte by hash. */ }
            }
            else
                progress?.Invoke(current * 25, $"已有檔案，核對後略過：{key}");
            await VerifyFileAsync(key, bytes, token,
                read => progress?.Invoke((int)((current + (double)read / bytes.Length) * 25), $"R2 回驗：{Path.GetFileName(key)}（{read / 1048576d:F2} MiB）"));
            progress?.Invoke(index * 25, $"已上傳並回驗：{Path.GetFileName(key)}");
        }
        // Upload never changes the channel, even on retry or partial success.
    }

    public async Task VerifyRemoteAsync(LauncherPackage package, CancellationToken token = default)
    {
        int index = 0;
        foreach ((string key, byte[] bytes) in package.Files)
        {
            int current = index++;
            await VerifyFileAsync(key, bytes, token,
                read => progress?.Invoke((int)((current + (double)read / bytes.Length) * 25), $"R2 回驗：{Path.GetFileName(key)}（{read / 1048576d:F2} MiB）"));
            progress?.Invoke(index * 25, $"R2 回驗通過：{Path.GetFileName(key)}");
        }
    }

    public async Task<string?> StatusAsync(CancellationToken token = default)
    {
        byte[]? bytes = await store.GetBoundedBytesOrNullAsync(LauncherPackage.ChannelKey, LauncherPackage.MetadataLimit, token);
        return bytes is null ? null : LauncherPackage.ParseChannel(bytes, LauncherPackage.PublicKey).Version;
    }

    public async Task PublishAsync(LauncherPackage package, CancellationToken token = default)
    {
        // Capture the ETag BEFORE reading the signed current channel. A race can only fail the CAS.
        ObjectMetadata? previous = await store.HeadAsync(LauncherPackage.ChannelKey, token);
        byte[]? channel = await store.GetBoundedBytesOrNullAsync(LauncherPackage.ChannelKey, LauncherPackage.MetadataLimit, token);
        if ((previous is null) != (channel is null) || previous is not null && string.IsNullOrWhiteSpace(previous.ETag))
            throw new PatchPublisherException("啟動器頻道在讀取時變更，請重新整理後再試。");
        bool alreadyPublished = false;
        if (channel is not null)
        {
            LauncherManifest old = package.ReadChannel(channel);
            int comparison = LauncherPackage.CompareVersions(package.Manifest.Version, old.Version);
            if (comparison < 0) throw new PatchPublisherException("不可發布較舊的啟動器版本。");
            alreadyPublished = channel.AsSpan().SequenceEqual(package.ChannelBytes);
            if (comparison == 0 && !alreadyPublished) throw new PatchPublisherException("同版號不可改成不同的啟動器清單，請提高版本。");
        }

        // Never trust a UI flag or old verification receipt. Re-download all four immutable objects.
        await VerifyRemoteAsync(package, token);
        token.ThrowIfCancellationRequested();
        if (!alreadyPublished)
        {
            progress?.Invoke(99, "最後切換啟動器 Stable；若中斷，請重新整理確認結果。");
            await store.PutBytesConditionalAsync(LauncherPackage.ChannelKey, package.ChannelBytes, "application/json", NoCache,
                previous?.ETag, previous is null, token);
        }
        byte[]? actual = await store.GetBoundedBytesOrNullAsync(LauncherPackage.ChannelKey, LauncherPackage.MetadataLimit, token);
        if (actual is null || !actual.AsSpan().SequenceEqual(package.ChannelBytes))
            throw new PatchPublisherException("發布後的頻道狀態與預期不同，請重新整理；不要直接重複覆寫。");
        progress?.Invoke(100, $"啟動器 Stable 已確認：{package.Manifest.Version}");
    }

    private async Task VerifyFileAsync(string key, byte[] expected, CancellationToken token, Action<long>? onRead = null)
    {
        using HashSink sink = new(expected.Length, onRead);
        await store.DownloadToStreamAsync(key, sink, token);
        if (sink.Count != expected.LongLength || !sink.Finish().AsSpan().SequenceEqual(SHA256.HashData(expected)))
            throw new PatchPublisherException($"R2 檔案內容與本機套件不同：{key}。不覆寫、不發布。");
    }

    private sealed class HashSink(long limit, Action<long>? onRead) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Count { get; private set; }
        public byte[] Finish() => _hash.GetHashAndReset();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Count + buffer.Length > limit) throw new PatchPublisherException("R2 回傳超出預期大小，停止下載。");
            _hash.AppendData(buffer); Count += buffer.Length; onRead?.Invoke(Count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
