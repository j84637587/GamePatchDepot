using System.Text.Json.Serialization;

namespace GamePatchDepot;

public sealed class ProductConfig
{
    public int SchemaVersion { get; init; } = 1;
    public required string Product { get; init; }
    public required string GameVersion { get; init; }
    public required string CandidateRoot { get; init; }
    public required string ChunkRoot { get; init; }
    public required string CatalogRoot { get; init; }
    public required string ReportRoot { get; init; }
    public required string Bucket { get; init; }
    public required string PublicBaseUrl { get; init; }
    public required string SigningPublicKeyFile { get; init; }
    public int ChunkSizeMiB { get; init; } = 256;
    public int KeepStableReleases { get; init; } = 3;
    public int OrphanGraceDays { get; init; } = 14;
    public int TemporaryRetentionDays { get; init; } = 7;
    public int OperationLeaseMinutes { get; init; } = 30;
    public int UploadConcurrency { get; init; } = 4;
    public bool EnableDeltaPatches { get; init; }
}

public sealed class AllowedFilesConfig
{
    public required List<string> Files { get; init; }
}

public sealed class ReleaseInfo
{
    public int SchemaVersion { get; init; } = 1;
    public List<string>? FullClientFiles { get; init; }
    public List<string> ExcludedFiles { get; init; } = [];
    public List<string>? Preserve { get; init; }
    public required string ReleaseId { get; init; }
    public required long ContentVersion { get; init; }
    public required string GameVersion { get; init; }
    public string? SourceDirectory { get; init; }
    public string MinimumLauncherVersion { get; init; } = "1.0.0";
    public required DateTimeOffset CreatedAt { get; init; }
    public string? BaseReleaseId { get; init; }
    public List<ReleaseChange> Changes { get; init; } = [];
    public List<string> Delete { get; init; } = [];
}

public sealed class ReleaseChange
{
    public required string Path { get; init; }
    public string? SourceRevision { get; init; }
    public string? Description { get; init; }
}

public sealed class CancelledRelease
{
    public int SchemaVersion { get; init; } = 1;
    public required string ReleaseId { get; init; }
    public required long ContentVersion { get; init; }
    public required string Stage { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }
    public string? Reason { get; init; }
}

public sealed class VerificationReport
{
    public int SchemaVersion { get; init; } = 2;
    public required string ReleaseId { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
    public required bool Deep { get; init; }
    public required string ManifestSha256 { get; init; }
}

public sealed class ReleaseManifest
{
    public int SchemaVersion { get; init; } = 1;
    public List<string>? Preserve { get; init; }
    public required string Product { get; init; }
    public required string ReleaseId { get; init; }
    public required long ContentVersion { get; init; }
    public required string GameVersion { get; init; }
    public required string MinimumLauncherVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required List<ManifestFile> Files { get; init; }
    public List<string> Delete { get; init; } = [];
}

public sealed class ManifestFile
{
    public required string Path { get; init; }
    public string? SourceRevision { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required List<ManifestChunk> Chunks { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileDeltaPatch? Patch { get; init; }
}

public sealed class FileDeltaPatch
{
    public string Format { get; init; } = "hdiffpatch-sf20-zstd";
    public required string BaseSha256 { get; init; }
    public required long BaseSize { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required string Key { get; init; }
}

public sealed class ManifestChunk
{
    public required long Offset { get; init; }
    public required int Size { get; init; }
    public required string Sha256 { get; init; }
    public required string Key { get; init; }
}

public sealed class BuildReport
{
    public required string ReleaseId { get; init; }
    public required DateTimeOffset BuiltAt { get; init; }
    public required int FileCount { get; init; }
    public required long TotalFileBytes { get; init; }
    public required int UniqueChunkCount { get; init; }
    public required long UniqueChunkBytes { get; init; }
    public required int NewLocalChunkCount { get; init; }
    public required long NewLocalChunkBytes { get; init; }
    public required string ManifestSha256 { get; init; }
    public int PatchCount { get; init; }
    public long PatchBytes { get; init; }
}

public sealed class DiffReport
{
    public required string ReleaseId { get; init; }
    public string? AgainstReleaseId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public List<FileDifference> Added { get; init; } = [];
    public List<FileDifference> Changed { get; init; } = [];
    public List<FileDifference> Removed { get; init; } = [];
    public List<string> Unchanged { get; init; } = [];
    public long EstimatedDownloadBytes { get; init; }
    public long NewUniqueChunkBytes { get; init; }
}

public sealed class FileDifference
{
    public required string Path { get; init; }
    public long? OldSize { get; init; }
    public long? NewSize { get; init; }
    public string? OldSha256 { get; init; }
    public string? NewSha256 { get; init; }
}

public sealed class SignedEnvelope
{
    public int SchemaVersion { get; init; } = 1;
    public string Algorithm { get; init; } = "ECDSA-P256-SHA256";
    public required string PayloadBase64 { get; init; }
    public required string SignatureBase64 { get; init; }
}

public sealed class ChannelPayload
{
    public int SchemaVersion { get; init; } = 1;
    public required string Channel { get; init; }
    public required long Generation { get; init; }
    public required string ReleaseId { get; init; }
    public required long ContentVersion { get; init; }
    public required string ManifestKey { get; init; }
    public required string ManifestSha256 { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

public sealed class OperationLeasePayload
{
    public int SchemaVersion { get; init; } = 1;
    public required string OperationId { get; init; }
    public required string Operation { get; init; }
    public required string Owner { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class GcReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string ReportId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required Dictionary<string, long> ChannelGenerations { get; init; }
    public required List<string> RootReleaseIds { get; init; }
    public required int ReferencedObjectCount { get; init; }
    public required List<GcCandidate> Candidates { get; init; }
    public DateTimeOffset? AppliedAt { get; set; }
    public int? DeletedObjectCount { get; set; }
    public long? DeletedBytes { get; set; }
}

public sealed class GcCandidate
{
    public required string Key { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastModified { get; init; }
}

public sealed record ObjectMetadata(string Key, long Size, DateTimeOffset LastModified, string ETag);

public class PatchPublisherException(string message) : Exception(message);

[JsonSerializable(typeof(ProductConfig))]
[JsonSerializable(typeof(AllowedFilesConfig))]
[JsonSerializable(typeof(ReleaseInfo))]
[JsonSerializable(typeof(CancelledRelease))]
[JsonSerializable(typeof(VerificationReport))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(BuildReport))]
[JsonSerializable(typeof(DiffReport))]
[JsonSerializable(typeof(SignedEnvelope))]
[JsonSerializable(typeof(ChannelPayload))]
[JsonSerializable(typeof(OperationLeasePayload))]
[JsonSerializable(typeof(GcReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class PatchJsonContext : JsonSerializerContext;
