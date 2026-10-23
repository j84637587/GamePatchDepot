using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GamePatchDepot;

public sealed class ReleaseBuilder(ProjectContext context, SigningService signer)
{
    private const int IoBufferSize = 1024 * 1024;

    public async Task<(ReleaseManifest Manifest, BuildReport Report)> BuildAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        context.EnsureDirectories();
        string candidatePath = context.CandidatePath(releaseId);
        if (!Directory.Exists(candidatePath))
        {
            throw new PatchPublisherException($"找不到 Candidate：{candidatePath}");
        }

        ReleaseInfo releaseInfo = await LoadAndValidateReleaseInfoAsync(candidatePath, releaseId, cancellationToken);
        if (releaseInfo.SchemaVersion == 1 && string.IsNullOrWhiteSpace(releaseInfo.BaseReleaseId))
            throw new PatchPublisherException("首次發布必須使用完整客戶端，不能只更新 NX。");
        bool fullClient = releaseInfo.SchemaVersion == 2;
        string payloadRoot = fullClient ? Path.Combine(candidatePath, "files") : candidatePath;
        ReleaseCatalog versionCatalog = new(context, signer);
        await versionCatalog.EnsureContentVersionAvailableAsync(
            releaseInfo.ReleaseId,
            releaseInfo.ContentVersion,
            cancellationToken);
        Dictionary<string, string?> sourceRevisions = releaseInfo.Changes
            .GroupBy(change => PathPolicy.NormalizeInstallPath(change.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().SourceRevision, StringComparer.OrdinalIgnoreCase);

        List<FileInfo> sourceFiles = ValidateCandidate(candidatePath, releaseInfo);
        int chunkSize = checked(context.Config.ChunkSizeMiB * 1024 * 1024);
        Dictionary<string, ManifestFile> manifestFiles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestFile> baseFiles = new(StringComparer.OrdinalIgnoreCase);
        bool migratingLegacyProtocol = false;
        ReleaseManifest? existingManifest = File.Exists(context.ManifestPath(releaseId))
            ? (await versionCatalog.LoadVerifiedAsync(releaseId, cancellationToken)).Manifest : null;
        int newLocalChunkCount = 0;
        long newLocalChunkBytes = 0;

        if (!string.IsNullOrWhiteSpace(releaseInfo.BaseReleaseId))
        {
            (ReleaseManifest baseManifest, _, _) = await versionCatalog.LoadVerifiedAsync(
                releaseInfo.BaseReleaseId,
                cancellationToken);
            LauncherTicketPolicy.ValidateAgainstBase(releaseInfo.MinimumLauncherVersion, baseManifest.MinimumLauncherVersion);
            migratingLegacyProtocol = baseManifest.LauncherTicketProtocol != 2;
            if (migratingLegacyProtocol && !fullClient)
                throw new PatchPublisherException("舊票證協定遷移必須使用完整客戶端，不能只更新 NX。");
            foreach (ManifestFile file in baseManifest.Files)
            {
                baseFiles[file.Path] = file;
                // 未變更檔案不沿用舊補丁；這一版只提供本次變更的補丁。
                manifestFiles[file.Path] = new ManifestFile
                {
                    Path = file.Path, SourceRevision = file.SourceRevision,
                    Size = file.Size, Sha256 = file.Sha256, Chunks = file.Chunks
                };
            }
        }

        foreach (FileInfo file in sourceFiles.OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessedFile processed = await ProcessFileAsync(file, chunkSize, cancellationToken);

            foreach (ProcessedChunk chunk in processed.Chunks)
            {
                if (chunk.WasNew)
                {
                    newLocalChunkCount++;
                    newLocalChunkBytes += chunk.Manifest.Size;
                }
            }

            string installPath = PathPolicy.NormalizeInstallPath(Path.GetRelativePath(payloadRoot, file.FullName));
            sourceRevisions.TryGetValue(installPath, out string? sourceRevision);
            FileDeltaPatch? patch = null;
            if (existingManifest is not null)
            {
                ManifestFile? existingFile = existingManifest.Files.SingleOrDefault(item => item.Path == installPath);
                if (existingFile?.Sha256 != processed.Sha256)
                    throw new PatchPublisherException($"已建立版本的內容不可修改：{installPath}");
                patch = existingFile.Patch;
                if (patch is not null) await DeltaPatchBuilder.VerifyPatchAsync(context, patch, cancellationToken);
            }
            else if (context.Config.EnableDeltaPatches && baseFiles.TryGetValue(installPath, out ManifestFile? baseFile) &&
                baseFile.Sha256 != processed.Sha256 && baseFile.Size > 0 && processed.Size > 0)
            {
                Console.WriteLine($"產生差異補丁：{installPath}");
                patch = await new DeltaPatchBuilder(context).BuildAsync(baseFile, file.FullName, processed.Sha256, processed.Size, cancellationToken);
                Console.WriteLine(patch is null ? $"補丁未小於完整檔案，僅保留完整分塊：{installPath}" :
                    $"補丁完成：{installPath}，{patch.Size:N0} / {processed.Size:N0} bytes");
            }
            manifestFiles[installPath] = new ManifestFile
            {
                Path = installPath,
                SourceRevision = sourceRevision,
                Size = processed.Size,
                Sha256 = processed.Sha256,
                Chunks = processed.Chunks.Select(chunk => chunk.Manifest).ToList(),
                Patch = patch
            };
        }

        IEnumerable<string> requiredFiles = fullClient ? releaseInfo.FullClientFiles! : context.AllowedFiles;
        if (fullClient)
        {
            HashSet<string> expected = requiredFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string removed in manifestFiles.Keys.Where(path => !expected.Contains(path)).ToArray())
                manifestFiles.Remove(removed);
        }
        string[] missingManifestFiles = requiredFiles
            .Except(manifestFiles.Keys, StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();
        if (missingManifestFiles.Length > 0)
        {
            throw new PatchPublisherException(
                $"合成後的 Manifest 缺少正式檔案：{string.Join(", ", missingManifestFiles)}");
        }

        List<ManifestFile> orderedManifestFiles = manifestFiles.Values
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (migratingLegacyProtocol && !orderedManifestFiles.Any(file =>
            string.Equals(file.Path, context.GameExecutable, StringComparison.OrdinalIgnoreCase) &&
            (!baseFiles.TryGetValue(file.Path, out ManifestFile? old) || file.Sha256 != old.Sha256)))
            throw new PatchPublisherException("舊票證協定遷移必須交付已更新的遊戲 EXE，不能只修改協定欄位。");
        List<string> deletePaths = releaseInfo.Delete
            .Select(PathPolicy.NormalizeInstallPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (deletePaths.Any(path => manifestFiles.ContainsKey(path)))
        {
            throw new PatchPublisherException("delete 清單不得刪除本版本仍要求存在的正式檔案。");
        }

        ReleaseManifest manifest = new()
        {
            SchemaVersion = releaseInfo.SchemaVersion,
            Preserve = releaseInfo.Preserve,
            Product = context.Config.Product,
            ReleaseId = releaseInfo.ReleaseId,
            ContentVersion = releaseInfo.ContentVersion,
            GameVersion = releaseInfo.GameVersion,
            MinimumLauncherVersion = releaseInfo.MinimumLauncherVersion,
            LauncherTicketProtocol = releaseInfo.LauncherTicketProtocol,
            CreatedAt = releaseInfo.CreatedAt,
            Files = orderedManifestFiles,
            Delete = deletePaths
        };

        versionCatalog.ValidateManifest(manifest, releaseId);

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PatchJsonContext.Default.ReleaseManifest);
        string manifestHash = FileUtilities.Sha256Hex(manifestBytes);
        await WriteImmutableAsync(context.ManifestPath(releaseId), manifestBytes, cancellationToken);
        string signaturePath = context.ManifestSignaturePath(releaseId);
        if (File.Exists(signaturePath))
        {
            string existingSignature = (await File.ReadAllTextAsync(signaturePath, cancellationToken)).Trim();
            signer.Verify(manifestBytes, existingSignature);
        }
        else
        {
            string signature = signer.Sign(manifestBytes);
            await WriteImmutableAsync(
                signaturePath,
                System.Text.Encoding.UTF8.GetBytes(signature + Environment.NewLine),
                cancellationToken);
        }

        List<ManifestChunk> uniqueChunks = orderedManifestFiles
            .SelectMany(file => file.Chunks)
            .GroupBy(chunk => chunk.Sha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        BuildReport report = new()
        {
            ReleaseId = releaseId,
            BuiltAt = DateTimeOffset.UtcNow,
            FileCount = orderedManifestFiles.Count,
            TotalFileBytes = orderedManifestFiles.Sum(file => file.Size),
            UniqueChunkCount = uniqueChunks.Count,
            UniqueChunkBytes = uniqueChunks.Sum(chunk => (long)chunk.Size),
            NewLocalChunkCount = newLocalChunkCount,
            NewLocalChunkBytes = newLocalChunkBytes,
            ManifestSha256 = manifestHash,
            PatchCount = orderedManifestFiles.Count(file => file.Patch is not null),
            PatchBytes = orderedManifestFiles.Sum(file => file.Patch?.Size ?? 0)
        };
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, PatchJsonContext.Default.BuildReport);
        string reportPath = context.BuildReportPath(releaseId);
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }
        await FileUtilities.WriteAtomicAsync(reportPath, reportBytes, cancellationToken);
        return (manifest, report);
    }

    private async Task<ProcessedFile> ProcessFileAsync(FileInfo source, int chunkSize, CancellationToken cancellationToken)
    {
        long initialLength = source.Length;
        DateTime initialWriteTime = source.LastWriteTimeUtc;
        List<ProcessedChunk> chunks = [];
        using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            await using FileStream input = new(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long fileOffset = 0;
            while (fileOffset < initialLength)
            {
                string temporaryChunk = Path.Combine(context.ChunkRoot, $".{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(context.ChunkRoot);
                int chunkLength = 0;
                using IncrementalHash chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    await using (FileStream output = new(
                        temporaryChunk,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        IoBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        while (chunkLength < chunkSize)
                        {
                            int requested = Math.Min(buffer.Length, chunkSize - chunkLength);
                            int read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                            if (read == 0)
                            {
                                break;
                            }

                            fileHash.AppendData(buffer, 0, read);
                            chunkHash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            chunkLength += read;
                        }
                    }

                    if (chunkLength == 0)
                    {
                        throw new PatchPublisherException($"讀取檔案時意外提前結束：{source.Name}");
                    }

                    string chunkSha256 = Convert.ToHexStringLower(chunkHash.GetHashAndReset());
                    string objectKey = ObjectKeys.ForChunk(chunkSha256);
                    string finalChunkPath = ObjectKeys.LocalChunkPath(context.ChunkRoot, chunkSha256);
                    Directory.CreateDirectory(Path.GetDirectoryName(finalChunkPath)!);
                    bool wasNew = false;
                    if (File.Exists(finalChunkPath))
                    {
                        if (new FileInfo(finalChunkPath).Length != chunkLength)
                        {
                            throw new PatchPublisherException($"本機 chunk hash 衝突或檔案損壞：{chunkSha256}");
                        }
                        File.Delete(temporaryChunk);
                    }
                    else
                    {
                        File.Move(temporaryChunk, finalChunkPath);
                        wasNew = true;
                    }

                    chunks.Add(new ProcessedChunk(
                        new ManifestChunk
                        {
                            Offset = fileOffset,
                            Size = chunkLength,
                            Sha256 = chunkSha256,
                            Key = objectKey
                        },
                        wasNew));
                    fileOffset += chunkLength;
                }
                finally
                {
                    if (File.Exists(temporaryChunk))
                    {
                        File.Delete(temporaryChunk);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        source.Refresh();
        if (source.Length != initialLength || source.LastWriteTimeUtc != initialWriteTime)
        {
            throw new PatchPublisherException($"Build 過程中檔案遭到修改：{source.FullName}");
        }

        return new ProcessedFile(
            initialLength,
            Convert.ToHexStringLower(fileHash.GetHashAndReset()),
            chunks);
    }

    private List<FileInfo> ValidateCandidate(string candidatePath, ReleaseInfo releaseInfo)
    {
        if (releaseInfo.SchemaVersion == 2)
        {
            if (releaseInfo.FullClientFiles is null)
                throw new PatchPublisherException("完整客戶端 Candidate 缺少檔案清單。");
            PathPolicy.ValidateFileSet(releaseInfo.FullClientFiles);
            ClientFilePolicy policy = ClientFilePolicy.Load(context.ProjectRoot);
            policy.ValidateRequired(releaseInfo.FullClientFiles);
            PathPolicy.ValidateInstallPath(context.GameExecutable);
            if (!releaseInfo.FullClientFiles.Contains(context.GameExecutable, StringComparer.OrdinalIgnoreCase))
                throw new PatchPublisherException($"完整客戶端缺少遊戲主程式：{context.GameExecutable}");
            List<string> all = ClientFilePolicy.EnumerateFiles(candidatePath);
            if (all.Any(path => path != "release-info.json" && !path.StartsWith("files/", StringComparison.Ordinal)))
                throw new PatchPublisherException("完整客戶端 Candidate 內容必須位於 files 子目錄。");
            List<string> fullActual = all.Where(path => path.StartsWith("files/", StringComparison.Ordinal)).Select(path => path[6..]).ToList();
            List<string> declared = releaseInfo.Changes.Select(change => change.Path).ToList();
            PathPolicy.ValidateFileSet(declared);
            HashSet<string> expected = releaseInfo.FullClientFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!fullActual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(declared) || fullActual.Any(path => !expected.Contains(path)) ||
                string.IsNullOrWhiteSpace(releaseInfo.BaseReleaseId) && !expected.SetEquals(fullActual))
                throw new PatchPublisherException("Candidate 變更檔案與完整清單不一致。");
            return fullActual.Select(path => new FileInfo(Path.Combine(candidatePath, "files", path))).ToList();
        }
        DirectoryInfo candidate = new(candidatePath);
        if ((candidate.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PatchPublisherException("Candidate 不得是 junction 或 symlink。");
        }

        if (candidate.EnumerateDirectories().Any())
        {
            throw new PatchPublisherException("Candidate 不得包含子目錄。");
        }

        List<FileInfo> files = candidate.EnumerateFiles().ToList();
        foreach (FileInfo file in files)
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PatchPublisherException($"Candidate 不得包含 junction 或 symlink：{file.Name}");
            }
        }

        List<FileInfo> nxFiles = files.Where(file => file.Extension.Equals(".nx", StringComparison.OrdinalIgnoreCase)).ToList();
        HashSet<string> actual = nxFiles.Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] unexpected = actual.Except(context.AllowedFiles, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (unexpected.Length > 0)
        {
            throw new PatchPublisherException($"Candidate 含有不允許的 NX：{string.Join(", ", unexpected)}");
        }

        if (string.IsNullOrWhiteSpace(releaseInfo.BaseReleaseId))
        {
            string[] missing = context.AllowedFiles.Except(actual, StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (missing.Length > 0)
            {
                throw new PatchPublisherException(
                    $"完整 Candidate 缺少正式檔案：{string.Join(", ", missing)}");
            }
        }
        else
        {
            HashSet<string> declaredChanges = releaseInfo.Changes
                .Select(change => PathPolicy.NormalizeInstallPath(change.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] undeclared = actual.Except(declaredChanges, StringComparer.OrdinalIgnoreCase).Order().ToArray();
            string[] missingChanges = declaredChanges.Except(actual, StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (undeclared.Length > 0 || missingChanges.Length > 0)
            {
                throw new PatchPublisherException(
                    $"差異 Candidate 與 release-info.json 不一致。未宣告：{string.Join(", ", undeclared)}；缺少：{string.Join(", ", missingChanges)}");
            }
        }

        string[] unexpectedOtherFiles = files
            .Where(file => !file.Name.Equals("release-info.json", StringComparison.OrdinalIgnoreCase) &&
                           !file.Extension.Equals(".nx", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Name)
            .Order()
            .ToArray();
        if (unexpectedOtherFiles.Length > 0)
        {
            throw new PatchPublisherException($"Candidate 含有不允許的檔案：{string.Join(", ", unexpectedOtherFiles)}");
        }

        return nxFiles;
    }

    private async Task<ReleaseInfo> LoadAndValidateReleaseInfoAsync(
        string candidatePath,
        string expectedReleaseId,
        CancellationToken cancellationToken)
    {
        string releaseInfoPath = Path.Combine(candidatePath, "release-info.json");
        if (!File.Exists(releaseInfoPath))
        {
            throw new PatchPublisherException($"Candidate 缺少 release-info.json：{releaseInfoPath}");
        }

        await using FileStream stream = File.OpenRead(releaseInfoPath);
        ReleaseInfo releaseInfo = await JsonSerializer.DeserializeAsync(
            stream,
            PatchJsonContext.Default.ReleaseInfo,
            cancellationToken) ?? throw new PatchPublisherException("release-info.json 格式錯誤。");

        if (releaseInfo.LauncherTicketProtocol != 2)
            throw new PatchPublisherException("舊票證協定已移除，請使用支援新交接的 NovaStory 重新 Prepare（launcherTicketProtocol 必須是 2）。");
        LauncherTicketPolicy.ValidateMinimumVersion(releaseInfo.MinimumLauncherVersion);
        if (releaseInfo.SchemaVersion != 2)
            throw new PatchPublisherException("新發布必須使用完整客戶端 Candidate，不能只更新 NX。");

        if (!string.Equals(releaseInfo.ReleaseId, expectedReleaseId, StringComparison.Ordinal))
        {
            throw new PatchPublisherException("Candidate 目錄名稱與 release-info.json 的 Release ID 不一致。");
        }
        if (!string.Equals(releaseInfo.GameVersion, context.Config.GameVersion, StringComparison.Ordinal))
        {
            throw new PatchPublisherException("Candidate 的遊戲版本與產品設定不一致。");
        }
        if (releaseInfo.ContentVersion <= 0)
        {
            throw new PatchPublisherException("contentVersion 必須大於零。");
        }

        return releaseInfo;
    }

    private static async Task WriteImmutableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            byte[] existing = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new PatchPublisherException($"不可覆寫已存在且內容不同的不可變檔案：{path}");
            }
            return;
        }

        await FileUtilities.WriteAtomicAsync(path, bytes, cancellationToken);
    }

    private sealed record ProcessedFile(long Size, string Sha256, List<ProcessedChunk> Chunks);
    private sealed record ProcessedChunk(ManifestChunk Manifest, bool WasNew);
}

public static class ObjectKeys
{
    public static string ForPatch(string sha256) => $"objects/patches/sha256/{sha256[..2]}/{sha256}";
    public static string LocalPatchPath(ProjectContext context, string sha256) =>
        Path.Combine(context.PatchRoot, sha256[..2], sha256);
    public static IEnumerable<(string Key, string Sha256, long Size)> Payloads(ReleaseManifest manifest) =>
        manifest.Files.SelectMany(file => file.Chunks.Select(chunk => (chunk.Key, chunk.Sha256, (long)chunk.Size)))
            .Concat(manifest.Files.Where(file => file.Patch is not null).Select(file => (file.Patch!.Key, file.Patch.Sha256, file.Patch.Size)))
            .GroupBy(item => item.Key, StringComparer.Ordinal).Select(group => group.First());
    public static string LocalPayloadPath(ProjectContext context, string key, string sha256) =>
        key == ForPatch(sha256) ? LocalPatchPath(context, sha256) : LocalChunkPath(context.ChunkRoot, sha256);
    public static string ForChunk(string sha256) => $"objects/sha256/{sha256[..2]}/{sha256}";
    public static string ForManifest(string releaseId) => $"manifests/{releaseId}.json";
    public static string ForManifestSignature(string releaseId) => $"manifests/{releaseId}.sig";
    public static string ForChannel(string channel) => $"channels/{channel}.json";
    public static string ForChannelHistory(string channel, long generation) =>
        $"channel-history/{channel}/{generation:D20}.json";

    public static string LocalChunkPath(string chunkRoot, string sha256) =>
        Path.Combine(chunkRoot, "sha256", sha256[..2], sha256);
}
