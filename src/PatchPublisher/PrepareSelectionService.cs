using System.Text.Json;

namespace GamePatchDepot;

public sealed class PrepareSelectionService(ProjectContext context, ReleaseCatalog catalog)
{
    public async Task<PrepareSelection> ScanAsync(string sourceDirectory, string? baseReleaseId,
        IReadOnlyDictionary<string, string> replacements, CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        ClientFilePolicy policy = ClientFilePolicy.Load(context.ProjectRoot);
        List<string> source = ClientFilePolicy.EnumerateFiles(root);
        Dictionary<string, ManifestFile> previous = new(StringComparer.OrdinalIgnoreCase);
        List<string> preserved = [];
        string? baseHash = null;
        if (!string.IsNullOrWhiteSpace(baseReleaseId))
        {
            (ReleaseManifest manifest, byte[] bytes, _) = await catalog.LoadVerifiedAsync(baseReleaseId, cancellationToken);
            previous = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            preserved = manifest.Preserve ?? [];
            baseHash = FileUtilities.Sha256Hex(bytes);
        }
        foreach (string replacement in replacements.Keys)
        {
            PathPolicy.ValidateInstallPath(replacement);
            if (!source.Contains(replacement, StringComparer.OrdinalIgnoreCase))
                throw new PatchPublisherException($"替換目標不存在於來源目錄：{replacement}");
        }

        HashSet<string> present = source.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SelectionFile> files = [];
        foreach (string path in source.Union(previous.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool exists = present.Contains(path);
            bool excluded = policy.IsExcluded(path);
            bool wasManaged = previous.ContainsKey(path);
            string? replacement = replacements.TryGetValue(path, out string? value) ? Path.GetFullPath(value) : null;
            FileInfo? info = exists ? new FileInfo(replacement ?? Path.Combine(root, path)) : null;
            if (info is not null)
            {
                if (!info.Exists) throw new PatchPublisherException($"來源檔案已不存在：{path}");
                ClientFilePolicy.RejectLink(info);
            }
            bool previouslyPreserved = preserved.Any(item => string.Equals(path, item, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase));
            bool include = exists && !excluded && !previouslyPreserved;
            files.Add(new SelectionFile
            {
                Path = path, Present = exists, WasManaged = wasManaged, PolicyExcluded = excluded,
                Size = info?.Length ?? 0, LastWriteUtcTicks = info?.LastWriteTimeUtc.Ticks ?? 0,
                ReplacementPath = replacement, RecommendedInclude = include, Include = include,
                Reason = excluded ? "設定規則排除（修改 client-files.json 才能收錄）" :
                    !exists ? "舊版有此檔案，來源已不存在；預設保留玩家檔案" :
                    previouslyPreserved ? "先前版本已保留；可重新勾選納入管理" :
                    replacement is not null ? "使用選定的 UI 替換檔" :
                    wasManaged ? "舊版已追蹤，Prepare 時才比對變更" : "來源中的新檔案"
            });
        }
        return new PrepareSelection
        {
            SourceDirectory = root, BaseReleaseId = baseReleaseId, BaseManifestSha256 = baseHash,
            PolicySha256 = FileUtilities.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(policy)),
            RequiredFiles = policy.RequiredFiles, Files = files
        };
    }

    public async Task ValidateAsync(PrepareSelection selection, string sourceDirectory, string? baseReleaseId,
        IReadOnlyDictionary<string, string> replacements, CancellationToken cancellationToken)
    {
        PrepareSelection current = await ScanAsync(sourceDirectory, baseReleaseId, replacements, cancellationToken);
        if (selection.SchemaVersion != 1 ||
            !string.Equals(selection.SourceDirectory, current.SourceDirectory, StringComparison.OrdinalIgnoreCase) ||
            selection.BaseReleaseId != current.BaseReleaseId || selection.BaseManifestSha256 != current.BaseManifestSha256 ||
            selection.PolicySha256 != current.PolicySha256)
            throw new PatchPublisherException("來源、比較版本或規則已改變，請重新 Prepare 並確認清單。");
        PathPolicy.ValidateFileSet(selection.Files.Select(file => file.Path));
        Dictionary<string, SelectionFile> selected = selection.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        if (!selected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(current.Files.Select(file => file.Path)))
            throw new PatchPublisherException("預覽後檔案清單已改變，請重新掃描確認。");
        foreach (SelectionFile file in current.Files)
        {
            SelectionFile choice = selected[file.Path];
            if (choice.Present != file.Present || choice.Size != file.Size || choice.LastWriteUtcTicks != file.LastWriteUtcTicks ||
                choice.ReplacementPath != file.ReplacementPath || choice.PolicyExcluded != file.PolicyExcluded || choice.WasManaged != file.WasManaged)
                throw new PatchPublisherException($"預覽後檔案狀態已改變：{file.Path}，請重新掃描確認。");
            if (choice.Include && !file.CanInclude || choice.Remove && (!file.WasManaged || file.PolicyExcluded) || choice.Include && choice.Remove)
                throw new PatchPublisherException($"檔案選取不符合規則：{file.Path}");
        }
        ClientFilePolicy.Load(context.ProjectRoot).ValidateRequired(selection.Files.Where(file => file.Include).Select(file => file.Path));
    }
}
