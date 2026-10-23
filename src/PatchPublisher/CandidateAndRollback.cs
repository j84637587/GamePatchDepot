using System.Security.Cryptography;
using System.Text.Json;

namespace GamePatchDepot;

public sealed class CandidatePreparer(ProjectContext context, ReleaseCatalog catalog)
{
    public async Task<string> PrepareAsync(
        string releaseId,
        long contentVersion,
        string sourceDirectory,
        IReadOnlyDictionary<string, string> replacements,
        string? description,
        CancellationToken cancellationToken = default,
        string? baseReleaseId = null,
        bool fullClient = false,
        PrepareSelection? selection = null,
        int? launcherTicketProtocol = null,
        string? minimumLauncherVersion = null)
    {
        if (launcherTicketProtocol is not null and not 2)
            throw new PatchPublisherException("舊票證協定已移除，僅支援 launcherTicketProtocol 2。");
        context.EnsureDirectories();
        if (contentVersion <= 0)
        {
            throw new PatchPublisherException("contentVersion 必須大於零。");
        }
        await catalog.EnsureContentVersionAvailableAsync(releaseId, contentVersion, cancellationToken);

        string sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new PatchPublisherException($"找不到來源目錄：{sourceRoot}");
        }
        if (selection is not null)
        {
            if (!fullClient) throw new PatchPublisherException("檔案確認清單只適用於完整客戶端模式。");
            await new PrepareSelectionService(context, catalog).ValidateAsync(
                selection, sourceRoot, baseReleaseId, replacements, cancellationToken);
        }

        string finalPath = context.CandidatePath(releaseId);
        if (Directory.Exists(finalPath) || File.Exists(finalPath))
        {
            throw new PatchPublisherException($"Candidate 已存在，為避免覆寫而停止：{finalPath}");
        }

        string stagingRoot = Path.Combine(context.ProjectRoot, "work", "staging");
        Directory.CreateDirectory(stagingRoot);
        string stagingPath = Path.Combine(stagingRoot, $"prepare-{releaseId}-{Guid.NewGuid():N}");
        EnsureUnder(stagingPath, stagingRoot);
        Directory.CreateDirectory(stagingPath);

        try
        {
            ReleaseManifest? baseManifest = null;
            Dictionary<string, ManifestFile> baseFiles = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(baseReleaseId))
            {
                (baseManifest, _, _) = await catalog.LoadVerifiedAsync(baseReleaseId, cancellationToken);
                if (contentVersion <= baseManifest.ContentVersion)
                {
                    throw new PatchPublisherException(
                        $"Content Version 必須高於基準版本 {baseManifest.ContentVersion}（{baseReleaseId}）。");
                }
                baseFiles = baseManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            }

            if (!fullClient && baseManifest?.SchemaVersion == 2)
                throw new PatchPublisherException("完整客戶端版本必須使用 --full-client 準備後續更新。");
            if (!fullClient && baseManifest?.LauncherTicketProtocol != 2)
                throw new PatchPublisherException("首次發布或從舊票證協定遷移必須使用 --full-client，包含支援新交接的遊戲 EXE；不能只更新 NX。");
            string? selectedMinimum = minimumLauncherVersion ??
                (baseManifest?.LauncherTicketProtocol == 2 ? baseManifest.MinimumLauncherVersion : null);
            LauncherTicketPolicy.ValidateAgainstBase(selectedMinimum, baseManifest?.MinimumLauncherVersion);

