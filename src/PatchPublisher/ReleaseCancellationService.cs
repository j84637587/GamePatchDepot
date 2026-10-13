using System.Text.Json;
using System.Text.RegularExpressions;

namespace GamePatchDepot;

public sealed partial class ReleaseCancellationService(ProjectContext context, ReleaseCatalog catalog)
{
    public async Task DiscardCandidateAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        ValidateReleaseId(releaseId);
        if (File.Exists(context.ManifestPath(releaseId)) || File.Exists(context.ManifestSignaturePath(releaseId)))
        {
            throw new PatchPublisherException("已建立 Manifest 的版本不可回到可修改的 Candidate；請改用取消版本並建立新 Release ID。");
        }

        string candidatePath = context.CandidatePath(releaseId);
        if (!Directory.Exists(candidatePath))
        {
            throw new PatchPublisherException($"找不到可捨棄的 Candidate：{releaseId}");
        }

        await Task.Run(() => DeleteCandidate(candidatePath), cancellationToken);
    }

    public async Task<CancelledRelease> CancelAsync(
        string releaseId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ValidateReleaseId(releaseId);
        context.EnsureDirectories();
        string markerPath = context.CancelledReleasePath(releaseId);
        if (File.Exists(markerPath))
        {
            await using FileStream existingStream = File.OpenRead(markerPath);
            CancelledRelease existing = await JsonSerializer.DeserializeAsync(
                existingStream,
                PatchJsonContext.Default.CancelledRelease,
                cancellationToken) ?? throw new PatchPublisherException($"取消版本紀錄格式錯誤：{markerPath}");
            string existingCandidatePath = context.CandidatePath(releaseId);
            if (Directory.Exists(existingCandidatePath))
            {
                await Task.Run(() => DeleteCandidate(existingCandidatePath), cancellationToken);
            }
            return existing;
        }

        foreach (string channel in new[] { "test", "stable" })
        {
            ChannelPayload? payload = await catalog.LoadLocalChannelAsync(channel, cancellationToken);
            if (string.Equals(payload?.ReleaseId, releaseId, StringComparison.Ordinal))
            {
                throw new PatchPublisherException(
                    $"{releaseId} 目前仍在 {channel} 頻道，不能直接取消；請建立更高版本的退版。");
            }
        }

        string candidatePath = context.CandidatePath(releaseId);
        bool hasCandidate = Directory.Exists(candidatePath);
        bool hasManifest = File.Exists(context.ManifestPath(releaseId)) &&
            File.Exists(context.ManifestSignaturePath(releaseId));
        if (!hasCandidate && !hasManifest)
        {
            throw new PatchPublisherException($"找不到可取消的 Candidate 或 Manifest：{releaseId}");
        }

        long contentVersion;
        string stage;
        if (hasManifest)
        {
            (ReleaseManifest manifest, _, _) = await catalog.LoadVerifiedAsync(releaseId, cancellationToken);
            contentVersion = manifest.ContentVersion;
            stage = "built";
        }
        else
        {
            string infoPath = Path.Combine(candidatePath, "release-info.json");
            await using FileStream stream = File.OpenRead(infoPath);
            ReleaseInfo info = await JsonSerializer.DeserializeAsync(
                stream,
                PatchJsonContext.Default.ReleaseInfo,
                cancellationToken) ?? throw new PatchPublisherException($"Candidate 資料格式錯誤：{releaseId}");
            contentVersion = info.ContentVersion;
            stage = "candidate";
        }

        CancelledRelease cancelled = new()
        {
            ReleaseId = releaseId,
            ContentVersion = contentVersion,
            Stage = stage,
            CancelledAt = DateTimeOffset.UtcNow,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(cancelled, PatchJsonContext.Default.CancelledRelease);
        await FileUtilities.WriteAtomicAsync(markerPath, bytes, cancellationToken);

        if (hasCandidate)
        {
            await Task.Run(() => DeleteCandidate(candidatePath), cancellationToken);
        }
        return cancelled;
    }

    private void DeleteCandidate(string candidatePath)
    {
        string root = Path.GetFullPath(context.CandidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(candidatePath);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatchPublisherException($"Candidate 路徑超出允許範圍：{target}");
        }
        Directory.Delete(target, true);
    }

    private static void ValidateReleaseId(string releaseId)
    {
        if (!ReleaseIdPattern().IsMatch(releaseId))
        {
            throw new PatchPublisherException("Release ID 格式必須是 yyyy.MM.dd.流水號。");
        }
    }

    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseIdPattern();
}
