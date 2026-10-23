using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GamePatchDepot;

public sealed class RemoteReleaseService(
    ProjectContext context,
    SigningService signer,
    ReleaseCatalog catalog,
    R2ObjectStore store)
{
    private static readonly TimeSpan ImmutableCache = TimeSpan.FromDays(365);

    public async Task UploadAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        (ReleaseManifest manifest, byte[] manifestBytes, string signature) =
            await catalog.LoadVerifiedAsync(releaseId, cancellationToken);

        await using R2OperationLease lease = await R2OperationLease.AcquireAsync(
            store,
            $"upload:{releaseId}",
            TimeSpan.FromMinutes(context.Config.OperationLeaseMinutes),
            cancellationToken);

        List<(string Key, string Sha256, long Size)> chunks = ObjectKeys.Payloads(manifest).ToList();

        using CancellationTokenSource uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource renewalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewal = RenewLeaseAsync();
        try
        {
            int processed = 0;
            int uploaded = 0;
            int skipped = 0;
            object progressGate = new();
            Console.WriteLine($"並行上傳：最多 {context.Config.UploadConcurrency} 個物件。");
            Console.WriteLine($"[0/{chunks.Count}] 開始處理分塊與補丁。");
            await Parallel.ForEachAsync(chunks, new ParallelOptions
            {
                MaxDegreeOfParallelism = context.Config.UploadConcurrency,
                CancellationToken = uploadCancellation.Token
            }, async (chunk, token) =>
            {
                ObjectMetadata? existing = await store.HeadAsync(chunk.Key, token);
                if (existing is not null)
                {
                    if (existing.Size != chunk.Size)
                        throw new PatchPublisherException($"R2 已存在相同 Key 但大小不同：{chunk.Key}");
                    ReportCompleted(wasSkipped: true, chunk.Sha256);
                    return;
                }

                string localPath = ObjectKeys.LocalPayloadPath(context, chunk.Key, chunk.Sha256);
                if (!File.Exists(localPath))
                    throw new PatchPublisherException($"R2 缺少物件，本機也沒有可上傳的分塊／補丁：{chunk.Sha256}");
                await DeltaPatchBuilder.VerifyFileAsync(localPath, chunk.Size, chunk.Sha256, token);
                token.ThrowIfCancellationRequested();
                Console.WriteLine($"上傳中：{chunk.Key}（{chunk.Size / 1024d / 1024d:F2} MiB）");
                await store.PutFileIfAbsentAsync(
                    chunk.Key, localPath, "application/octet-stream",
                    $"public, max-age={(long)ImmutableCache.TotalSeconds}, immutable", token);
                ReportCompleted(wasSkipped: false, chunk.Sha256);
            });

            Console.WriteLine($"分塊與補丁處理完成：新上傳 {uploaded}，略過 {skipped}，總計 {chunks.Count}。");
            // 所有工作均結束後才寫 Manifest；取消或任一物件失敗時不會走到這裡。
            CancellationToken publishToken = uploadCancellation.Token;
            publishToken.ThrowIfCancellationRequested();
            await lease.EnsureHeldAsync(publishToken);
            await store.PutBytesImmutableAsync(
                ObjectKeys.ForManifest(releaseId), manifestBytes, "application/json; charset=utf-8",
                $"public, max-age={(long)ImmutableCache.TotalSeconds}, immutable", publishToken);
            await lease.EnsureHeldAsync(publishToken);
            await store.PutBytesImmutableAsync(
                ObjectKeys.ForManifestSignature(releaseId), Encoding.UTF8.GetBytes(signature + "\n"), "text/plain; charset=utf-8",
                $"public, max-age={(long)ImmutableCache.TotalSeconds}, immutable", publishToken);

            void ReportCompleted(bool wasSkipped, string hash)
            {
                // 計數與輸出一起鎖定，避免完成順序交錯導致 UI 進度倒退。
                lock (progressGate)
                {
                    if (wasSkipped) skipped++;
                    else uploaded++;
                    processed++;
                    Console.WriteLine($"[{processed}/{chunks.Count}] {(wasSkipped ? "已存在，略過" : "上傳完成")}：{hash[..12]}");
                }
            }
        }
        finally
        {
            await renewalStop.CancelAsync();
            await renewal;
        }

        async Task RenewLeaseAsync()
        {
            try
            {
                // 只有此工作可續租，且單一物件傳輸時間很長時也持續運作。
                using PeriodicTimer timer = new(TimeSpan.FromMinutes(context.Config.OperationLeaseMinutes / 4d));
                while (await timer.WaitForNextTickAsync(renewalStop.Token))
                    await lease.RenewIfNeededAsync(renewalStop.Token);
            }
            catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
            catch
            {
                await uploadCancellation.CancelAsync();
                throw;
            }
        }
    }

    public async Task<(ReleaseManifest Manifest, string ManifestSha256)> VerifyRemoteAsync(
        string releaseId,
        bool deep,
        CancellationToken cancellationToken = default,
        Action<RemoteVerificationProgress>? onProgress = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(releaseId, @"^\d{4}\.\d{2}\.\d{2}\.\d+\z"))
            throw new PatchPublisherException("Release ID 格式必須是 yyyy.MM.dd.流水號。");
        string reportPath = context.VerificationReportPath(releaseId);
        // 重新驗證失敗或取消時，不可沿用上次的通過紀錄。
        if (deep && File.Exists(reportPath)) File.Delete(reportPath);
        (ReleaseManifest manifest, byte[] manifestBytes, _) = await LoadRemoteManifestAsync(releaseId, cancellationToken);
        await EnsureLocalManifestMatchesAsync(releaseId, manifestBytes, cancellationToken);
        string manifestSha256 = FileUtilities.Sha256Hex(manifestBytes);
        // 依實際驗證讀取量計算；同一分塊被多個檔案引用時，會為各檔案雜湊再次讀取。
        long totalBytes = deep ? manifest.Files.Sum(file => checked(file.Size + (file.Patch?.Size ?? 0))) : 0;
        long bytesRead = 0;
        Stopwatch progressInterval = Stopwatch.StartNew();
        Action<RemoteVerificationProgress> emitProgress = onProgress ?? (value => Console.WriteLine(value.ToLogLine()));
        var payloads = ObjectKeys.Payloads(manifest).ToList();
        ReportProgress($"檢查遠端物件 0/{payloads.Count}", force: true);
        int checkedObjects = 0;
        foreach (var chunk in payloads)
        {
            ObjectMetadata? metadata = await store.HeadAsync(chunk.Key, cancellationToken);
            if (metadata is null || metadata.Size != chunk.Size)
            {
                throw new PatchPublisherException($"R2 缺少分塊／補丁或大小錯誤：{chunk.Key}");
            }
            checkedObjects++;
            ReportProgress($"檢查遠端物件 {checkedObjects}/{payloads.Count}", force: checkedObjects == payloads.Count);
        }

        if (!deep)
        {
            return (manifest, manifestSha256);
        }

        foreach (ManifestFile file in manifest.Files)
        {
            ReportProgress("下載並核對", file.Path, force: true);
            if (file.Patch is FileDeltaPatch patch)
            {
                using IncrementalHash patchHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using HashingWriteStream patchSink = new(bytes =>
                {
                    patchHash.AppendData(bytes.Span);
                    bytesRead += bytes.Length;
                    ReportProgress("下載並核對補丁", file.Path);
                });
                await store.DownloadToStreamAsync(patch.Key, patchSink, cancellationToken);
                if (patchSink.BytesWritten != patch.Size || Convert.ToHexStringLower(patchHash.GetHashAndReset()) != patch.Sha256)
                    throw new PatchPublisherException($"R2 差異補丁 SHA-256 或大小不符：{file.Path}");
                ReportProgress("補丁核對完成", file.Path, force: true);
            }
            using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long fileBytes = 0;
            foreach (ManifestChunk chunk in file.Chunks)
            {
                using IncrementalHash chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using HashingWriteStream sink = new(
                    bytes =>
                    {
                        chunkHash.AppendData(bytes.Span);
                        fileHash.AppendData(bytes.Span);
                        fileBytes += bytes.Length;
                        bytesRead += bytes.Length;
                        ReportProgress("下載並核對", file.Path);
                    });
                await store.DownloadToStreamAsync(chunk.Key, sink, cancellationToken);
                string actualChunkHash = Convert.ToHexStringLower(chunkHash.GetHashAndReset());
                if (sink.BytesWritten != chunk.Size || !string.Equals(actualChunkHash, chunk.Sha256, StringComparison.Ordinal))
                {
                    throw new PatchPublisherException($"R2 chunk 深度驗證失敗：{chunk.Key}");
                }
                ReportProgress("分塊核對完成", file.Path, force: true);
            }

            string actualFileHash = Convert.ToHexStringLower(fileHash.GetHashAndReset());
            if (fileBytes != file.Size || !string.Equals(actualFileHash, file.Sha256, StringComparison.Ordinal))
            {
                throw new PatchPublisherException($"R2 重組檔案驗證失敗：{file.Path}");
            }
        }

        // 完整下載可能耗時，通過前再確認本機發布基準沒有被替換。
        ReportProgress("確認清單與寫入驗證紀錄", force: true);
        await EnsureLocalManifestMatchesAsync(releaseId, manifestBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        VerificationReport report = new()
        {
            ReleaseId = releaseId,
            VerifiedAt = DateTimeOffset.UtcNow,
            Deep = true,
            ManifestSha256 = manifestSha256
        };
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, PatchJsonContext.Default.VerificationReport);
        await FileUtilities.WriteReplaceAtomicAsync(reportPath, reportBytes, cancellationToken);
        ReportProgress("全部 SHA-256 核對通過", force: true, complete: true);
        return (manifest, manifestSha256);

        void ReportProgress(string stage, string? filePath = null, bool force = false, bool complete = false)
        {
            if (!deep || (!force && progressInterval.Elapsed < TimeSpan.FromSeconds(1))) return;
            // 下載結束不代表驗證通過，通過最終檢查並寫入紀錄前最多顯示 99.9%。
            int permille = complete ? 1000 : totalBytes == 0 ? 0 : (int)Math.Min(999, bytesRead * 1000d / totalBytes);
            emitProgress(new(bytesRead, totalBytes, permille, stage, filePath));
            progressInterval.Restart();
        }
    }

    private async Task EnsureLocalManifestMatchesAsync(string releaseId, byte[] remoteBytes, CancellationToken cancellationToken)
    {
        (_, byte[] localBytes, _) = await catalog.LoadVerifiedAsync(releaseId, cancellationToken);
        if (!localBytes.AsSpan().SequenceEqual(remoteBytes))
            throw new PatchPublisherException($"R2 Manifest 與本機 Build 的 Manifest 不一致：{releaseId}。請確認發布版本，不可繼續發布。");
    }

    public async Task<ChannelPayload> PromoteAsync(
        string releaseId,
        string channel,
        CancellationToken cancellationToken = default)
    {
        channel = NormalizeChannel(channel);
        await using R2OperationLease lease = await R2OperationLease.AcquireAsync(
            store,
            $"promote:{channel}:{releaseId}",
            TimeSpan.FromMinutes(context.Config.OperationLeaseMinutes),
            cancellationToken);

        (ReleaseManifest manifest, string manifestSha256) = await VerifyRemoteAsync(releaseId, false, cancellationToken);
        if (manifest.LauncherTicketProtocol != 2)
            throw new PatchPublisherException("舊票證協定已移除，此版本不可發布頻道；請先建立支援新交接的版本。");
        if (manifest.SchemaVersion != 2 || !manifest.Files.Any(file => string.Equals(file.Path, context.Config.GameExecutable, StringComparison.OrdinalIgnoreCase)))
            throw new PatchPublisherException("發布版本必須是包含遊戲主程式的完整客戶端版本。");
        LauncherTicketPolicy.ValidateMinimumVersion(manifest.MinimumLauncherVersion);
        string manifestKey = ObjectKeys.ForManifest(releaseId);

        string channelKey = ObjectKeys.ForChannel(channel);
        ObjectMetadata? currentMetadata = await store.HeadAsync(channelKey, cancellationToken);
        byte[]? currentBytes = currentMetadata is null ? null : await store.GetBytesOrNullAsync(channelKey, cancellationToken);
        ChannelPayload? current = currentBytes is null
            ? null
            : SignedEnvelopeService.Open(currentBytes, PatchJsonContext.Default.ChannelPayload, signer);

        if (current is not null && string.Equals(current.ReleaseId, releaseId, StringComparison.Ordinal))
        {
            if (currentBytes is not null)
            {
                await store.PutBytesImmutableAsync(
                    ObjectKeys.ForChannelHistory(channel, current.Generation),
                    currentBytes,
                    "application/json; charset=utf-8",
                    $"public, max-age={(long)ImmutableCache.TotalSeconds}, immutable",
                    cancellationToken);
            }
            return current;
        }
        if (current is not null && manifest.ContentVersion <= current.ContentVersion)
        {
            throw new PatchPublisherException(
                $"新版本 contentVersion 必須高於目前 {channel} 的 {current.ContentVersion}。");
        }

        ChannelPayload payload = new()
        {
            Channel = channel,
            Generation = (current?.Generation ?? 0) + 1,
            ReleaseId = releaseId,
            ContentVersion = manifest.ContentVersion,
            ManifestKey = manifestKey,
            ManifestSha256 = manifestSha256,
            PublishedAt = DateTimeOffset.UtcNow
        };
        byte[] envelope = SignedEnvelopeService.Create(payload, PatchJsonContext.Default.ChannelPayload, signer);

        await lease.EnsureHeldAsync(cancellationToken);
        try
        {
            await store.PutBytesConditionalAsync(
                channelKey,
                envelope,
                "application/json; charset=utf-8",
                "no-store, no-cache, max-age=0",
                currentMetadata?.ETag,
                currentMetadata is null,
                cancellationToken);
        }
        catch (ConditionalWriteException)
        {
            throw new PatchPublisherException("Channel 在 Promote 期間已被其他操作更新，沒有覆寫。");
        }

        // Channel 是唯一的對外切換點，先原子切換，再寫入不可變歷史。
        // 若歷史寫入失敗，重跑相同 Promote 會由上方的冪等分支補齊。
        await store.PutBytesImmutableAsync(
            ObjectKeys.ForChannelHistory(channel, payload.Generation),
            envelope,
            "application/json; charset=utf-8",
            $"public, max-age={(long)ImmutableCache.TotalSeconds}, immutable",
            cancellationToken);

        string localChannelPath = Path.Combine(context.ChannelCatalogRoot, $"{channel}.json");
        await FileUtilities.WriteReplaceAtomicAsync(localChannelPath, envelope, cancellationToken);
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(payload, PatchJsonContext.Default.ChannelPayload);
        string reportPath = Path.Combine(
            context.ReportRoot,
            "publishes",
            $"{channel}-{payload.Generation:D20}-{releaseId}.json");
        await FileUtilities.WriteAtomicAsync(reportPath, reportBytes, cancellationToken);
        return payload;
    }

    public async Task<(ReleaseManifest Manifest, byte[] Bytes, string Signature)> LoadRemoteManifestAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        byte[] manifestBytes = await store.GetBytesOrNullAsync(ObjectKeys.ForManifest(releaseId), cancellationToken)
            ?? throw new PatchPublisherException($"R2 缺少 Manifest：{releaseId}");
        byte[] signatureBytes = await store.GetBytesOrNullAsync(ObjectKeys.ForManifestSignature(releaseId), cancellationToken)
            ?? throw new PatchPublisherException($"R2 缺少 Manifest 簽章：{releaseId}");
        string signature = Encoding.UTF8.GetString(signatureBytes).Trim();
        signer.Verify(manifestBytes, signature);
        ReleaseManifest manifest = JsonSerializer.Deserialize(manifestBytes, PatchJsonContext.Default.ReleaseManifest)
            ?? throw new PatchPublisherException($"R2 Manifest 格式錯誤：{releaseId}");
        if (!string.Equals(manifest.ReleaseId, releaseId, StringComparison.Ordinal))
        {
            throw new PatchPublisherException("R2 Manifest Release ID 不符。");
        }
        catalog.ValidateManifest(manifest, releaseId);
        return (manifest, manifestBytes, signature);
    }

    public async Task<ChannelPayload?> LoadRemoteChannelAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        channel = NormalizeChannel(channel);
        byte[]? bytes = await store.GetBytesOrNullAsync(ObjectKeys.ForChannel(channel), cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        ChannelPayload payload = SignedEnvelopeService.Open(bytes, PatchJsonContext.Default.ChannelPayload, signer);
        if (!string.Equals(payload.Channel, channel, StringComparison.Ordinal) ||
            payload.Generation <= 0 ||
            payload.ContentVersion <= 0 ||
            payload.ManifestSha256.Length != 64 ||
            !string.Equals(payload.ManifestKey, ObjectKeys.ForManifest(payload.ReleaseId), StringComparison.Ordinal))
        {
            throw new PatchPublisherException($"Channel payload 格式或內容錯誤：{channel}");
        }
        return payload;
    }

    private static string NormalizeChannel(string channel)
    {
        string normalized = channel.ToLowerInvariant();
        return normalized is "test" or "stable"
            ? normalized
            : throw new PatchPublisherException("Channel 只允許 test 或 stable。");
    }
}

internal sealed class HashingWriteStream(Action<ReadOnlyMemory<byte>> onWrite) : Stream
{
    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
        onWrite(buffer.AsMemory(offset, count));
        BytesWritten += count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        onWrite(buffer);
        BytesWritten += buffer.Length;
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
