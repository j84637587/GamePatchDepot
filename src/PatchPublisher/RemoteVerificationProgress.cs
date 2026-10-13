using System.Globalization;

namespace GamePatchDepot;

public sealed record RemoteVerificationProgress(long BytesRead, long TotalBytes, int Permille, string Stage, string? FilePath)
{
    public string ToLogLine() => string.Create(CultureInfo.InvariantCulture,
        $"[{Permille}/1000] R2 深度驗證：{Permille / 10d:F1}%｜{Stage}｜{FormatBytes(BytesRead)} / {FormatBytes(TotalBytes)}{(FilePath is null ? "" : $"｜{FilePath}")}");

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):F2} GiB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):F2} MiB");
}