            ClientFilePolicy policy = fullClient ? ClientFilePolicy.Load(context.ProjectRoot) : new();
            List<string> sourceFiles = fullClient ? ClientFilePolicy.EnumerateFiles(sourceRoot) : context.AllowedFiles.ToList();
            List<string> excluded = fullClient ? sourceFiles.Where(policy.IsExcluded).ToList() : [];
            List<string> installFiles = sourceFiles.Except(excluded, StringComparer.OrdinalIgnoreCase).ToList();
            if (selection is not null)
            {
                installFiles = selection.Files.Where(file => file.Include).Select(file => file.Path).ToList();
                excluded = sourceFiles.Except(installFiles, StringComparer.OrdinalIgnoreCase).ToList();
            }
            foreach (string replacement in replacements.Keys)
            {
                PathPolicy.ValidateInstallPath(replacement);
                if (selection is null && !installFiles.Contains(replacement, StringComparer.OrdinalIgnoreCase))
                    throw new PatchPublisherException($"替換目標必須存在於來源且未被排除：{replacement}");
            }
            if (fullClient) policy.ValidateRequired(installFiles);
            List<string> deleted = fullClient
                ? baseFiles.Keys.Except(installFiles, StringComparer.OrdinalIgnoreCase).Where(path => !policy.IsExcluded(path)).ToList()
                : [];
            if (selection is not null)
                deleted = selection.Files.Where(file => file.Remove).Select(file => file.Path).ToList();
            List<string>? preserved = fullClient ? excluded.Concat(baseFiles.Keys.Where(policy.IsExcluded))
                .Concat(selection?.Files.Where(file => !file.Include && !file.Remove).Select(file => file.Path) ?? [])
                .Concat(policy.PreservedPaths).Concat(baseManifest?.Preserve ?? [])
                .Except(deleted, StringComparer.OrdinalIgnoreCase)
                .Where(path => !installFiles.Any(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase) ||
                    file.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
            List<ReleaseChange> changes = [];
            foreach (string installName in installFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = replacements.TryGetValue(installName, out string? replacement)
                    ? Path.GetFullPath(replacement)
                    : Path.Combine(sourceRoot, installName);
                if (!File.Exists(sourcePath))
                {
                    throw new PatchPublisherException($"找不到 Candidate 來源：{installName} ← {sourcePath}");
                }
                if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PatchPublisherException($"Candidate 來源不得是 junction 或 symlink：{sourcePath}");
                }

                string sourceHash = await ComputeStableSha256Async(sourcePath, cancellationToken);
                long sourceSize = new FileInfo(sourcePath).Length;
                bool changed = baseManifest is null ||
                    !baseFiles.TryGetValue(installName, out ManifestFile? baseFile) ||
                    baseFile.Size != sourceSize ||
                    !string.Equals(baseFile.Sha256, sourceHash, StringComparison.Ordinal);
                if (!changed)
                {
                    continue;
                }

                string destination = Path.Combine(fullClient ? Path.Combine(stagingPath, "files") : stagingPath, installName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(sourcePath, destination, cancellationToken);
                changes.Add(new ReleaseChange
                {
                    Path = installName,
                    SourceRevision = replacements.ContainsKey(installName)
                        ? Path.GetFileNameWithoutExtension(sourcePath)
                        : null,
                    Description = description
                });
            }

            const int selectedProtocol = 2;
            bool protocolChanged = baseManifest is not null && selectedProtocol != baseManifest.LauncherTicketProtocol;
            if (protocolChanged && !changes.Any(change => change.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new PatchPublisherException("舊票證協定遷移必須交付已更新的遊戲 EXE，不能只修改協定欄位。");
            bool minimumChanged = baseManifest is not null && selectedMinimum != baseManifest.MinimumLauncherVersion;
            if (baseManifest is not null && !protocolChanged && !minimumChanged && changes.Count == 0 && deleted.Count == 0 &&
                (!fullClient || baseManifest.SchemaVersion == 2 && baseFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(installFiles) &&
                    (baseManifest.Preserve ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(preserved!)))
            {
                throw new PatchPublisherException($"所有受管檔案都與基準版本 {baseReleaseId} 相同，沒有可建立的變更。");
            }

            ReleaseInfo releaseInfo = new()
            {
                SchemaVersion = fullClient ? 2 : 1,
                FullClientFiles = fullClient ? installFiles : null,
                ExcludedFiles = excluded,
                Preserve = preserved,
                ReleaseId = releaseId,
                ContentVersion = contentVersion,
                GameVersion = context.Config.GameVersion,
                MinimumLauncherVersion = selectedMinimum!,
                LauncherTicketProtocol = selectedProtocol,
                SourceDirectory = sourceRoot,
                CreatedAt = DateTimeOffset.UtcNow,
                BaseReleaseId = baseReleaseId,
                Changes = changes,
                Delete = deleted
            };
            byte[] infoBytes = JsonSerializer.SerializeToUtf8Bytes(releaseInfo, PatchJsonContext.Default.ReleaseInfo);
            await File.WriteAllBytesAsync(Path.Combine(stagingPath, "release-info.json"), infoBytes, cancellationToken);

            Directory.CreateDirectory(context.CandidateRoot);
            Directory.Move(stagingPath, finalPath);
            return finalPath;
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                EnsureUnder(stagingPath, stagingRoot);
                Directory.Delete(stagingPath, true);
            }
            throw;
        }
    }

    private static async Task<string> ComputeStableSha256Async(
        string source,
        CancellationToken cancellationToken)
    {
        FileInfo sourceInfo = new(source);
        long initialLength = sourceInfo.Length;
        DateTime initialWriteTime = sourceInfo.LastWriteTimeUtc;
        await using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        sourceInfo.Refresh();
        if (sourceInfo.Length != initialLength || sourceInfo.LastWriteTimeUtc != initialWriteTime)
        {
            throw new PatchPublisherException($"Prepare 比對期間來源檔案遭到修改：{source}");
        }
        return hash;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        FileInfo sourceInfo = new(source);
        long initialLength = sourceInfo.Length;
        DateTime initialWriteTime = sourceInfo.LastWriteTimeUtc;
        await using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
        sourceInfo.Refresh();
        if (sourceInfo.Length != initialLength || sourceInfo.LastWriteTimeUtc != initialWriteTime)
        {
            throw new PatchPublisherException($"Prepare 複製期間來源檔案遭到修改：{source}");
        }
    }

    private static void EnsureUnder(string path, string root)
    {
        string absolutePath = Path.GetFullPath(path);
        string absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatchPublisherException($"暫存路徑超出允許範圍：{absolutePath}");
        }
    }
}

