using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GamePatchDepot;

public sealed class ReleaseCatalog(ProjectContext context, SigningService signer)
{
    public async Task EnsureContentVersionAvailableAsync(
        string releaseId,
        long contentVersion,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(context.CancelledCatalogRoot))
        {
            foreach (string cancelledPath in Directory.EnumerateFiles(context.CancelledCatalogRoot, "*.json"))
            {
                await using FileStream stream = File.OpenRead(cancelledPath);
                CancelledRelease cancelled = await JsonSerializer.DeserializeAsync(
                    stream,
                    PatchJsonContext.Default.CancelledRelease,
                    cancellationToken) ?? throw new PatchPublisherException($"取消版本紀錄格式錯誤：{cancelledPath}");
                if (string.Equals(cancelled.ReleaseId, releaseId, StringComparison.Ordinal))
                {
                    throw new PatchPublisherException($"Release ID {releaseId} 已取消，不可重複使用。");
                }
                if (cancelled.ContentVersion >= contentVersion)
                {
                    throw new PatchPublisherException(
                        $"contentVersion 必須高於已取消版本 {cancelled.ContentVersion}（{cancelled.ReleaseId}）。");
                }
            }
        }

        if (!Directory.Exists(context.ReleaseCatalogRoot))
        {
            return;
        }

