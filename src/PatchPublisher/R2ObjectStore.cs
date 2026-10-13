using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace GamePatchDepot;

public sealed class R2Settings
{
    public required string Endpoint { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public required string Bucket { get; init; }

    public static R2Settings FromEnvironment(ProductConfig config)
    {
        string? endpoint = Environment.GetEnvironmentVariable("PATCH_R2_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            string accountId = RequireEnvironment("PATCH_R2_ACCOUNT_ID");
            endpoint = $"https://{accountId}.r2.cloudflarestorage.com";
        }

        return new R2Settings
        {
            Endpoint = endpoint.TrimEnd('/'),
            AccessKeyId = RequireEnvironment("PATCH_R2_ACCESS_KEY_ID"),
            SecretAccessKey = RequireEnvironment("PATCH_R2_SECRET_ACCESS_KEY"),
            Bucket = config.Bucket
        };
    }

    private static string RequireEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new PatchPublisherException($"缺少必要環境變數：{name}");
}

public sealed class R2ObjectStore : IAsyncDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    internal R2ObjectStore(IAmazonS3 client, string bucket)
    {
        _client = client;
        _bucket = bucket;
    }

    public R2ObjectStore(R2Settings settings)
    {
        BasicAWSCredentials credentials = new(settings.AccessKeyId, settings.SecretAccessKey);
        AmazonS3Config config = new()
        {
            ServiceURL = settings.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        };
        _client = new AmazonS3Client(credentials, config);
        _bucket = settings.Bucket;
    }

