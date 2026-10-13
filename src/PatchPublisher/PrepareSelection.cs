using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePatchDepot;

// UI 與 CLI 共用同一份選取資料；UI 以連結原始碼使用，無須載入發布端依賴。
public sealed class PrepareSelection
{
    public int SchemaVersion { get; init; } = 1;
    public required string SourceDirectory { get; init; }
    public string? BaseReleaseId { get; init; }
    public string? BaseManifestSha256 { get; init; }
    public required string PolicySha256 { get; init; }
    public List<string> RequiredFiles { get; init; } = [];
    public List<SelectionFile> Files { get; init; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed class SelectionFile : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public bool Present { get; init; }
    public bool WasManaged { get; init; }
    public bool PolicyExcluded { get; init; }
    public bool RecommendedInclude { get; init; }
    public required string Reason { get; init; }
    public long Size { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public string? ReplacementPath { get; init; }
    private bool _include;
    private bool _remove;

    public bool Include
    {
        get => _include;
        set
        {
            _include = value;
            if (value) _remove = false;
            Notify();
        }
    }

    public bool Remove
    {
        get => _remove;
        set
        {
            _remove = value;
            if (value) _include = false;
            Notify();
        }
    }

    [JsonIgnore] public bool CanInclude => Present && !PolicyExcluded;
    [JsonIgnore] public bool CanRemove => WasManaged && !PolicyExcluded && !Include;
    [JsonIgnore] public string SizeText => Present ? $"{Size / 1048576d:N2} MiB" : "來源不存在";
    [JsonIgnore] public string ResultText => Include ? "收錄（Prepare 時比對內容）" :
        Remove ? "從新版移除" : WasManaged ? "停止追蹤，保留玩家檔案" : "不收錄";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
