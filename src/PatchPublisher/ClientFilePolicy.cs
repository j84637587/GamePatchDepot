using System.Text.Json;

namespace GamePatchDepot;

public sealed class ClientFilePolicy
{
    public List<string> ExcludedDirectories { get; init; } = [".git", ".vs", ".patch", "logs", "screenshots"];
    public List<string> ExcludedFiles { get; init; } = ["credentials.json", "accounts.dat", ".env"];
    public List<string> ExcludedExtensions { get; init; } = [".log", ".tmp", ".pdb", ".pem", ".key", ".pfx"];
    public List<string> PreservedPaths { get; init; } = [];
    public List<string> RequiredFiles { get; init; } = [];

    public static ClientFilePolicy Load(string projectRoot)
    {
        string path = Path.Combine(projectRoot, "config", "client-files.json");
        ClientFilePolicy policy = File.Exists(path)
            ? JsonSerializer.Deserialize<ClientFilePolicy>(File.ReadAllBytes(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new PatchPublisherException("client-files.json 格式錯誤。")
            : new ClientFilePolicy();
        foreach (string item in policy.PreservedPaths.Concat(policy.RequiredFiles))
            PathPolicy.ValidateInstallPath(item);
        return policy;
    }

    public bool IsExcluded(string path)
    {
        string[] parts = path.Split('/');
        return parts.SkipLast(1).Any(part => ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)) ||
            ExcludedFiles.Contains(parts[^1], StringComparer.OrdinalIgnoreCase) ||
            ExcludedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
            PreservedPaths.Any(item => string.Equals(path, item.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(item.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    public void ValidateRequired(IEnumerable<string> paths)
    {
        HashSet<string> files = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!files.Any(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PatchPublisherException("完整客戶端必須包含遊戲 EXE，請選擇客戶端根目錄，而不是只有 NX 的目錄。");
        }
        foreach (string path in RequiredFiles)
        {
            PathPolicy.ValidateInstallPath(path);
            if (!files.Contains(path))
            {
                throw new PatchPublisherException($"完整客戶端缺少必備檔案：{path}");
            }
        }
    }

    // 不追蹤連結；每層先檢查再走訪，避免離開來源範圍。
    public static List<string> EnumerateFiles(string root)
    {
        List<string> files = [];
        for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
            RejectLink(parent);
        Visit(new DirectoryInfo(root));
        PathPolicy.ValidateFileSet(files);
        return files.Order(StringComparer.OrdinalIgnoreCase).ToList();

        void Visit(DirectoryInfo directory)
        {
            RejectLink(directory);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                RejectLink(entry);
                string relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                PathPolicy.ValidateInstallPath(relative);
                if (entry is DirectoryInfo child)
                {
                    Visit(child);
                }
                else
                {
                    files.Add(relative);
                }
            }
        }
    }

    public static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new PatchPublisherException($"來源或 Candidate 不得包含 junction／symlink：{entry.FullName}");
        }
    }
}