    public async Task<ObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            GetObjectMetadataResponse response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = key },
                cancellationToken);
            return new ObjectMetadata(
                key,
                response.ContentLength,
                response.LastModified is DateTime modified ? new DateTimeOffset(modified) : DateTimeOffset.MinValue,
                NormalizeEtag(response.ETag));
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task PutFileIfAbsentAsync(
        string key,
        string filePath,
        string contentType,
        string cacheControl,
        CancellationToken cancellationToken = default)
    {
        FileInfo file = new(filePath);
        ObjectMetadata? existing = await HeadAsync(key, cancellationToken);
        if (existing is not null)
        {
            if (existing.Size != file.Length)
            {
                throw new PatchPublisherException($"R2 已存在相同 Key 但大小不同：{key}");
            }
            return;
        }

        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        PutObjectRequest request = new()
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true,
            ContentType = contentType
        };
        request.Headers["Cache-Control"] = cacheControl;
        request.Headers["If-None-Match"] = "*";

        try
        {
            await _client.PutObjectAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            ObjectMetadata? raced = await HeadAsync(key, cancellationToken);
            if (raced is null || raced.Size != file.Length)
            {
                throw new PatchPublisherException($"R2 條件式上傳發生衝突：{key}");
            }
        }
    }

    public async Task<string> PutBytesConditionalAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string cacheControl,
        string? ifMatchEtag,
        bool ifNoneMatch,
        CancellationToken cancellationToken = default,
        Action<long, long>? transferProgress = null)
    {
        using MemoryStream stream = new(bytes.ToArray(), writable: false);
        PutObjectRequest request = new()
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true,
            ContentType = contentType
        };
        request.Headers["Cache-Control"] = cacheControl;
        if (ifNoneMatch)
        {
            request.Headers["If-None-Match"] = "*";
        }
        if (!string.IsNullOrWhiteSpace(ifMatchEtag))
        {
            request.Headers["If-Match"] = QuoteEtag(ifMatchEtag);
        }
        if (transferProgress is not null)
            request.StreamTransferProgress += (_, progress) => transferProgress(progress.TransferredBytes, progress.TotalBytes);

        try
        {
            PutObjectResponse response = await _client.PutObjectAsync(request, cancellationToken);
            return NormalizeEtag(response.ETag);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ConditionalWriteException(key);
        }
    }

    public async Task PutBytesImmutableAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string cacheControl,
        CancellationToken cancellationToken = default)
    {
        byte[]? existing = await GetBytesOrNullAsync(key, cancellationToken);
        if (existing is not null)
        {
            if (!existing.AsSpan().SequenceEqual(bytes.Span))
            {
                throw new PatchPublisherException($"R2 不可變物件已存在但內容不同：{key}");
            }
            return;
        }

        try
        {
            await PutBytesConditionalAsync(
                key,
                bytes,
                contentType,
                cacheControl,
                null,
                true,
                cancellationToken);
        }
        catch (ConditionalWriteException)
        {
            existing = await GetBytesOrNullAsync(key, cancellationToken);
            if (existing is null || !existing.AsSpan().SequenceEqual(bytes.Span))
            {
                throw new PatchPublisherException($"R2 不可變物件發生寫入衝突：{key}");
            }
        }
    }

    public async Task<byte[]?> GetBytesOrNullAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using GetObjectResponse response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            using MemoryStream memory = new();
            await response.ResponseStream.CopyToAsync(memory, cancellationToken);
            return memory.ToArray();
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task DownloadToStreamAsync(
        string key,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        using GetObjectResponse response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public async Task<byte[]?> GetBoundedBytesOrNullAsync(string key, int limit, CancellationToken token = default)
    {
        try
        {
            using GetObjectResponse response = await _client.GetObjectAsync(_bucket, key, token);
            if (response.ContentLength > limit) throw new PatchPublisherException($"R2 資料超過大小限制：{key}");
            using MemoryStream result = new();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await response.ResponseStream.ReadAsync(buffer, token)) > 0)
            {
                if (result.Length + count > limit) throw new PatchPublisherException($"R2 資料超過大小限制：{key}");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception)) { return null; }
    }

    public async Task<List<ObjectMetadata>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        List<ObjectMetadata> objects = [];
        string? continuationToken = null;
        do
        {
            ListObjectsV2Response response = await _client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                },
                cancellationToken);
            objects.AddRange((response.S3Objects ?? []).Select(item =>
                new ObjectMetadata(
                    item.Key,
                    item.Size ?? 0,
                    item.LastModified is DateTime modified ? new DateTimeOffset(modified) : DateTimeOffset.MinValue,
                    NormalizeEtag(item.ETag))));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return objects;
    }

    public async Task DeleteManyAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        foreach (string[] batch in keys.Chunk(1000))
        {
            DeleteObjectsResponse response = await _client.DeleteObjectsAsync(
                new DeleteObjectsRequest
                {
                    BucketName = _bucket,
                    Objects = batch.Select(key => new KeyVersion { Key = key }).ToList(),
                    Quiet = true
                },
                cancellationToken);
            if (response.DeleteErrors is { Count: > 0 })
            {
                string errors = string.Join(", ", response.DeleteErrors.Select(error => $"{error.Key}:{error.Code}"));
                throw new PatchPublisherException($"刪除 R2 物件失敗：{errors}");
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
        string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal);

    private static string NormalizeEtag(string? etag) => (etag ?? string.Empty).Trim('"');
    private static string QuoteEtag(string etag) => $"\"{NormalizeEtag(etag)}\"";
}

public sealed class ConditionalWriteException(string key)
    : PatchPublisherException($"條件式寫入失敗，物件已被其他操作修改：{key}");

public sealed class R2OperationLease : IAsyncDisposable
{
    public const string LeaseKey = "locks/production-operation.json";

    private readonly R2ObjectStore _store;
    private readonly TimeSpan _duration;
    private OperationLeasePayload _payload;
    private string _etag;
    private bool _released;

    private R2OperationLease(R2ObjectStore store, TimeSpan duration, OperationLeasePayload payload, string etag)
    {
        _store = store;
        _duration = duration;
        _payload = payload;
        _etag = etag;
    }

    public string OperationId => _payload.OperationId;

    public static async Task<R2OperationLease> AcquireAsync(
        R2ObjectStore store,
        string operation,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OperationLeasePayload payload = NewPayload(operation, duration, now);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, PatchJsonContext.Default.OperationLeasePayload);
        try
        {
            string etag = await store.PutBytesConditionalAsync(
                LeaseKey,
                bytes,
                "application/json",
                "no-store, no-cache, max-age=0",
                null,
                true,
                cancellationToken);
            return new R2OperationLease(store, duration, payload, etag);
        }
        catch (ConditionalWriteException)
        {
            // 檢查既有鎖是否已過期，再使用 ETag 進行有條件接管。
        }

