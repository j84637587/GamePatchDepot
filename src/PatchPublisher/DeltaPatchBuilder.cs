using System.Diagnostics;
using System.Security.Cryptography;

namespace GamePatchDepot;

public sealed class DeltaPatchBuilder(ProjectContext context)
{
    public async Task<FileDeltaPatch?> BuildAsync(ManifestFile baseline, string newPath, string newHash, long newSize,
        CancellationToken cancellationToken = default)
    {
        string toolRoot = Path.Combine(context.ProjectRoot, "tools", "hdiffpatch");
        HdiffPatchTool tool = new(toolRoot);
        tool.EnsureAvailable();
        string directory = Path.Combine(context.ProjectRoot, "work", "staging", $"delta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string oldPath = Path.Combine(directory, "base.bin");
        string patchPath = Path.Combine(directory, "delta.hdiff");
        string restoredPath = Path.Combine(directory, "verified.bin");
        try
        {
            await RestoreBaseAsync(baseline, oldPath, cancellationToken);
            await tool.CreateAsync(oldPath, newPath, patchPath, cancellationToken);
            if (new FileInfo(patchPath).Length >= newSize) return null;
            // 不只信任 diff 工具的成功碼，實際重組後再比對完整 SHA-256。
            await tool.ApplyAsync(oldPath, patchPath, restoredPath, cancellationToken);
            await VerifyFileAsync(restoredPath, newSize, newHash, cancellationToken);
            string hash = await FileUtilities.Sha256FileAsync(patchPath, cancellationToken);
            FileDeltaPatch patch = new()
            {
                BaseSha256 = baseline.Sha256, BaseSize = baseline.Size,
                Sha256 = hash, Size = new FileInfo(patchPath).Length, Key = ObjectKeys.ForPatch(hash)
            };
            string finalPath = ObjectKeys.LocalPatchPath(context, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(finalPath)) await VerifyPatchAsync(context, patch, cancellationToken);
            else File.Move(patchPath, finalPath);
            return patch;
        }
        finally
        {
            foreach (string path in new[] { oldPath, patchPath, restoredPath })
                if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private async Task RestoreBaseAsync(ManifestFile baseline, string destination, CancellationToken cancellationToken)
    {
        await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[1024 * 1024];
            foreach (ManifestChunk chunk in baseline.Chunks)
            {
                string path = ObjectKeys.LocalChunkPath(context.ChunkRoot, chunk.Sha256);
                if (!File.Exists(path))
                    throw new PatchPublisherException("產生補丁需要本機保留基準版本的 chunks；請恢復基準資料後再 Build。");
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using FileStream input = File.OpenRead(path);
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    count += read;
                    if (count > chunk.Size) throw new PatchPublisherException("基準 chunk 大小錯誤。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (count != chunk.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != chunk.Sha256)
                    throw new PatchPublisherException("基準 chunk SHA-256 不符，不可產生補丁。");
            }
        }
        await VerifyFileAsync(destination, baseline.Size, baseline.Sha256, cancellationToken);
    }

    public static Task VerifyPatchAsync(ProjectContext context, FileDeltaPatch patch, CancellationToken cancellationToken = default) =>
        VerifyFileAsync(ObjectKeys.LocalPatchPath(context, patch.Sha256), patch.Size, patch.Sha256, cancellationToken);

    public static async Task VerifyFileAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size ||
            await FileUtilities.Sha256FileAsync(path, cancellationToken) != sha256)
            throw new PatchPublisherException($"補丁／重組檔案大小或 SHA-256 不符：{Path.GetFileName(path)}");
    }
}

public sealed class HdiffPatchTool(string toolRoot)
{
    public void EnsureAvailable()
    {
        foreach (string name in new[] { "hdiffz.exe", "hpatchz.exe" })
            if (!File.Exists(Path.Combine(toolRoot, name)))
                throw new PatchPublisherException("缺少差異補丁工具，請先執行 setup-patch-tools.ps1。");
    }

    public Task CreateAsync(string oldPath, string newPath, string patchPath, CancellationToken cancellationToken) =>
        RunAsync("hdiffz.exe", ["-s-64k", "-SD", "-c-zstd-3", "-p-2", oldPath, newPath, patchPath], cancellationToken);

    public Task ApplyAsync(string oldPath, string patchPath, string outputPath, CancellationToken cancellationToken) =>
        RunAsync("hpatchz.exe", ["-s-8m", oldPath, patchPath, outputPath], cancellationToken);

    private async Task RunAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        string executablePath = Path.GetFullPath(Path.Combine(toolRoot, executable));
        string expectedHash = executable == "hdiffz.exe"
            ? "f5ed7ac622a2daf4a31cc21ffa8ea1717f92323e79ef5ae695c5c9238a282f52"
            : "9703c694b5955c576d9f0e26e98b60941f0bbb53b382b1f1988d75d461e580cb";
        if (await FileUtilities.Sha256FileAsync(executablePath, cancellationToken) != expectedHash)
            throw new PatchPublisherException("差異補丁工具 SHA-256 與固定版本不符，停止執行。");
        ProcessStartInfo start = new(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        process.Start();
        // 持續排空輸出，避免大型檔案進度文字填滿管線；不將整份輸出累積在記憶體。
        Task stdout = DrainAsync(process.StandardOutput);
        Task stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await Task.WhenAll(stdout, stderr); }
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new PatchPublisherException($"{executable} 執行失敗，結束碼 {process.ExitCode}；未建立可發布補丁。");
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer) != 0) { }
    }
}
