using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace GamePatchDepot.Manager;

public partial class MainWindow
{
    private bool _runningLauncher;
    private bool _launcherValid;
    private bool _launcherUploaded;
    private string? _launcherVersion;
    private string? _launcherDigest;
    private string LauncherPathState => Path.Combine(_projectRoot, "work", "manager-launcher-path.txt");

    private void InitializeLauncherPane()
    {
        try
        {
            if (File.Exists(LauncherPathState)) LauncherBundleTextBox.Text = File.ReadAllText(LauncherPathState, Encoding.UTF8);
            using JsonDocument config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_projectRoot, "config", "product.json")));
            LauncherTargetText.Text = $"{config.RootElement.GetProperty("bucket").GetString()} / launcher/";
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException)
        { AppendLog($"啟動器畫面設定無法讀取：{error.Message}", true); }
        UpdateLauncherAvailability();
    }

    private void ResetLauncherSelection(string path)
    {
        _launcherValid = false;
        _launcherUploaded = false;
        _launcherVersion = null;
        _launcherDigest = null;
        LauncherBundleTextBox.Text = path;
        LauncherVersionTextBox.Text = "尚未驗證";
        LauncherHashTextBox.Clear();
        LauncherChangelogTextBox.Clear();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherPathState)!);
            File.WriteAllText(LauncherPathState, path, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AppendLog($"套件路徑未儲存：{error.Message}", true); }
        UpdateLauncherAvailability();
    }

    private void LauncherBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        OpenFolderDialog dialog = new() { Title = "選擇包含 launcher 子目錄的發布套件（例如 launcher-0.6.2）" };
        if (dialog.ShowDialog(this) == true) ResetLauncherSelection(dialog.FolderName);
    }

    private void LauncherClearButton_Click(object sender, RoutedEventArgs e)
    { if (!_busy) ResetLauncherSelection(string.Empty); }

    private void LauncherHelpButton_Click(object sender, RoutedEventArgs e) =>
        AppDialog.Information(this, "怎麼發布啟動器",
            "1. 選擇套件目錄，例如 launcher-0.6.2，裡面應有 launcher 子目錄。\n" +
            "2. 按「驗證套件」，版本、大小與更新說明會自動帶入。\n" +
            "3. 按「上傳並回驗」，核對 R2 檔案；此時 Stable 不變。\n" +
            "4. EXE 測試啟動正常後，按「發布 Stable」並確認。\n\n" +
            "重設／下一版只清除畫面，不刪檔。重開後須重新驗證。\n" +
            "取消發布不保證 Stable 未切換，請重新整理確認。\n" +
            "啟動器只發布完整 EXE，沒有 Test 或退版按鈕；修正時請提高版號。\n" +
            "新公鑰的首版必須手動安裝。此操作不變更遊戲頻道。");

    private async void LauncherVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _launcherValid = false;
        _launcherUploaded = false;
        _launcherValid = await RunCommandAsync("驗證啟動器套件", ["launcher-verify", "--bundle", LauncherBundleTextBox.Text]);
        UpdateLauncherAvailability();
    }

    private async void LauncherUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_launcherValid || _launcherDigest is null) return;
        _launcherUploaded = false;
        _launcherUploaded = await RunCommandAsync("啟動器上傳並回驗", ["launcher-upload", "--bundle", LauncherBundleTextBox.Text, "--expected-bundle", _launcherDigest]);
        if (!_launcherUploaded) _launcherValid = false;
        UpdateLauncherAvailability();
    }

    private async void LauncherPublishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_launcherUploaded || _launcherVersion is null || _launcherDigest is null) return;
        if (!AppDialog.Confirm(this, "正式發布啟動器",
            $"即將發布 NovaLink {_launcherVersion}。\n\n請確認此 EXE 已完成測試啟動。發布前會再次從 R2 下載核對，最後才切換 launcher/channels/stable.json。\n\n不變更遊戲頻道。新公鑰的首版必須手動安裝；Minisign 不等於 Windows 程式碼簽章。\n\n切換時若中斷，須重新整理確認結果，取消不會自動退版。", "已測試，正式發布", true)) return;
        LauncherRemoteText.Text = "發布結果待確認";
        await RunCommandAsync("正式發布啟動器", ["launcher-publish", "--bundle", LauncherBundleTextBox.Text, "--expected-bundle", _launcherDigest, "--confirm"]);
        _launcherUploaded = false;
        // Even cancellation may happen after the server committed the pointer.
        await RefreshLauncherStatusAsync();
        UpdateLauncherAvailability();
    }

    private async void LauncherRefreshButton_Click(object sender, RoutedEventArgs e)
    { if (!_busy) await RefreshLauncherStatusAsync(); }

    private async Task RefreshLauncherStatusAsync()
    {
        LauncherRemoteText.Text = "查詢中";
        if (!await RunCommandAsync("讀取啟動器 Stable", ["launcher-status"]))
            LauncherRemoteText.Text = "無法確認，請稍後重新整理";
    }

    private void ProcessLauncherOutputLine(string line)
    {
        if (line.StartsWith("launcher-stable:", StringComparison.Ordinal))
            LauncherRemoteText.Text = line[16..].Trim();
        if (!line.StartsWith("launcher-package:", StringComparison.Ordinal)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line[17..]);
            JsonElement value = document.RootElement;
            _launcherVersion = value.GetProperty("Version").GetString();
            _launcherDigest = value.GetProperty("BundleSha256").GetString();
            LauncherVersionTextBox.Text = $"{_launcherVersion} ／ {value.GetProperty("Size").GetInt64() / 1048576d:F2} MiB";
            LauncherHashTextBox.Text = value.GetProperty("Sha256").GetString();
            LauncherChangelogTextBox.Text = value.GetProperty("Changelog").GetString();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { AppendLog("無法讀取啟動器套件摘要，請重新驗證。", true); }
    }

    private void UpdateLauncherAvailability()
    {
        LauncherBrowseButton.IsEnabled = !_busy;
        LauncherClearButton.IsEnabled = !_busy;
        LauncherRefreshButton.IsEnabled = !_busy;
        LauncherVerifyButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(LauncherBundleTextBox.Text);
        LauncherUploadButton.IsEnabled = !_busy && _launcherValid && _launcherDigest is not null;
        LauncherPublishButton.IsEnabled = !_busy && _launcherValid && _launcherUploaded && _launcherDigest is not null;
    }
}
