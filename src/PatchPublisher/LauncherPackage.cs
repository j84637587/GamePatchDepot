using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace GamePatchDepot;

// Trust is compiled in, never supplied by the selected package. Keep in sync with NovaLink.
public sealed class LauncherPackage
{
    internal const string PublicKey = "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IDU5RUQ1ODFDQzA2MTJGRUYKUldUdkwySEFIRmp0V1J5TXoxRFhRRXlCNHhTT2lQQkh6QW0rZ3B0aHlhOG9VRzJObU96VVVOZloK";
    public const string ChannelKey = "launcher/channels/stable.json";
    public const int MetadataLimit = 64 * 1024;
    public const int SignatureLimit = 8 * 1024;
    public const int ExeLimit = 256 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _publicKey;
    private readonly Dictionary<string, byte[]> _files;
    private LauncherPackage(string publicKey, LauncherManifest manifest, byte[] channel, Dictionary<string, byte[]> files)
        => (_publicKey, Manifest, ChannelBytes, _files) = (publicKey, manifest, channel, files);

    public LauncherManifest Manifest { get; }
    internal byte[] ChannelBytes { get; }
    internal IReadOnlyDictionary<string, byte[]> Files => _files;
    public string Digest => FileUtilities.Sha256Hex(ChannelBytes);