        foreach (string manifestPath in Directory.EnumerateFiles(context.ReleaseCatalogRoot, "*.json"))
        {
            string existingReleaseId = Path.GetFileNameWithoutExtension(manifestPath);
            (ReleaseManifest existing, _, _) = await LoadVerifiedAsync(existingReleaseId, cancellationToken);
            if (string.Equals(existingReleaseId, releaseId, StringComparison.Ordinal))
            {
                if (existing.ContentVersion != contentVersion)
                {
                    throw new PatchPublisherException(
                        $"Release ID {releaseId} 已使用不同的 contentVersion：{existing.ContentVersion}");
                }
                continue;
            }

            if (existing.ContentVersion >= contentVersion)
            {
                throw new PatchPublisherException(
                    $"contentVersion 必須高於本機 Catalog 既有版本 {existing.ContentVersion}（{existing.ReleaseId}）。");
            }
        }
    }

    public async Task<(ReleaseManifest Manifest, byte[] Bytes, string Signature)> LoadVerifiedAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        string manifestPath = context.ManifestPath(releaseId);
        string signaturePath = context.ManifestSignaturePath(releaseId);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            throw new PatchPublisherException($"找不到版本 Manifest 或簽章：{releaseId}");
        }

        byte[] bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        string signature = (await File.ReadAllTextAsync(signaturePath, cancellationToken)).Trim();
        signer.Verify(bytes, signature);
        ReleaseManifest manifest = JsonSerializer.Deserialize(bytes, PatchJsonContext.Default.ReleaseManifest)
            ?? throw new PatchPublisherException($"Manifest 格式錯誤：{releaseId}");
        ValidateManifest(manifest, releaseId);
        return (manifest, bytes, signature);
    }

    public async Task<ChannelPayload?> LoadLocalChannelAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(context.ChannelCatalogRoot, $"{channel}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] envelope = await File.ReadAllBytesAsync(path, cancellationToken);
        return SignedEnvelopeService.Open(envelope, PatchJsonContext.Default.ChannelPayload, signer);
    }

    public void ValidateManifest(ReleaseManifest manifest, string expectedReleaseId)
    {
        if (manifest.SchemaVersion is not (1 or 2) || !string.Equals(manifest.ReleaseId, expectedReleaseId, StringComparison.Ordinal))
        {
            throw new PatchPublisherException("Manifest Schema 或 Release ID 不正確。");
        }
        if (!string.Equals(manifest.Product, context.Config.Product, StringComparison.Ordinal) ||
            !string.Equals(manifest.GameVersion, context.Config.GameVersion, StringComparison.Ordinal))
        {
            throw new PatchPublisherException("Manifest 產品或遊戲版本不符合目前設定。");
        }

        PathPolicy.ValidateFileSet(manifest.Files.Select(file => file.Path));
        if (manifest.SchemaVersion == 2 && !manifest.Files.Any(file => file.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new PatchPublisherException("完整客戶端 Manifest 缺少 EXE。");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in manifest.Files)
        {
            string normalized = PathPolicy.NormalizeInstallPath(file.Path);
            if (!paths.Add(normalized))
            {
                throw new PatchPublisherException($"Manifest 含有重複檔案：{file.Path}");
            }
            if (manifest.SchemaVersion == 1 && !context.AllowedFiles.Contains(normalized))
            {
                throw new PatchPublisherException($"Manifest 含有不允許的檔案：{file.Path}");
            }
            ValidateFileChunks(file);
            if (file.Patch is FileDeltaPatch patch &&
                (patch.Format != "hdiffpatch-sf20-zstd" || !IsHash(patch.BaseSha256) || !IsHash(patch.Sha256) ||
                 patch.BaseSize <= 0 || patch.Size <= 0 || patch.Size >= file.Size || patch.BaseSha256 == file.Sha256 ||
                 patch.Key != ObjectKeys.ForPatch(patch.Sha256)))
                throw new PatchPublisherException($"Manifest 差異補丁資料錯誤：{file.Path}");
        }

        string[] missing = context.AllowedFiles.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray();
        if (manifest.SchemaVersion == 1 && missing.Length > 0)
        {
            throw new PatchPublisherException($"Manifest 缺少正式檔案：{string.Join(", ", missing)}");
        }

        foreach (string delete in manifest.Delete)
        {
            PathPolicy.ValidateInstallPath(delete);
            if (paths.Contains(delete.Replace('\\', '/')))
            {
                throw new PatchPublisherException($"Manifest 同時要求安裝與刪除同一檔案：{delete}");
            }
        }
        foreach (string preserved in manifest.Preserve ?? [])
        {
            PathPolicy.ValidateInstallPath(preserved);
            if (manifest.Files.Select(file => file.Path).Concat(manifest.Delete).Any(path =>
                string.Equals(path, preserved, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(preserved + "/", StringComparison.OrdinalIgnoreCase)))
                throw new PatchPublisherException($"保留路徑不可同時安裝或刪除：{preserved}");
        }
    }

    private static void ValidateFileChunks(ManifestFile file)
    {
        if (file.Size < 0 || !IsHash(file.Sha256) || (file.Size > 0 && file.Chunks.Count == 0) ||
            (file.Size == 0 && (file.Chunks.Count != 0 || file.Sha256 != FileUtilities.Sha256Hex([]))))
        {
            throw new PatchPublisherException($"Manifest 檔案資料不完整：{file.Path}");
        }

        long expectedOffset = 0;
        foreach (ManifestChunk chunk in file.Chunks)
        {
            if (chunk.Offset != expectedOffset || chunk.Size <= 0 || !IsHash(chunk.Sha256) ||
                !string.Equals(chunk.Key, ObjectKeys.ForChunk(chunk.Sha256), StringComparison.Ordinal))
            {
                throw new PatchPublisherException($"Manifest chunk 不連續或格式錯誤：{file.Path}");
            }
            expectedOffset += chunk.Size;
        }

        if (expectedOffset != file.Size)
        {
            throw new PatchPublisherException($"Manifest chunk 總大小與檔案大小不符：{file.Path}");
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class LocalReleaseVerifier(ProjectContext context, ReleaseCatalog catalog)
{
    private const int BufferSize = 1024 * 1024;

    public async Task VerifyAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        (ReleaseManifest manifest, _, _) = await catalog.LoadVerifiedAsync(releaseId, cancellationToken);
        await VerifyFilesAsync(manifest.Files, cancellationToken);
    }

    public async Task<(int Verified, int Reused)> VerifyChangesAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        (ReleaseManifest manifest, _, _) = await catalog.LoadVerifiedAsync(releaseId, cancellationToken);
        string infoPath = Path.Combine(context.CandidatePath(releaseId), "release-info.json");
        if (!File.Exists(infoPath))
        {
            await VerifyFilesAsync(manifest.Files, cancellationToken);
            return (manifest.Files.Count, 0);
        }

        await using FileStream infoStream = File.OpenRead(infoPath);
        ReleaseInfo info = await JsonSerializer.DeserializeAsync(infoStream, PatchJsonContext.Default.ReleaseInfo, cancellationToken)
            ?? throw new PatchPublisherException("Candidate 版本資料無法辨識。");
        if (info.ReleaseId != releaseId || info.SchemaVersion != manifest.SchemaVersion)
            throw new PatchPublisherException("Candidate 版本資料與 Manifest 不一致。");
        if (string.IsNullOrWhiteSpace(info.BaseReleaseId))
        {
            await VerifyFilesAsync(manifest.Files, cancellationToken);
            return (manifest.Files.Count, 0);
        }

        (ReleaseManifest previous, _, _) = await catalog.LoadVerifiedAsync(info.BaseReleaseId, cancellationToken);
        if (previous.ContentVersion >= manifest.ContentVersion)
            throw new PatchPublisherException("比較基準不是較早的內容版本。");
        Dictionary<string, ManifestFile> oldFiles = previous.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        List<ManifestFile> changed = manifest.Files.Where(file =>
            !oldFiles.TryGetValue(file.Path, out ManifestFile? old) ||
            old.Size != file.Size || old.Sha256 != file.Sha256 ||
            !old.Chunks.Select(chunk => (chunk.Offset, chunk.Size, chunk.Sha256, chunk.Key))
                .SequenceEqual(file.Chunks.Select(chunk => (chunk.Offset, chunk.Size, chunk.Sha256, chunk.Key))) ||
            file.Patch is not null).ToList();
        await VerifyFilesAsync(changed, cancellationToken);
        return (changed.Count, manifest.Files.Count - changed.Count);
    }

    private async Task VerifyFilesAsync(IEnumerable<ManifestFile> files, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (ManifestFile file in files)
            {
                if (file.Patch is not null)
                    await DeltaPatchBuilder.VerifyPatchAsync(context, file.Patch, cancellationToken);
                using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                foreach (ManifestChunk chunk in file.Chunks)
                {
                    string chunkPath = ObjectKeys.LocalChunkPath(context.ChunkRoot, chunk.Sha256);
                    if (!File.Exists(chunkPath))
                    {
                        throw new PatchPublisherException($"本機缺少 chunk：{chunk.Sha256}");
                    }
                    FileInfo info = new(chunkPath);
                    if (info.Length != chunk.Size)
                    {
                        throw new PatchPublisherException($"本機 chunk 大小錯誤：{chunk.Sha256}");
                    }

                    using IncrementalHash chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using FileStream stream = new(
                        chunkPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        chunkHash.AppendData(buffer, 0, read);
                        fileHash.AppendData(buffer, 0, read);
                        total += read;
                    }
                    string actualChunkHash = Convert.ToHexStringLower(chunkHash.GetHashAndReset());
                    if (!string.Equals(actualChunkHash, chunk.Sha256, StringComparison.Ordinal))
                    {
                        throw new PatchPublisherException($"本機 chunk SHA-256 錯誤：{chunk.Sha256}");
                    }
                }

                string actualFileHash = Convert.ToHexStringLower(fileHash.GetHashAndReset());
                if (total != file.Size || !string.Equals(actualFileHash, file.Sha256, StringComparison.Ordinal))
                {
                    throw new PatchPublisherException($"重組檔案驗證失敗：{file.Path}");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed class ManifestDiffService(ProjectContext context, ReleaseCatalog catalog)
{
    public async Task<DiffReport> CreateAsync(
        string releaseId,
        string against,
        CancellationToken cancellationToken = default)
    {
        (ReleaseManifest current, _, _) = await catalog.LoadVerifiedAsync(releaseId, cancellationToken);
        string? againstReleaseId = against;
        if (string.Equals(against, "none", StringComparison.OrdinalIgnoreCase))
        {
            againstReleaseId = null;
        }
        else if (string.Equals(against, "stable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(against, "test", StringComparison.OrdinalIgnoreCase))
        {
            ChannelPayload? channel = await catalog.LoadLocalChannelAsync(against.ToLowerInvariant(), cancellationToken);
            againstReleaseId = channel?.ReleaseId;
        }

        ReleaseManifest? previous = null;
        if (!string.IsNullOrWhiteSpace(againstReleaseId))
        {
            (previous, _, _) = await catalog.LoadVerifiedAsync(againstReleaseId, cancellationToken);
        }

        Dictionary<string, ManifestFile> currentFiles = current.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestFile> previousFiles = previous?.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        List<FileDifference> added = [];
        List<FileDifference> changed = [];
        List<FileDifference> removed = [];
        List<string> unchanged = [];

        foreach ((string path, ManifestFile file) in currentFiles.OrderBy(pair => pair.Key))
        {
            if (!previousFiles.TryGetValue(path, out ManifestFile? old))
            {
                added.Add(ToDifference(path, null, file));
            }
            else if (!string.Equals(old.Sha256, file.Sha256, StringComparison.Ordinal))
            {
                changed.Add(ToDifference(path, old, file));
            }
            else
            {
                unchanged.Add(path);
            }
        }

        foreach ((string path, ManifestFile file) in previousFiles.OrderBy(pair => pair.Key))
        {
            if (!currentFiles.ContainsKey(path))
            {
                removed.Add(ToDifference(path, file, null));
            }
        }

        HashSet<string> previousChunks = previousFiles.Values
            .SelectMany(file => file.Chunks)
            .Select(chunk => chunk.Sha256)
            .ToHashSet(StringComparer.Ordinal);
        long newUniqueChunkBytes = current.Files
            .SelectMany(file => file.Chunks)
            .Where(chunk => !previousChunks.Contains(chunk.Sha256))
            .GroupBy(chunk => chunk.Sha256, StringComparer.Ordinal)
            .Sum(group => (long)group.First().Size);

        DiffReport report = new()
        {
            ReleaseId = releaseId,
            AgainstReleaseId = againstReleaseId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Added = added,
            Changed = changed,
            Removed = removed,
            Unchanged = unchanged,
            EstimatedDownloadBytes = added.Sum(file => file.NewSize ?? 0) + changed.Sum(file => file.NewSize ?? 0),
            NewUniqueChunkBytes = newUniqueChunkBytes
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, PatchJsonContext.Default.DiffReport);
        string reportPath = context.DiffReportPath(releaseId, against);
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }
        await FileUtilities.WriteAtomicAsync(reportPath, bytes, cancellationToken);
        return report;
    }

    private static FileDifference ToDifference(string path, ManifestFile? old, ManifestFile? current) => new()
    {
        Path = path,
        OldSize = old?.Size,
        NewSize = current?.Size,
        OldSha256 = old?.Sha256,
        NewSha256 = current?.Sha256
    };
}