public sealed class RollbackBuilder(ProjectContext context, SigningService signer, ReleaseCatalog catalog)
{
    public async Task<ReleaseManifest> BuildAsync(
        string newReleaseId,
        long newContentVersion,
        string targetReleaseId,
        CancellationToken cancellationToken = default)
    {
        if (newContentVersion <= 0)
        {
            throw new PatchPublisherException("新的 contentVersion 必須大於零。");
        }

        (ReleaseManifest target, _, _) = await catalog.LoadVerifiedAsync(targetReleaseId, cancellationToken);
        if (target.LauncherTicketProtocol != 2)
            throw new PatchPublisherException("此回滾目標使用已移除的舊票證協定，請選擇支援新交接的版本。");
        LauncherTicketPolicy.ValidateMinimumVersion(target.MinimumLauncherVersion);
        if (newContentVersion <= target.ContentVersion)
        {
            throw new PatchPublisherException("回滾版本的 contentVersion 仍必須高於目標內容版本。");
        }
        await catalog.EnsureContentVersionAvailableAsync(newReleaseId, newContentVersion, cancellationToken);

        ReleaseManifest rollback = new()
        {
            SchemaVersion = target.SchemaVersion,
            Preserve = target.Preserve,
            Product = target.Product,
            ReleaseId = newReleaseId,
            ContentVersion = newContentVersion,
            GameVersion = target.GameVersion,
            MinimumLauncherVersion = target.MinimumLauncherVersion,
            LauncherTicketProtocol = target.LauncherTicketProtocol,
            CreatedAt = DateTimeOffset.UtcNow,
            Files = target.Files,
            Delete = target.Delete
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(rollback, PatchJsonContext.Default.ReleaseManifest);
        await WriteImmutableAsync(context.ManifestPath(newReleaseId), manifestBytes, cancellationToken);
        string signaturePath = context.ManifestSignaturePath(newReleaseId);
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

        BuildReport report = new()
        {
            ReleaseId = newReleaseId,
            BuiltAt = DateTimeOffset.UtcNow,
            FileCount = rollback.Files.Count,
            TotalFileBytes = rollback.Files.Sum(file => file.Size),
            UniqueChunkCount = rollback.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.Sha256).Distinct().Count(),
            UniqueChunkBytes = rollback.Files.SelectMany(file => file.Chunks).GroupBy(chunk => chunk.Sha256).Sum(group => group.First().Size),
            NewLocalChunkCount = 0,
            NewLocalChunkBytes = 0,
            ManifestSha256 = FileUtilities.Sha256Hex(manifestBytes)
        };
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, PatchJsonContext.Default.BuildReport);
        await WriteReplaceOrCreateAsync(context.BuildReportPath(newReleaseId), reportBytes, cancellationToken);
        return rollback;
    }

    private static async Task WriteImmutableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            byte[] existing = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new PatchPublisherException($"不可覆寫已存在且內容不同的檔案：{path}");
            }
            return;
        }
        await FileUtilities.WriteAtomicAsync(path, bytes, cancellationToken);
    }

    private static async Task WriteReplaceOrCreateAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await FileUtilities.WriteReplaceAtomicAsync(path, bytes, cancellationToken);
        }
        else
        {
            await FileUtilities.WriteAtomicAsync(path, bytes, cancellationToken);
        }
    }
}