    public static Task<LauncherPackage> LoadAsync(string directory, CancellationToken token = default) => LoadAsync(directory, PublicKey, token);
    internal static async Task<LauncherPackage> LoadAsync(string directory, string publicKey, CancellationToken token = default)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new PatchPublisherException("找不到套件資料夾，請重新選擇。");
        byte[] channel = await ReadFileAsync(root, ChannelKey, MetadataLimit, token);
        LauncherManifest manifest = ParseChannel(channel, publicKey);
        string prefix = $"launcher/releases/{manifest.Version}/";
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string key in new[] { manifest.Key, manifest.SignatureKey, prefix + "manifest.json", prefix + "manifest.json.sig" })
            files[key] = await ReadFileAsync(root, key, key.EndsWith(".exe", StringComparison.Ordinal) ? ExeLimit : key.EndsWith(".sig", StringComparison.Ordinal) ? SignatureLimit : MetadataLimit, token);
        using JsonDocument envelope = ParseJson(channel);
        byte[] payload = DecodeBase64(envelope.RootElement.GetProperty("payloadBase64").GetString()!);
        if (!payload.AsSpan().SequenceEqual(files[prefix + "manifest.json"]) ||
            envelope.RootElement.GetProperty("signature").GetString() != StrictUtf8.GetString(files[prefix + "manifest.json.sig"]).Trim())
            throw new PatchPublisherException("套件的頻道封套與 manifest.json／簽章不一致。");
        byte[] exe = files[manifest.Key];
        if (exe.LongLength != manifest.Size || FileUtilities.Sha256Hex(exe) != manifest.Sha256)
            throw new PatchPublisherException("啟動器 EXE 大小或 SHA-256 不符。");
        VerifySignature(exe, StrictUtf8.GetString(files[manifest.SignatureKey]), publicKey);
        token.ThrowIfCancellationRequested();
        return new LauncherPackage(publicKey, manifest, channel, files);
    }

    internal LauncherManifest ReadChannel(byte[] bytes) => ParseChannel(bytes, _publicKey);
    internal static LauncherManifest ParseChannel(byte[] bytes, string publicKey)
    {
        try
        {
            using JsonDocument envelope = ParseJson(bytes);
            JsonElement e = envelope.RootElement;
            if (e.GetProperty("schemaVersion").GetInt32() != 1 || e.GetProperty("algorithm").GetString() != "minisign")
                throw new PatchPublisherException("不支援的啟動器頻道格式。");
            byte[] payload = DecodeBase64(e.GetProperty("payloadBase64").GetString()!);
            VerifySignature(payload, e.GetProperty("signature").GetString()!, publicKey);
            using JsonDocument document = ParseJson(payload);
            JsonElement m = document.RootElement;
            string version = m.GetProperty("version").GetString()!;
            _ = VersionParts(version);
            string key = $"launcher/releases/{version}/NovaLink.exe";
            long size = m.GetProperty("size").GetInt64();
            string sha = m.GetProperty("sha256").GetString()!;
            if (m.GetProperty("schemaVersion").GetInt32() != 1 || m.GetProperty("product").GetString() != "NovaLink" ||
                m.GetProperty("channel").GetString() != "stable" || m.GetProperty("target").GetString() != "windows-x86_64" ||
                m.GetProperty("key").GetString() != key || m.GetProperty("signatureKey").GetString() != key + ".sig" ||
                size is <= 0 or > ExeLimit || !Regex.IsMatch(sha, "\\A[0-9a-f]{64}\\z"))
                throw new PatchPublisherException("啟動器產品、頻道、平台、路徑、大小或雜湊不符規範。");
            string publishedAt = m.GetProperty("publishedAt").GetString()!;
            if (!Regex.IsMatch(publishedAt, "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,9})?(?:Z|[+-][0-9]{2}:[0-9]{2})\\z"))
                throw new FormatException();
            _ = m.GetProperty("publishedAt").GetDateTimeOffset();
            return new(version, size, sha, key, key + ".sig", m.GetProperty("changelog").GetString() ?? throw new FormatException());
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw new PatchPublisherException("啟動器更新清單格式錯誤或欄位缺漏。");
        }
    }

    internal static JsonDocument ParseJson(byte[] bytes)
    {
        if (bytes.Length > MetadataLimit) throw new PatchPublisherException("啟動器清單超過 64 KiB。");
        JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new PatchPublisherException("啟動器清單必須是物件。"); }
        HashSet<string> names = new(StringComparer.Ordinal);
        if (document.RootElement.EnumerateObject().Any(p => !names.Add(p.Name)))
        { document.Dispose(); throw new PatchPublisherException("啟動器清單有重複欄位。"); }
        return document;
    }

    internal static int CompareVersions(string left, string right) => VersionParts(left).CompareTo(VersionParts(right));
    private static (ulong, ulong, ulong) VersionParts(string version)
    {
        if (version is null || !Regex.IsMatch(version, "\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z"))
            throw new PatchPublisherException("啟動器版本必須是正式版號，例如 0.6.2。");
        string[] p = version.Split('.');
        return (ulong.Parse(p[0], CultureInfo.InvariantCulture), ulong.Parse(p[1], CultureInfo.InvariantCulture), ulong.Parse(p[2], CultureInfo.InvariantCulture));
    }

    internal static void VerifySignature(byte[] data, string encodedSignature, string encodedKey)
    {
        try
        {
            if (encodedSignature is null || encodedSignature.Length > SignatureLimit) throw new FormatException();
            string[] keyLines = StrictUtf8.GetString(DecodeBase64(encodedKey.Trim())).Split('\n');
            string[] lines = StrictUtf8.GetString(DecodeBase64(encodedSignature.Trim())).Split('\n');
            byte[] key = DecodeBase64(keyLines[1].TrimEnd('\r'));
            byte[] signature = DecodeBase64(lines[1].TrimEnd('\r'));
            string comment = lines[2].TrimEnd('\r');
            byte[] global = DecodeBase64(lines[3].TrimEnd('\r'));
            if (key.Length != 42 || signature.Length != 74 || global.Length != 64 ||
                key[0] != 0x45 || (key[1] != 0x64 && key[1] != 0x44) ||
                signature[0] != 0x45 || signature[1] != 0x44 || !key.AsSpan(2, 8).SequenceEqual(signature.AsSpan(2, 8)) ||
                !comment.StartsWith("trusted comment: ", StringComparison.Ordinal)) throw new FormatException();
            Blake2bDigest digest = new(512);
            digest.BlockUpdate(data, 0, data.Length);
            byte[] hash = new byte[64];
            digest.DoFinal(hash, 0);
            Ed25519PublicKeyParameters publicKey = new(key, 10);
            static bool Check(Ed25519PublicKeyParameters key, byte[] message, byte[] sig)
            {
                Ed25519Signer verifier = new();
                verifier.Init(false, key);
                verifier.BlockUpdate(message, 0, message.Length);
                return verifier.VerifySignature(sig);
            }
            byte[] sig = signature[10..];
            byte[] trusted = [.. sig, .. StrictUtf8.GetBytes(comment[17..])];
            if (!Check(publicKey, hash, sig) || !Check(publicKey, trusted, global)) throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or ArgumentException or IndexOutOfRangeException)
        { throw new PatchPublisherException("啟動器 Minisign 簽章無效，或不是受信任的 NovaLink 金鑰。"); }
    }

    private static byte[] DecodeBase64(string encoded)
    {
        byte[] bytes = Convert.FromBase64String(encoded);
        if (Convert.ToBase64String(bytes) != encoded) throw new FormatException("非標準 Base64 編碼。");
        return bytes;
    }

    private static async Task<byte[]> ReadFileAsync(string root, string key, int limit, CancellationToken token)
    {
        string path = root;
        foreach (string part in key.Split('/'))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new PatchPublisherException("套件目錄不可使用符號連結或接合點。");
            path = Path.Combine(path, part);
        }
        if (!File.Exists(path)) throw new PatchPublisherException($"套件缺少 {key}；請選擇包含 launcher 子目錄的套件根目錄。");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new PatchPublisherException("套件檔案不可使用連結。");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new PatchPublisherException($"套件檔案超過限制：{key}");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }
}

public sealed record LauncherManifest(string Version, long Size, string Sha256, string Key, string SignatureKey, string Changelog);
