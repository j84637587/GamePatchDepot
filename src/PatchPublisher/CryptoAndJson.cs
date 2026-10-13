using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GamePatchDepot;

public sealed class SigningService
{
    private readonly string _publicKeyPath;
    private readonly string? _privateKeyPath;

    public SigningService(string publicKeyPath, string? privateKeyPath = null)
    {
        _publicKeyPath = publicKeyPath;
        _privateKeyPath = privateKeyPath;
    }

    public static void GenerateKeyPair(string privateKeyPath, string publicKeyPath)
    {
        if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
        {
            throw new PatchPublisherException("目標密鑰檔已存在，為避免覆寫而停止。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateKeyPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicKeyPath))!);

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privateKeyPath, ecdsa.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        File.WriteAllText(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
    }

    public string Sign(ReadOnlySpan<byte> payload)
    {
        string privateKeyPath = _privateKeyPath
            ?? throw new PatchPublisherException("此操作需要簽章私鑰。請設定 PATCH_SIGNING_PRIVATE_KEY_FILE。");
        if (!File.Exists(privateKeyPath))
        {
            throw new PatchPublisherException($"找不到簽章私鑰：{privateKeyPath}");
        }

        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        return Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Verify(ReadOnlySpan<byte> payload, string signatureBase64)
    {
        if (!File.Exists(_publicKeyPath))
        {
            throw new PatchPublisherException($"找不到簽章公鑰：{_publicKeyPath}");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException exception)
        {
            throw new PatchPublisherException($"簽章不是有效的 Base64：{exception.Message}");
        }

        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(_publicKeyPath));
        if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new PatchPublisherException("簽章驗證失敗。");
        }
    }
}

public static class SignedEnvelopeService
{
    public static byte[] Create<T>(T payload, JsonTypeInfo<T> typeInfo, SigningService signer)
    {
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        SignedEnvelope envelope = new()
        {
            PayloadBase64 = Convert.ToBase64String(payloadBytes),
            SignatureBase64 = signer.Sign(payloadBytes)
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, PatchJsonContext.Default.SignedEnvelope);
    }

    public static T Open<T>(ReadOnlySpan<byte> envelopeBytes, JsonTypeInfo<T> typeInfo, SigningService signer)
    {
        SignedEnvelope envelope = JsonSerializer.Deserialize(envelopeBytes, PatchJsonContext.Default.SignedEnvelope)
            ?? throw new PatchPublisherException("Signed envelope 格式錯誤。");
        if (!string.Equals(envelope.Algorithm, "ECDSA-P256-SHA256", StringComparison.Ordinal))
        {
            throw new PatchPublisherException($"不支援的簽章演算法：{envelope.Algorithm}");
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
        }
        catch (FormatException exception)
        {
            throw new PatchPublisherException($"Envelope payload 不是有效的 Base64：{exception.Message}");
        }

        signer.Verify(payloadBytes, envelope.SignatureBase64);
        return JsonSerializer.Deserialize(payloadBytes, typeInfo)
            ?? throw new PatchPublisherException("Envelope payload 格式錯誤。");
    }
}

public static class FileUtilities
{
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, data.ToArray(), cancellationToken);
            File.Move(temporaryPath, path, false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task WriteAtomicTextAsync(string path, string text, CancellationToken cancellationToken = default) =>
        await WriteAtomicAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);

    public static async Task WriteReplaceAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, data.ToArray(), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