        byte[] existingBytes = await store.GetBytesOrNullAsync(LeaseKey, cancellationToken)
            ?? throw new PatchPublisherException("操作鎖在讀取時消失，請重新執行。");
        ObjectMetadata existingMetadata = await store.HeadAsync(LeaseKey, cancellationToken)
            ?? throw new PatchPublisherException("操作鎖在讀取 Metadata 時消失，請重新執行。");
        OperationLeasePayload existing = JsonSerializer.Deserialize(existingBytes, PatchJsonContext.Default.OperationLeasePayload)
            ?? throw new PatchPublisherException("既有操作鎖格式錯誤，需人工檢查。");
        if (existing.ExpiresAt > now)
        {
            throw new PatchPublisherException(
                $"已有操作進行中：{existing.Operation}，擁有者 {existing.Owner}，到期時間 {existing.ExpiresAt:O}");
        }

        try
        {
            string etag = await store.PutBytesConditionalAsync(
                LeaseKey,
                bytes,
                "application/json",
                "no-store, no-cache, max-age=0",
                existingMetadata.ETag,
                false,
                cancellationToken);
            return new R2OperationLease(store, duration, payload, etag);
        }
        catch (ConditionalWriteException)
        {
            throw new PatchPublisherException("其他操作已接管過期鎖，請稍後重試。");
        }
    }

    public async Task RenewIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
        {
            throw new PatchPublisherException("操作鎖已釋放。");
        }
        if (_payload.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromTicks(_duration.Ticks / 2))
        {
            return;
        }

        await RenewAsync(cancellationToken);
    }

    public async Task EnsureHeldAsync(CancellationToken cancellationToken = default)
    {
        if (_released || _payload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new PatchPublisherException("操作鎖已失效。");
        }

        byte[] bytes = await _store.GetBytesOrNullAsync(LeaseKey, cancellationToken)
            ?? throw new PatchPublisherException("操作鎖不存在。");
        OperationLeasePayload current = JsonSerializer.Deserialize(bytes, PatchJsonContext.Default.OperationLeasePayload)
            ?? throw new PatchPublisherException("操作鎖格式錯誤。");
        if (!string.Equals(current.OperationId, _payload.OperationId, StringComparison.Ordinal) ||
            current.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new PatchPublisherException("操作鎖已被其他程序接管或已過期。");
        }
    }

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OperationLeasePayload renewed = new()
        {
            OperationId = _payload.OperationId,
            Operation = _payload.Operation,
            Owner = _payload.Owner,
            StartedAt = _payload.StartedAt,
            ExpiresAt = now.Add(_duration)
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(renewed, PatchJsonContext.Default.OperationLeasePayload);
        try
        {
            _etag = await _store.PutBytesConditionalAsync(
                LeaseKey,
                bytes,
                "application/json",
                "no-store, no-cache, max-age=0",
                _etag,
                false,
                cancellationToken);
            _payload = renewed;
        }
        catch (ConditionalWriteException)
        {
            throw new PatchPublisherException("操作鎖續租失敗，立即停止可變更操作。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }
        _released = true;

        // 過期後可能已有新擁有者，這時不可刪除鎖。
        if (_payload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return;
        }

        byte[]? bytes = await _store.GetBytesOrNullAsync(LeaseKey);
        if (bytes is null)
        {
            return;
        }
        OperationLeasePayload? current = JsonSerializer.Deserialize(bytes, PatchJsonContext.Default.OperationLeasePayload);
        if (current is not null && string.Equals(current.OperationId, _payload.OperationId, StringComparison.Ordinal))
        {
            await _store.DeleteAsync(LeaseKey);
        }
    }

    private static OperationLeasePayload NewPayload(string operation, TimeSpan duration, DateTimeOffset now) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"),
        Operation = operation,
        Owner = $"{Environment.MachineName}:{Environment.ProcessId}",
        StartedAt = now,
        ExpiresAt = now.Add(duration)
    };
}
