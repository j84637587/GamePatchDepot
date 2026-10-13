using System.Text.Json;

namespace GamePatchDepot;

public sealed class GarbageCollector(
    ProjectContext context,
    SigningService signer,
    R2ObjectStore store,
    RemoteReleaseService remote)
{
    public async Task<(GcReport Report, string Path)> DryRunAsync(CancellationToken cancellationToken = default)
    {
        GcSnapshot snapshot = await CreateSnapshotAsync(cancellationToken);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-context.Config.OrphanGraceDays);
        List<ObjectMetadata> allObjects = await store.ListAsync("objects/sha256/", cancellationToken);
        allObjects.AddRange(await store.ListAsync("objects/patches/sha256/", cancellationToken));
        List<GcCandidate> candidates = allObjects
            .Where(item => !snapshot.ReferencedObjectKeys.Contains(item.Key) && item.LastModified < cutoff)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new GcCandidate
            {
                Key = item.Key,
                Size = item.Size,
                LastModified = item.LastModified
            })
            .ToList();

        string reportId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        GcReport report = new()
        {
            ReportId = reportId,
            GeneratedAt = DateTimeOffset.UtcNow,
            ChannelGenerations = snapshot.ChannelGenerations,
            RootReleaseIds = snapshot.ReleaseIds.Order(StringComparer.Ordinal).ToList(),
            ReferencedObjectCount = snapshot.ReferencedObjectKeys.Count,
            Candidates = candidates
        };
        string path = ReportPath(reportId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, PatchJsonContext.Default.GcReport);
        await FileUtilities.WriteAtomicAsync(path, bytes, cancellationToken);
        return (report, path);
    }

    public async Task<GcReport> ApplyAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        string absolutePath = Path.GetFullPath(reportPath);
        if (!File.Exists(absolutePath))
        {
            throw new PatchPublisherException($"找不到 GC 報告：{absolutePath}");
        }
        GcReport report;
        await using (FileStream reportStream = File.OpenRead(absolutePath))
        {
            report = await JsonSerializer.DeserializeAsync(
                reportStream,
                PatchJsonContext.Default.GcReport,
                cancellationToken) ?? throw new PatchPublisherException("GC 報告格式錯誤。");
        }
        if (report.AppliedAt is not null)
        {
            throw new PatchPublisherException("此 GC 報告已經套用過。");
        }

        await using R2OperationLease lease = await R2OperationLease.AcquireAsync(
            store,
            $"gc:{report.ReportId}",
            TimeSpan.FromMinutes(context.Config.OperationLeaseMinutes),
            cancellationToken);

        GcSnapshot current = await CreateSnapshotAsync(cancellationToken);
        if (!DictionaryEqual(report.ChannelGenerations, current.ChannelGenerations))
        {
            throw new PatchPublisherException("Channel generation 已改變，請重新執行 GC Dry Run。");
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-context.Config.OrphanGraceDays);
        HashSet<string> reportCandidates = report.Candidates.Select(candidate => candidate.Key).ToHashSet(StringComparer.Ordinal);
        List<ObjectMetadata> currentObjects = await store.ListAsync("objects/sha256/", cancellationToken);
        currentObjects.AddRange(await store.ListAsync("objects/patches/sha256/", cancellationToken));
        List<ObjectMetadata> deletable = currentObjects
            .Where(item => reportCandidates.Contains(item.Key))
            .Where(item => !current.ReferencedObjectKeys.Contains(item.Key))
            .Where(item => item.LastModified < cutoff)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

        int deleted = 0;
        long deletedBytes = 0;
        foreach (ObjectMetadata[] batch in deletable.Chunk(1000))
        {
            await lease.RenewIfNeededAsync(cancellationToken);
            await lease.EnsureHeldAsync(cancellationToken);
            await store.DeleteManyAsync(batch.Select(item => item.Key).ToArray(), cancellationToken);
            deleted += batch.Length;
            deletedBytes += batch.Sum(item => item.Size);
        }

        report.AppliedAt = DateTimeOffset.UtcNow;
        report.DeletedObjectCount = deleted;
        report.DeletedBytes = deletedBytes;
        byte[] updated = JsonSerializer.SerializeToUtf8Bytes(report, PatchJsonContext.Default.GcReport);
        await FileUtilities.WriteReplaceAtomicAsync(absolutePath, updated, cancellationToken);
        return report;
    }

    public static HashSet<string> SelectDeletableKeys(
        IEnumerable<ObjectMetadata> objects,
        IReadOnlySet<string> referenced,
        DateTimeOffset cutoff) =>
        objects
            .Where(item => !referenced.Contains(item.Key) && item.LastModified < cutoff)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);

    private async Task<GcSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, long> generations = new(StringComparer.Ordinal);
        HashSet<string> releaseIds = new(StringComparer.Ordinal);

        ChannelPayload? stable = await remote.LoadRemoteChannelAsync("stable", cancellationToken);
        ChannelPayload? test = await remote.LoadRemoteChannelAsync("test", cancellationToken);
        generations["stable"] = stable?.Generation ?? 0;
        generations["test"] = test?.Generation ?? 0;
        if (stable is not null)
        {
            releaseIds.Add(stable.ReleaseId);
        }
        if (test is not null)
        {
            releaseIds.Add(test.ReleaseId);
        }

        List<ObjectMetadata> history = await store.ListAsync("channel-history/stable/", cancellationToken);
        foreach (ObjectMetadata item in history
                     .OrderByDescending(item => item.Key, StringComparer.Ordinal)
                     .Take(context.Config.KeepStableReleases))
        {
            byte[] envelope = await store.GetBytesOrNullAsync(item.Key, cancellationToken)
                ?? throw new PatchPublisherException($"GC 讀取歷史 Channel 失敗：{item.Key}");
            ChannelPayload payload = SignedEnvelopeService.Open(
                envelope,
                PatchJsonContext.Default.ChannelPayload,
                signer);
            if (!string.Equals(payload.Channel, "stable", StringComparison.Ordinal))
            {
                throw new PatchPublisherException($"Stable history 內容錯誤：{item.Key}");
            }
            releaseIds.Add(payload.ReleaseId);
        }

        HashSet<string> referenced = new(StringComparer.Ordinal);
        foreach (string releaseId in releaseIds)
        {
            (ReleaseManifest manifest, _, _) = await remote.LoadRemoteManifestAsync(releaseId, cancellationToken);
            foreach (var chunk in ObjectKeys.Payloads(manifest))
            {
                referenced.Add(chunk.Key);
            }
        }

        return new GcSnapshot(generations, releaseIds, referenced);
    }

    private string ReportPath(string reportId) =>
        Path.Combine(context.ReportRoot, "garbage-collection", $"{reportId}.json");

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out long value) && value == pair.Value);

    private sealed record GcSnapshot(
        Dictionary<string, long> ChannelGenerations,
        HashSet<string> ReleaseIds,
        HashSet<string> ReferencedObjectKeys);
}
