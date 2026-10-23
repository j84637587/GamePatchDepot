using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GamePatchDepot.Manager;

public partial class MainWindow : Window
{
    private static readonly Regex ProgressPattern = new(@"^\[(?<current>\d+)/(?<total>\d+)\]", RegexOptions.Compiled);
    private static readonly Regex ContentVersionPattern = new(@"content (?<version>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly string _projectRoot;
    private readonly string _patchScript;
    private readonly StringBuilder _pendingLog = new();
    private DispatcherTimer? _logFlushTimer;
    private long _lastProgressUpdate;
    private bool _busy;
    private bool _channelStatusLoaded;
    private long _highestContentVersion;
    private long _testContentVersion;
    private long _stableContentVersion;
    private string? _deepVerifiedReleaseId;
    private string? _testReleaseId;
    private string? _stableReleaseId;
    private Process? _runningProcess;
    private bool _cancelRequested;
    private string? _runningCancelSignalPath;
    private bool _updatingFields;
    private bool _restoredInitialSelection;
    private ManagerState? _savedState;
    private int? _uploadConcurrency;

    public MainWindow()
    {
        InitializeComponent();
        _projectRoot = FindProjectRoot();
        _patchScript = Path.Combine(_projectRoot, "patch.ps1");
        InitializeLauncherPane();
        InitializeAnnouncementPane();
        _savedState = LoadManagerState();
        _updatingFields = true;
        ReleaseIdTextBox.Text = IsValidReleaseId(_savedState?.ReleaseId ?? string.Empty)
            ? _savedState!.ReleaseId
            : SuggestReleaseId();
        string defaultSource = @"F:\MSPrivate\NoLifeWzToNx\wz";
        if (!string.IsNullOrWhiteSpace(_savedState?.SourceDirectory) && Directory.Exists(_savedState.SourceDirectory))
        {
            SourceTextBox.Text = _savedState.SourceDirectory;
        }
        else if (Directory.Exists(defaultSource))
        {
            SourceTextBox.Text = defaultSource;
        }
        UiReplacementTextBox.Text = _savedState?.UiReplacementFile ?? string.Empty;
        DescriptionTextBox.Text = _savedState?.Description ?? string.Empty;
        UploadConcurrencyComboBox.ItemsSource = Enumerable.Range(1, 8).ToArray();
        try
        {
            _uploadConcurrency = UploadSettings.Load(Path.Combine(_projectRoot, "config", "product.json"));
            UploadConcurrencyComboBox.SelectedItem = _uploadConcurrency.Value;
        }
        catch (Exception exception)
        {
            AppendLog($"無法讀取同時上傳數，請檢查 config/product.json：{exception.Message}", true);
        }
        _updatingFields = false;

        string credentialFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamePatchDepot",
            "credentials.json");
        bool credentialsExist = File.Exists(credentialFile);
        CredentialStatusText.Text = credentialsExist ? "憑證已安全設定" : "尚未設定憑證";
        CredentialStatusText.Foreground = credentialsExist ? Brushes.DarkGreen : Brushes.DarkRed;

        Closing += MainWindow_Closing;
        Loaded += async (_, _) =>
        {
            await RefreshChannelStatusAsync("讀取頻道狀態");
            await RefreshAnnouncementsAsync();
        };
        UpdateActionAvailability();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshChannelStatusAsync("讀取頻道狀態");

    private void UploadConcurrencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFields || string.IsNullOrWhiteSpace(_projectRoot)) return;
        try
        {
            if (!_busy && UploadConcurrencyComboBox.SelectedItem is int value)
            {
                UploadSettings.Save(Path.Combine(_projectRoot, "config", "product.json"), value);
                _uploadConcurrency = value;
                AppendLog($"同時上傳數已儲存：{value} 個，下次 Upload 生效。");
            }
        }
        catch (Exception exception)
        {
            AppendLog($"同時上傳數儲存失敗：{exception.Message}", true);
            AppDialog.Error(this, "設定未儲存", "無法儲存同時上傳數，已恢復原值。請檢查 product.json 與檔案權限。");
        }
        finally
        {
            _updatingFields = true;
            UploadConcurrencyComboBox.SelectedItem = _uploadConcurrency;
            _updatingFields = false;
            UpdateActionAvailability();
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpWindow help = new() { Owner = this };
        help.ShowDialog();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_channelStatusLoaded) return;
        if (!AppDialog.Confirm(this, "重設並準備下一版",
            "將產生新的版本號，清空更新說明與選用替換檔，讓流程回到 Prepare。\n\n來源資料夾與上傳設定會保留。舊 Candidate、Manifest、發布紀錄及 R2 檔案都不會刪除；未完成版本也不會被取消。",
            "重設畫面")) return;
        ResetForNextRelease();
    }

    private void ResetForNextRelease()
    {
        if (_busy || !_channelStatusLoaded) return;
        _highestContentVersion = Math.Max(_highestContentVersion, GetHighestLocalContentVersion());
        _updatingFields = true;
        ReleaseIdTextBox.Text = SuggestReleaseId();
        ContentVersionTextBox.Text = (_highestContentVersion + 1).ToString(CultureInfo.InvariantCulture);
        DescriptionTextBox.Clear();
        UiReplacementTextBox.Clear();
        _updatingFields = false;
        _deepVerifiedReleaseId = null;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 0;
        OperationStatusText.Text = "已重設，請填寫更新說明後從 Prepare 開始。";
        AppendLog($"\n=== 準備下一版：{ReleaseIdTextBox.Text}／Content {ContentVersionTextBox.Text} ===\n只重設畫面，舊版本與檔案全部保留。");
        SaveManagerState();
        UpdateActionAvailability();
    }

    private void ReleaseIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFields || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }
        string releaseId = ReleaseIdTextBox.Text.Trim();
        long? existingContentVersion = TryGetReleaseContentVersion(releaseId);
        _updatingFields = true;
        ContentVersionTextBox.Text = existingContentVersion?.ToString(CultureInfo.InvariantCulture) ??
            (_channelStatusLoaded ? (_highestContentVersion + 1).ToString(CultureInfo.InvariantCulture) : string.Empty);
        _updatingFields = false;
        RestoreDeepVerificationState(releaseId);
        SaveManagerState();
        UpdateActionAvailability();
        ShowSelectedPublicationStatus();
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingFields && !string.IsNullOrWhiteSpace(_projectRoot))
        {
            SaveManagerState();
        }
    }

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new() { Title = "選擇完整客戶端根目錄（包含遊戲 EXE、DLL 與資源）" };
        if (dialog.ShowDialog(this) == true)
        {
            SourceTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseUiButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "選擇要替換為 UI.nx 的檔案",
            Filter = "NX 檔案 (*.nx)|*.nx|所有檔案 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            UiReplacementTextBox.Text = dialog.FileName;
        }
    }

    private async void PrepareButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReleaseInputs(out string releaseId, out string contentVersion) ||
            !ValidatePrepareInputs(releaseId))
        {
            return;
        }

        string minimumLauncherVersion = MinimumLauncherVersionTextBox.Text.Trim();
        List<string> arguments =
        [
            "prepare", "--full-client", "--release", releaseId,
            "--content-version", contentVersion,
            "--source", SourceTextBox.Text.Trim(),
            "--description", DescriptionTextBox.Text.Trim()
        ];
        if (minimumLauncherVersion.Length > 0)
        {
            arguments.Add("--minimum-launcher-version");
            arguments.Add(minimumLauncherVersion);
        }
        if (!string.IsNullOrWhiteSpace(UiReplacementTextBox.Text))
        {
            arguments.Add("--replace");
            arguments.Add($"UI.nx={UiReplacementTextBox.Text.Trim()}");
        }
        string? baseReleaseId = GetPublishedBaseReleaseId();
        if (baseReleaseId is not null)
        {
            string baseManifest = Path.Combine(_projectRoot, "catalog", "releases", $"{baseReleaseId}.json");
            string baseSignature = Path.Combine(_projectRoot, "catalog", "releases", $"{baseReleaseId}.sig");
            if (!File.Exists(baseManifest) || !File.Exists(baseSignature))
            {
                AppDialog.Warning(
                    this,
                    "缺少比較基準",
                    $"本機缺少目前發布版本 {baseReleaseId} 的 Manifest 或簽章，無法安全比較變更檔案。");
                return;
            }
            arguments.Add("--base");
            arguments.Add(baseReleaseId);
        }
        string selectionPath = Path.Combine(_projectRoot, "work", "selections", $"{Guid.NewGuid():N}.json");
        string source = SourceTextBox.Text.Trim();
        string replacement = UiReplacementTextBox.Text.Trim();
        string description = DescriptionTextBox.Text.Trim();
        List<string> previewArguments = ["preview", "--source", source, "--output", selectionPath];
        if (baseReleaseId is not null) previewArguments.AddRange(["--base", baseReleaseId]);
        if (replacement.Length > 0) previewArguments.AddRange(["--replace", $"UI.nx={replacement}"]);
        try
        {
            if (!await RunCommandAsync("掃描檔案，準備確認清單", previewArguments)) return;
            // 掃描期間若使用者改了欄位，不可拿舊清單建立不同來源的版本。
            if (SourceTextBox.Text.Trim() != source || UiReplacementTextBox.Text.Trim() != replacement ||
                ReleaseIdTextBox.Text.Trim() != releaseId || ContentVersionTextBox.Text.Trim() != contentVersion ||
                DescriptionTextBox.Text.Trim() != description || MinimumLauncherVersionTextBox.Text.Trim() != minimumLauncherVersion)
            {
                AppDialog.Warning(this, "欄位已變更", "掃描期間欄位有變更，請重新按 Prepare 確認清單。");
                return;
            }
            PrepareSelection selection = JsonSerializer.Deserialize<PrepareSelection>(
                File.ReadAllBytes(selectionPath), PrepareSelection.JsonOptions)
                ?? throw new InvalidOperationException("無法讀取檔案確認清單。");
            PrepareSelectionWindow dialog = new(selection) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                AppendLog("已取消檔案確認，尚未建立 Candidate。");
                OperationStatusText.Text = "已取消：檔案確認（未建立 Candidate）";
                return;
            }
            File.WriteAllBytes(selectionPath, JsonSerializer.SerializeToUtf8Bytes(selection, PrepareSelection.JsonOptions));
            arguments.AddRange(["--selection", selectionPath]);
            await RunCommandAsync("建立 Candidate", arguments);
        }
        catch (Exception exception)
        {
            AppendLog($"檔案確認失敗：{exception.Message}", true);
            AppDialog.Error(this, "檔案確認失敗", exception.Message);
        }
        finally
        {
            if (File.Exists(selectionPath)) File.Delete(selectionPath);
            UpdateActionAvailability();
        }
    }

    private void ShowSelectedPublicationStatus()
    {
        if (_busy || !_channelStatusLoaded) return;
        string releaseId = ReleaseIdTextBox.Text.Trim();
        if (string.Equals(_stableReleaseId, releaseId, StringComparison.Ordinal))
            OperationStatusText.Text = $"正式發布完成：{releaseId}；可按「重設／下一版」。";
        else if (string.Equals(_testReleaseId, releaseId, StringComparison.Ordinal))
            OperationStatusText.Text = $"已發布 Test：{releaseId}；等待實際遊戲測試。";
        else return;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 100;
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e) =>
        await ShowRollbackDialogAsync();

    private async Task ShowRollbackDialogAsync()
    {
        if (!_channelStatusLoaded)
        {
            AppDialog.Warning(this, "尚未取得版本資料", "請先重新整理頻道狀態。");
            return;
        }

        string newReleaseId = SuggestReleaseId();
        long newContentVersion = GetHighestLocalContentVersion() + 1;
        List<RollbackReleaseOption> releases = LoadRollbackReleaseOptions(newContentVersion);
        if (releases.Count == 0)
        {
            AppDialog.Warning(this, "無法建立退版", "本機 Catalog 沒有可用的舊版本。");
            return;
        }

        RollbackWindow dialog = new(releases, newReleaseId, newContentVersion) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedRelease is null)
        {
            return;
        }

        ReleaseIdTextBox.Text = newReleaseId;
        ContentVersionTextBox.Text = newContentVersion.ToString(CultureInfo.InvariantCulture);
        _deepVerifiedReleaseId = null;
        if (await RunCommandAsync(
            "建立退版",
            [
                "rollback",
                "--release", newReleaseId,
                "--content-version", newContentVersion.ToString(CultureInfo.InvariantCulture),
                "--to", dialog.SelectedRelease.ReleaseId
            ]))
        {
            AppDialog.Information(
                this,
                "退版建立完成",
                $"退版 {newReleaseId} 已建立。\n\n內容取自：{dialog.SelectedRelease.ReleaseId}\n下一步從「本機驗證」開始，再依序上傳、深度驗證與發布 Test。");
        }
        UpdateActionAvailability();
    }

    private async void CancelCurrentOperationButton_Click(object sender, RoutedEventArgs e)
    {
        Process? process = _runningProcess;
        if (!_busy || process is null || process.HasExited)
        {
            return;
        }
        if (!AppDialog.Confirm(
            this,
            "取消目前操作",
            "確定要中止目前操作嗎？\n\nPrepare／Build 的未完成暫存檔會自動清理。若正在 Upload 或 Promote，遠端請求可能已完成，取消後必須重新整理確認狀態。",
            "中止操作",
            dangerous: true))
        {
            return;
        }

        _cancelRequested = true;
        CancelCurrentOperationButton.IsEnabled = false;
        OperationStatusText.Text = "正在取消目前操作…";
        AppendLog("使用者要求取消目前操作。");
        try
        {
            if (_runningCancelSignalPath is not null)
            {
                File.WriteAllText(_runningCancelSignalPath, "cancel");
            }
            Task exitTask = process.WaitForExitAsync();
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
            Task completed = await Task.WhenAny(exitTask, timeoutTask);
            if (completed == timeoutTask && !process.HasExited)
            {
                AppendLog("操作在 10 秒內未回應取消訊號，改為強制終止。", true);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 程序剛好已經結束，RunCommandAsync 會依實際結束狀態處理。
        }
    }

    private async void RevertStepButton_Click(object sender, RoutedEventArgs e)
    {
        string releaseId = ReleaseIdTextBox.Text.Trim();
        if (!IsValidReleaseId(releaseId))
        {
            AppDialog.Warning(this, "無法回退", "Release ID 格式不正確。");
            return;
        }
        if (IsPublishedRelease(releaseId))
        {
            AppDialog.Warning(this, "需要建立退版", "此版本已進入 Test 或 Stable，頻道不能倒退。請建立一個更高版本的退版。");
            await ShowRollbackDialogAsync();
            return;
        }

        bool candidateExists = Directory.Exists(CandidatePath(releaseId));
        bool manifestExists = ManifestExists(releaseId);
        if (manifestExists)
        {
            AppDialog.Information(
                this,
                "Build 後不可直接回退",
                "Build 會產生不可變 Manifest，因此不能回到原 Candidate 覆寫同一個版本。\n\n若不再使用此版本，請按「完全取消版本」，再以新的 Release ID 建立。");
            return;
        }
        if (!candidateExists)
        {
            AppDialog.Warning(this, "沒有可回退項目", "目前版本還沒有可回退的 Candidate。");
            return;
        }

        if (!AppDialog.Confirm(
            this,
            "回退 Prepare",
            $"確定捨棄 Candidate {releaseId} 嗎？\n\n這會回到 Prepare 之前，Release ID 與 Content Version 可繼續使用。",
            "捨棄 Candidate",
            dangerous: true))
        {
            return;
        }
        await RunCommandAsync("回退 Prepare", ["discard-candidate", "--release", releaseId]);
        UpdateActionAvailability();
    }

    private async void CancelVersionButton_Click(object sender, RoutedEventArgs e)
    {
        string releaseId = ReleaseIdTextBox.Text.Trim();
        if (!IsValidReleaseId(releaseId))
        {
            AppDialog.Warning(this, "無法取消版本", "Release ID 格式不正確。");
            return;
        }
        if (IsPublishedRelease(releaseId))
        {
            AppDialog.Warning(this, "改用建立退版", "此版本已進入 Test 或 Stable，不能直接取消。接下來請選擇要恢復的舊版本。");
            await ShowRollbackDialogAsync();
            return;
        }
        if (!Directory.Exists(CandidatePath(releaseId)) && !ManifestExists(releaseId))
        {
            AppDialog.Warning(this, "沒有可取消版本", "找不到這個版本的 Candidate 或 Manifest。");
            return;
        }

        string extra = ManifestExists(releaseId)
            ? "Manifest 與簽章會保留作為不可變稽核紀錄；若曾 Upload，R2 孤兒 objects 會在 14 天後由 GC 清理。"
            : "Candidate 會刪除，並保留一份很小的取消紀錄。";
        if (!AppDialog.Confirm(
            this,
            "完全取消版本",
            $"確定完全取消版本 {releaseId} 嗎？\n\n{extra}\n\n這個 Release ID 與 Content Version 將永久保留，不會重複使用。",
            "完全取消版本",
            dangerous: true))
        {
            return;
        }

        if (await RunCommandAsync(
            "完全取消版本",
            ["cancel", "--release", releaseId, "--reason", "由管理 UI 取消"]))
        {
            _deepVerifiedReleaseId = null;
            _highestContentVersion = Math.Max(_highestContentVersion, GetHighestLocalContentVersion());
            ReleaseIdTextBox.Text = SuggestReleaseId();
            ContentVersionTextBox.Text = (_highestContentVersion + 1).ToString(CultureInfo.InvariantCulture);
        }
        UpdateActionAvailability();
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        string infoPath = Path.Combine(CandidatePath(ReleaseIdTextBox.Text.Trim()), "release-info.json");
        if (File.Exists(infoPath))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(infoPath));
            if (document.RootElement.TryGetProperty("delete", out JsonElement deleted) && deleted.GetArrayLength() > 0 &&
                !AppDialog.Confirm(this, "確認版本移除檔案",
                    $"這個版本將移除 {deleted.GetArrayLength()} 個原本受管的檔案。請確認 Prepare 日誌中的「移除」清單符合預期，再建立版本。\n\n這不會刪除來源目錄的檔案。",
                    "確認建立", dangerous: true))
                return;
        }
        await RunReleaseCommandAsync("建立 Manifest 與 chunks", "build");
    }

    private async void VerifyLocalButton_Click(object sender, RoutedEventArgs e) =>
        await RunReleaseCommandAsync("本機變更驗證", "verify", "--local", "--changed");

    private async void DiffButton_Click(object sender, RoutedEventArgs e) =>
        await RunReleaseCommandAsync("比較 Stable 差異", "diff", "--against", "stable");

    private async void UploadButton_Click(object sender, RoutedEventArgs e) =>
        await RunReleaseCommandAsync("上傳至 R2", "upload");

    private async void VerifyDeepButton_Click(object sender, RoutedEventArgs e)
    {
        _deepVerifiedReleaseId = null;
        UpdateActionAvailability();
        if (await RunReleaseCommandAsync("R2 深度驗證", "verify", "--deep"))
        {
            RestoreDeepVerificationState(ReleaseIdTextBox.Text.Trim());
            UpdateActionAvailability();
        }
    }

    private async void PromoteTestButton_Click(object sender, RoutedEventArgs e)
    {
        string releaseId = ReleaseIdTextBox.Text.Trim();
        if (!string.Equals(_deepVerifiedReleaseId, releaseId, StringComparison.Ordinal))
        {
            AppDialog.Warning(this, "尚未完成深度驗證", "請先完成這個版本的 R2 深度驗證。");
            return;
        }
        if (await RunReleaseCommandAsync("發布至 Test", "promote", "--channel", "test"))
        {
            await RefreshChannelStatusAsync("重新整理頻道狀態");
        }
    }

    private async void PromoteStableButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetReleaseInputs(out string releaseId, out _))
        {
            return;
        }
        if (!string.Equals(_deepVerifiedReleaseId, releaseId, StringComparison.Ordinal))
        {
            AppDialog.Warning(this, "尚未完成深度驗證", "請先完成這個版本的 R2 深度驗證。");
            return;
        }
        if (!string.Equals(_testReleaseId, releaseId, StringComparison.Ordinal))
        {
            AppDialog.Warning(this, "Test 版本不一致", $"目前 Test 是 {_testReleaseId ?? "尚未發布"}，必須先將相同版本發布到 Test。");
            return;
        }

        StableConfirmationWindow confirmation = new(releaseId) { Owner = this };
        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        if (await RunReleaseCommandAsync("發布至 Stable", "promote", "--channel", "stable"))
        {
            await RefreshChannelStatusAsync("重新整理頻道狀態");
        }
    }

    private async Task RefreshChannelStatusAsync(string title)
    {
        _channelStatusLoaded = false;
        _highestContentVersion = 0;
        _testContentVersion = 0;
        _stableContentVersion = 0;
        ContentVersionTextBox.Text = string.Empty;
        UpdateActionAvailability();

        if (await RunCommandAsync(title, ["status"]))
        {
            _channelStatusLoaded = true;
            _highestContentVersion = Math.Max(_highestContentVersion, GetHighestLocalContentVersion());
            if (!_restoredInitialSelection)
            {
                RestoreInitialSelection();
                _restoredInitialSelection = true;
            }
            else
            {
                SetSelectedContentVersion();
            }
            UpdateActionAvailability();
            ShowSelectedPublicationStatus();
        }
    }

    private async Task<bool> RunReleaseCommandAsync(string title, string command, params string[] extraArguments)
    {
        if (!TryGetReleaseInputs(out string releaseId, out _))
        {
            return false;
        }
        List<string> arguments = [command, "--release", releaseId];
        arguments.AddRange(extraArguments);
        return await RunCommandAsync(title, arguments);
    }

    private bool TryGetReleaseInputs(out string releaseId, out string contentVersion)
    {
        releaseId = ReleaseIdTextBox.Text.Trim();
        contentVersion = ContentVersionTextBox.Text.Trim();
        if (!Regex.IsMatch(releaseId, @"^\d{4}\.\d{2}\.\d{2}\.\d+$") ||
            !long.TryParse(contentVersion, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ||
            parsed <= 0)
        {
            AppDialog.Warning(this, "版本資料錯誤", "Release ID 格式必須是 yyyy.MM.dd.流水號；Content Version 必須是大於 0 的整數。");
            return false;
        }
        long? existingContentVersion = TryGetReleaseContentVersion(releaseId);
        if (existingContentVersion is not null && parsed != existingContentVersion)
        {
            AppDialog.Warning(this, "版本資料不一致", $"此版本的 Content Version 是 {existingContentVersion}，不能改成 {parsed}。");
            return false;
        }
        if (existingContentVersion is null && parsed <= _highestContentVersion)
        {
            AppDialog.Warning(this, "版本號不可降低", $"Content Version 必須大於目前頻道版本 {_highestContentVersion}。");
            return false;
        }
        return true;
    }

    private async Task<bool> RunCommandAsync(string title, IReadOnlyList<string> arguments)
    {
        if (_busy)
        {
            return false;
        }

        bool succeeded = false;
        bool wasCancelled = false;
        _runningLauncher = arguments.Count > 0 && (arguments[0].StartsWith("launcher-", StringComparison.Ordinal) ||
            arguments[0].StartsWith("announcement-", StringComparison.Ordinal));
        OperationArtifactsSnapshot snapshot = _runningLauncher
            ? new([], [], false, false, false, false) : CaptureOperationArtifacts(arguments);
        _cancelRequested = false;
        string cancelSignalDirectory = Path.Combine(_projectRoot, "work", "cancel-signals");
        Directory.CreateDirectory(cancelSignalDirectory);
        _runningCancelSignalPath = Path.Combine(cancelSignalDirectory, $"{Guid.NewGuid():N}.signal");
        SetBusy(true, title);
        AppendLog($"\n=== {title} ===");
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                WorkingDirectory = _projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.Environment.Remove("PSModulePath");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_patchScript);
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.ArgumentList.Add("--cancel-file");
            startInfo.ArgumentList.Add(_runningCancelSignalPath);

            using Process process = new() { StartInfo = startInfo };
            _runningProcess = process;
            process.Start();
            CancelCurrentOperationButton.IsEnabled = true;
            Task stdout = PumpOutputAsync(process.StandardOutput, false);
            Task stderr = PumpOutputAsync(process.StandardError, true);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
            if (_cancelRequested)
            {
                wasCancelled = true;
                OperationStatusText.Text = $"已取消：{title}";
                OperationProgressBar.IsIndeterminate = false;
                OperationProgressBar.Value = 0;
                AppendLog($"已取消：{title}");
            }
            else if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"命令執行失敗，結束碼：{process.ExitCode}");
            }
            if (!wasCancelled)
            {
                OperationStatusText.Text = $"完成：{title}";
                OperationProgressBar.IsIndeterminate = false;
                OperationProgressBar.Value = 100;
                succeeded = true;
            }
        }
        catch (Exception exception)
        {
            if (_cancelRequested)
            {
                wasCancelled = true;
                OperationStatusText.Text = $"已取消：{title}";
                AppendLog($"已取消：{title}");
            }
            else
            {
                OperationStatusText.Text = $"失敗：{title}";
                AppendLog($"錯誤：{exception.Message}", true);
                AppDialog.Error(this, "操作失敗", exception.Message);
            }
        }
        finally
        {
            _runningProcess = null;
            if (_runningCancelSignalPath is not null && File.Exists(_runningCancelSignalPath))
            {
                File.Delete(_runningCancelSignalPath);
            }
            _runningCancelSignalPath = null;
            if (wasCancelled && !_runningLauncher)
            {
                CleanupCancelledOperation(arguments, snapshot);
            }
            _cancelRequested = false;
            _runningLauncher = false;
            SetBusy(false, OperationStatusText.Text);
        }
        if (wasCancelled && arguments.Count > 0 &&
            string.Equals(arguments[0], "promote", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshChannelStatusAsync("確認取消後頻道狀態");
        }
        return succeeded;
    }

    private async Task PumpOutputAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Input and rendering must run before another buffered output line.
            await Dispatcher.InvokeAsync(() => ProcessOutputLine(line, isError), DispatcherPriority.Background)
                .Task.ConfigureAwait(false);
        }
        // Drain the last partial batch before reporting completion or failure.
        await Dispatcher.InvokeAsync(FlushPendingLog, DispatcherPriority.Background).Task.ConfigureAwait(false);
    }

    private void ProcessOutputLine(string line, bool isError)
    {
        AppendLog(line, isError);
        Match progress = ProgressPattern.Match(line);
        if (progress.Success &&
            int.TryParse(progress.Groups["current"].Value, out int current) &&
            int.TryParse(progress.Groups["total"].Value, out int total) && total > 0)
        {
            if (current == total || Stopwatch.GetElapsedTime(_lastProgressUpdate).TotalMilliseconds >= 100)
            {
                _lastProgressUpdate = Stopwatch.GetTimestamp();
                OperationProgressBar.IsIndeterminate = false;
                OperationProgressBar.Value = current * 100d / total;
                string detail = line[progress.Length..].Trim();
                OperationStatusText.Text = detail.StartsWith("R2 深度驗證：", StringComparison.Ordinal) || detail.StartsWith("啟動器：", StringComparison.Ordinal)
                    ? detail : $"處理中：{current} / {total}";
            }
        }

        if (_runningLauncher)
        {
            ProcessLauncherOutputLine(line);
            return;
        }
        if (line.StartsWith("test:", StringComparison.OrdinalIgnoreCase))
        {
            TestStatusText.Text = line[5..].Trim();
            _testReleaseId = ParseChannelReleaseId(line);
            _testContentVersion = ParseContentVersion(line);
        }
        else if (line.StartsWith("stable:", StringComparison.OrdinalIgnoreCase))
        {
            StableStatusText.Text = line[7..].Trim();
            _stableReleaseId = ParseChannelReleaseId(line);
            _stableContentVersion = ParseContentVersion(line);
        }

        Match contentVersion = ContentVersionPattern.Match(line);
        if (contentVersion.Success && long.TryParse(contentVersion.Groups["version"].Value, out long version))
        {
            _highestContentVersion = Math.Max(_highestContentVersion, version);
        }
        UpdateActionAvailability();
    }

    private void AppendLog(string text, bool isError = false)
    {
        _pendingLog.Append(isError ? "[錯誤] " : string.Empty).AppendLine(text);
        if (_logFlushTimer is null)
        {
            _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _logFlushTimer.Tick += (_, _) => FlushPendingLog();
        }
        _logFlushTimer.Start();
    }

    private void FlushPendingLog()
    {
        _logFlushTimer?.Stop();
        if (_pendingLog.Length == 0) return;
        string batch = _pendingLog.ToString();
        _pendingLog.Clear();
        // Append only the new batch; never bind one TextBox's entire Text to another.
        LogTextBox.AppendText(batch);
        LauncherLogTextBox.AppendText(batch);
        LogTextBox.ScrollToEnd();
        LauncherLogTextBox.ScrollToEnd();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        UpdateLauncherAvailability();
        AnnouncementPane.IsEnabled = !busy;
        ActionPanel.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy && _channelStatusLoaded;
        UploadConcurrencyComboBox.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy && _channelStatusLoaded;
        CancelCurrentOperationButton.IsEnabled = busy && _runningProcess is not null;
        RevertStepButton.IsEnabled = false;
        CancelVersionButton.IsEnabled = false;
        OperationStatusText.Text = status;
        if (busy)
        {
            OperationProgressBar.Value = 0;
            OperationProgressBar.IsIndeterminate = true;
        }
        else
        {
            UpdateActionAvailability();
        }
    }

    private bool ValidatePrepareInputs(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(DescriptionTextBox.Text))
        {
            AppDialog.Warning(this, "資料不完整", "更新說明不可空白。");
            return false;
        }
        string source = SourceTextBox.Text.Trim();
        if (!Directory.Exists(source))
        {
            AppDialog.Warning(this, "來源目錄錯誤", $"找不到完整客戶端來源目錄：{source}");
            return false;
        }
        // 遞迴收錄、排除規則與必要檔案檢查由發布核心統一執行。
        string replacement = UiReplacementTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(replacement) &&
            (!File.Exists(replacement) || !string.Equals(Path.GetExtension(replacement), ".nx", StringComparison.OrdinalIgnoreCase)))
        {
            AppDialog.Warning(this, "替換檔錯誤", "UI 替換檔必須是存在的 .nx 檔案。");
            return false;
        }
        if (Directory.Exists(Path.Combine(_projectRoot, "work", "candidates", releaseId)) ||
            File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json")))
        {
            AppDialog.Warning(this, "版本已存在", $"Release ID {releaseId} 已存在，不可重複使用。");
            return false;
        }
        return true;
    }

    private void UpdateActionAvailability()
    {
        if (!IsInitialized || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }
        string releaseId = ReleaseIdTextBox.Text.Trim();
        bool validId = Regex.IsMatch(releaseId, @"^\d{4}\.\d{2}\.\d{2}\.\d+$");
        bool candidateExists = validId && Directory.Exists(Path.Combine(_projectRoot, "work", "candidates", releaseId));
        bool manifestExists = validId &&
            File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json")) &&
            File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.sig"));
        bool cancelled = validId && IsReleaseCancelled(releaseId);
        bool published = validId && IsPublishedRelease(releaseId);
        bool currentTest = validId && string.Equals(_testReleaseId, releaseId, StringComparison.Ordinal);
        bool currentStable = validId && string.Equals(_stableReleaseId, releaseId, StringComparison.Ordinal);
        long selectedVersion = validId ? TryGetReleaseContentVersion(releaseId) ?? 0 : 0;
        string metadataPath = candidateExists
            ? Path.Combine(CandidatePath(releaseId), "release-info.json")
            : Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json");
        ReleaseScopeText.Text = "新版本：完整客戶端。Candidate 只保存變更檔案，包含 EXE、DLL、NX 與子目錄。";
        if (validId && File.Exists(metadataPath))
        {
            try
            {
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
                bool fullClient = metadata.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) && schema.GetInt32() == 2;
                ReleaseScopeText.Text = fullClient
                    ? "目前版本：完整客戶端。Candidate 只保存變更檔案，Manifest 記錄完整內容。"
                    : "目前版本：舊版 NX。仍可繼續原流程；新建版本將使用完整客戶端模式。";
            }
            catch (JsonException) { ReleaseScopeText.Text = "版本資料無法讀取，請檢查 Candidate／Manifest。"; }
        }
        PrepareButton.IsEnabled = !_busy && _channelStatusLoaded && validId && !candidateExists && !manifestExists && !cancelled;
        ResetButton.IsEnabled = !_busy && _channelStatusLoaded;
        RollbackButton.IsEnabled = !_busy && _channelStatusLoaded;
        BuildButton.IsEnabled = !_busy && candidateExists && !manifestExists && !cancelled;
        VerifyLocalButton.IsEnabled = !_busy && manifestExists && !cancelled;
        DiffButton.IsEnabled = !_busy && manifestExists && !cancelled;
        UploadButton.IsEnabled = !_busy && manifestExists && !cancelled && _uploadConcurrency.HasValue;
        VerifyDeepButton.IsEnabled = !_busy && manifestExists && !cancelled;
        PromoteTestButton.Content = currentTest ? "7. Test 已發布" : "7. 發布 Test";
        PromoteStableButton.Content = currentStable ? "8. Stable 已發布" : "8. 發布 Stable";
        PromoteTestButton.IsEnabled = !_busy && _channelStatusLoaded && manifestExists && !cancelled && !currentTest && !currentStable &&
            selectedVersion > _testContentVersion && selectedVersion > _stableContentVersion &&
            string.Equals(_deepVerifiedReleaseId, releaseId, StringComparison.Ordinal);
        PromoteStableButton.IsEnabled = !_busy && _channelStatusLoaded && manifestExists && !cancelled && selectedVersion > _stableContentVersion &&
            string.Equals(_deepVerifiedReleaseId, releaseId, StringComparison.Ordinal) &&
            string.Equals(_testReleaseId, releaseId, StringComparison.Ordinal) &&
            !string.Equals(_stableReleaseId, releaseId, StringComparison.Ordinal);
        RevertStepButton.IsEnabled = !_busy && _channelStatusLoaded && !cancelled && !published && candidateExists && !manifestExists;
        CancelVersionButton.IsEnabled = !_busy && _channelStatusLoaded && !cancelled && !published && (candidateExists || manifestExists);
        CancelCurrentOperationButton.IsEnabled = _busy && _runningProcess is not null;
    }

    private string CandidatePath(string releaseId) =>
        Path.Combine(_projectRoot, "work", "candidates", releaseId);

    private bool ManifestExists(string releaseId) =>
        File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json")) &&
        File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.sig"));

    private bool IsReleaseCancelled(string releaseId) =>
        File.Exists(Path.Combine(_projectRoot, "catalog", "cancelled", $"{releaseId}.json"));

    private bool IsPublishedRelease(string releaseId) =>
        string.Equals(_testReleaseId, releaseId, StringComparison.Ordinal) ||
        string.Equals(_stableReleaseId, releaseId, StringComparison.Ordinal) || HasPublishReport(releaseId);

    private bool HasPublishReport(string releaseId, string channel = "*")
    {
        string directory = Path.Combine(_projectRoot, "reports", "publishes");
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, $"{channel}-*-{releaseId}.json").Any();
    }

    private static bool IsValidReleaseId(string releaseId) =>
        Regex.IsMatch(releaseId, @"^\d{4}\.\d{2}\.\d{2}\.\d+$");

    private OperationArtifactsSnapshot CaptureOperationArtifacts(IReadOnlyList<string> arguments)
    {
        string? releaseId = GetArgumentValue(arguments, "--release");
        string stagingRoot = Path.Combine(_projectRoot, "work", "staging");
        string chunkRoot = Path.Combine(_projectRoot, "work", "chunks");
        HashSet<string> stagingDirectories = Directory.Exists(stagingRoot)
            ? Directory.EnumerateDirectories(stagingRoot).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> temporaryChunks = Directory.Exists(chunkRoot)
            ? Directory.EnumerateFiles(chunkRoot, ".*.tmp", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new OperationArtifactsSnapshot(
            stagingDirectories,
            temporaryChunks,
            releaseId is not null && Directory.Exists(CandidatePath(releaseId)),
            releaseId is not null && File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json")),
            releaseId is not null && File.Exists(Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.sig")),
            releaseId is not null && File.Exists(Path.Combine(_projectRoot, "reports", "builds", $"{releaseId}.json")));
    }

    private void CleanupCancelledOperation(
        IReadOnlyList<string> arguments,
        OperationArtifactsSnapshot snapshot)
    {
        try
        {
            string command = arguments.Count > 0 ? arguments[0] : string.Empty;
            string? releaseId = GetArgumentValue(arguments, "--release");
            string stagingRoot = Path.Combine(_projectRoot, "work", "staging");
            if (releaseId is not null && IsValidReleaseId(releaseId) && Directory.Exists(stagingRoot))
            {
                foreach (string path in Directory.EnumerateDirectories(stagingRoot).Select(Path.GetFullPath))
                {
                    string name = Path.GetFileName(path);
                    if (name.StartsWith($"prepare-{releaseId}-", StringComparison.OrdinalIgnoreCase) &&
                        !snapshot.StagingDirectories.Contains(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
            }

            string chunkRoot = Path.Combine(_projectRoot, "work", "chunks");
            if (Directory.Exists(chunkRoot))
            {
                foreach (string path in Directory.EnumerateFiles(chunkRoot, ".*.tmp", SearchOption.TopDirectoryOnly)
                             .Select(Path.GetFullPath))
                {
                    if (!snapshot.TemporaryChunks.Contains(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            if (releaseId is not null && IsValidReleaseId(releaseId))
            {
                if (string.Equals(command, "prepare", StringComparison.OrdinalIgnoreCase) &&
                    !snapshot.CandidateExisted && Directory.Exists(CandidatePath(releaseId)))
                {
                    Directory.Delete(CandidatePath(releaseId), true);
                }
                if ((string.Equals(command, "build", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(command, "rollback", StringComparison.OrdinalIgnoreCase)))
                {
                    DeleteIfNew(
                        Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json"),
                        snapshot.ManifestExisted);
                    DeleteIfNew(
                        Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.sig"),
                        snapshot.SignatureExisted);
                    DeleteIfNew(
                        Path.Combine(_projectRoot, "reports", "builds", $"{releaseId}.json"),
                        snapshot.BuildReportExisted);
                }
            }
            AppendLog("已清除本次取消操作留下的暫存檔。");
        }
        catch (Exception exception)
        {
            AppendLog($"取消後清理失敗：{exception.Message}", true);
        }
    }

    private static string? GetArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }

    private static void DeleteIfNew(string path, bool existedBefore)
    {
        if (!existedBefore && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string? ParseChannelReleaseId(string line)
    {
        string value = line[(line.IndexOf(':') + 1)..].Trim();
        if (value.StartsWith("尚未發布", StringComparison.Ordinal))
        {
            return null;
        }
        int separator = value.IndexOf(" / ", StringComparison.Ordinal);
        return separator > 0 ? value[..separator] : value;
    }

    private static long ParseContentVersion(string line)
    {
        Match match = ContentVersionPattern.Match(line);
        return match.Success && long.TryParse(match.Groups["version"].Value, out long value) ? value : 0;
    }

    private string? GetPublishedBaseReleaseId()
    {
        if (_testContentVersion == 0 && _stableContentVersion == 0)
        {
            return null;
        }
        return _testContentVersion >= _stableContentVersion ? _testReleaseId : _stableReleaseId;
    }

    private long GetHighestLocalContentVersion()
    {
        long highest = Math.Max(_testContentVersion, _stableContentVersion);
        string releaseDirectory = Path.Combine(_projectRoot, "catalog", "releases");
        if (Directory.Exists(releaseDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(releaseDirectory, "*.json"))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (document.RootElement.TryGetProperty("contentVersion", out JsonElement value) &&
                        value.TryGetInt64(out long contentVersion))
                    {
                        highest = Math.Max(highest, contentVersion);
                    }
                }
                catch (JsonException)
                {
                    // Publisher 會在真正操作前驗證 Manifest；UI 只忽略無法讀取的清單。
                }
            }
        }
        string candidateDirectory = Path.Combine(_projectRoot, "work", "candidates");
        if (Directory.Exists(candidateDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(candidateDirectory, "release-info.json", SearchOption.AllDirectories))
            {
                highest = Math.Max(highest, ReadContentVersion(path) ?? 0);
            }
        }
        string cancelledDirectory = Path.Combine(_projectRoot, "catalog", "cancelled");
        if (Directory.Exists(cancelledDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(cancelledDirectory, "*.json"))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (document.RootElement.TryGetProperty("contentVersion", out JsonElement value) &&
                        value.TryGetInt64(out long contentVersion))
                    {
                        highest = Math.Max(highest, contentVersion);
                    }
                }
                catch (JsonException)
                {
                    // 取消紀錄損壞時不在 UI 猜測內容，後端操作仍會拒絕不合法資料。
                }
            }
        }
        return highest;
    }

    private List<RollbackReleaseOption> LoadRollbackReleaseOptions(long newContentVersion)
    {
        List<RollbackReleaseOption> releases = [];
        string releaseDirectory = Path.Combine(_projectRoot, "catalog", "releases");
        if (!Directory.Exists(releaseDirectory))
        {
            return releases;
        }

        string gameExecutable;
        try
        {
            using JsonDocument config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_projectRoot, "config", "product.json")));
            gameExecutable = config.RootElement.TryGetProperty("gameExecutable", out JsonElement executable)
                ? executable.ValueKind == JsonValueKind.String ? executable.GetString()! : throw new InvalidDataException("gameExecutable 必須是字串。")
                : "NovaStory.exe";
            gameExecutable = gameExecutable.Replace('\\', '/').Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppendLog($"無法讀取遊戲主程式設定，暫不提供退版目標：{exception.Message}", true);
            return releases;
        }

        foreach (string path in Directory.EnumerateFiles(releaseDirectory, "*.json"))
        {
            string releaseId = Path.GetFileNameWithoutExtension(path);
            if (IsReleaseCancelled(releaseId))
            {
                continue;
            }
            if (!File.Exists(Path.Combine(releaseDirectory, $"{releaseId}.sig")))
            {
                continue;
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (!document.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array ||
                    !files.EnumerateArray().Any(file => file.ValueKind == JsonValueKind.Object &&
                        file.TryGetProperty("path", out JsonElement entryPath) && entryPath.ValueKind == JsonValueKind.String &&
                        string.Equals(entryPath.GetString(), gameExecutable, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (!document.RootElement.TryGetProperty("launcherTicketProtocol", out JsonElement protocol) ||
                    protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 2)
                {
                    continue;
                }
                if (!document.RootElement.TryGetProperty("minimumLauncherVersion", out JsonElement minimum) ||
                    minimum.ValueKind != JsonValueKind.String || minimum.GetString() is not string minimumText ||
                    minimumText.Split('.').Length != 3 || !Version.TryParse(minimumText, out Version? minimumVersion) ||
                    minimumVersion.ToString() != minimumText || minimumVersion <= new Version(1, 1, 0))
                {
                    continue;
                }
                long contentVersion = document.RootElement.GetProperty("contentVersion").GetInt64();
                if (contentVersion < newContentVersion)
                {
                    int schemaVersion = document.RootElement.GetProperty("schemaVersion").GetInt32();
                    if (schemaVersion == 2)
                        releases.Add(new RollbackReleaseOption(releaseId, contentVersion, true));
                }
            }
            catch (JsonException)
            {
                // 不把無法解析的 Manifest 顯示成可選退版目標。
            }
        }
        // 完整客戶端不能退回只有 NX 的清單，否則可能遺失 EXE／DLL。
        return releases.Where(release => !releases.Any(item => item.FullClient) || release.FullClient)
            .OrderByDescending(release => release.ContentVersion)
            .ThenByDescending(release => release.ReleaseId, StringComparer.Ordinal)
            .ToList();
    }

    private void RestoreInitialSelection()
    {
        (string ReleaseId, long ContentVersion)? pending = FindLatestWorkInProgress();
        long savedContentVersion = _savedState?.ContentVersion ?? 0;
        bool savedUsable = IsValidReleaseId(_savedState?.ReleaseId ?? string.Empty) &&
            !IsReleaseCancelled(_savedState!.ReleaseId);

        if (pending is not null && (!savedUsable || pending.Value.ContentVersion >= savedContentVersion))
        {
            SelectRelease(pending.Value.ReleaseId);
        }
        else if (savedUsable)
        {
            SelectRelease(_savedState!.ReleaseId);
        }
        else
        {
            SelectRelease(SuggestReleaseId());
        }
    }

    private (string ReleaseId, long ContentVersion)? FindLatestWorkInProgress()
    {
        List<(string ReleaseId, long ContentVersion)> versions = [];
        string candidateDirectory = Path.Combine(_projectRoot, "work", "candidates");
        if (Directory.Exists(candidateDirectory))
        {
            foreach (string infoPath in Directory.EnumerateFiles(candidateDirectory, "release-info.json", SearchOption.AllDirectories))
            {
                string releaseId = Path.GetFileName(Path.GetDirectoryName(infoPath)!);
                long? contentVersion = ReadContentVersion(infoPath);
                if (contentVersion is not null && contentVersion > _stableContentVersion &&
                    !IsReleaseCancelled(releaseId) && !HasPublishReport(releaseId, "stable") &&
                    !string.Equals(_stableReleaseId, releaseId, StringComparison.Ordinal))
                {
                    versions.Add((releaseId, contentVersion.Value));
                }
            }
        }

        string releaseDirectory = Path.Combine(_projectRoot, "catalog", "releases");
        if (Directory.Exists(releaseDirectory))
        {
            foreach (string manifestPath in Directory.EnumerateFiles(releaseDirectory, "*.json"))
            {
                string releaseId = Path.GetFileNameWithoutExtension(manifestPath);
                long? contentVersion = ReadContentVersion(manifestPath);
                if (contentVersion is not null && contentVersion > _stableContentVersion &&
                    !IsReleaseCancelled(releaseId) &&
                    !HasPublishReport(releaseId, "stable") &&
                    !string.Equals(_stableReleaseId, releaseId, StringComparison.Ordinal))
                {
                    versions.Add((releaseId, contentVersion.Value));
                }
            }
        }

        if (versions.Count == 0)
        {
            return null;
        }
        return versions
            .OrderByDescending(version => version.ContentVersion)
            .ThenByDescending(version => version.ReleaseId, StringComparer.Ordinal)
            .First();
    }

    private void SelectRelease(string releaseId)
    {
        _updatingFields = true;
        ReleaseIdTextBox.Text = releaseId;
        long? contentVersion = TryGetReleaseContentVersion(releaseId);
        ContentVersionTextBox.Text = (contentVersion ?? (_highestContentVersion + 1))
            .ToString(CultureInfo.InvariantCulture);

        string infoPath = Path.Combine(CandidatePath(releaseId), "release-info.json");
        if (File.Exists(infoPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(infoPath));
                string? description = document.RootElement.GetProperty("changes")
                    .EnumerateArray()
                    .Select(change => change.TryGetProperty("description", out JsonElement value) ? value.GetString() : null)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(description))
                {
                    DescriptionTextBox.Text = description;
                }
                if (document.RootElement.TryGetProperty("sourceDirectory", out JsonElement sourceValue) &&
                    sourceValue.GetString() is { Length: > 0 } sourceDirectory &&
                    Directory.Exists(sourceDirectory))
                {
                    SourceTextBox.Text = sourceDirectory;
                }
            }
            catch (JsonException)
            {
                // Candidate 的正式驗證交由 Publisher；UI 保留目前輸入值。
            }
        }
        _updatingFields = false;
        RestoreDeepVerificationState(releaseId);
        SaveManagerState();
        UpdateActionAvailability();
        ShowSelectedPublicationStatus();
    }

    private void SetSelectedContentVersion()
    {
        string releaseId = ReleaseIdTextBox.Text.Trim();
        long contentVersion = TryGetReleaseContentVersion(releaseId) ?? (_highestContentVersion + 1);
        _updatingFields = true;
        ContentVersionTextBox.Text = contentVersion.ToString(CultureInfo.InvariantCulture);
        _updatingFields = false;
        RestoreDeepVerificationState(releaseId);
        SaveManagerState();
    }

    private long? TryGetReleaseContentVersion(string releaseId)
    {
        if (!IsValidReleaseId(releaseId))
        {
            return null;
        }
        string[] paths =
        [
            Path.Combine(CandidatePath(releaseId), "release-info.json"),
            Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json"),
            Path.Combine(_projectRoot, "catalog", "cancelled", $"{releaseId}.json")
        ];
        foreach (string path in paths)
        {
            long? value = ReadContentVersion(path);
            if (value is not null)
            {
                return value;
            }
        }
        return null;
    }

    private static long? ReadContentVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("contentVersion", out JsonElement value) &&
                value.TryGetInt64(out long contentVersion)
                ? contentVersion
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RestoreDeepVerificationState(string releaseId)
    {
        _deepVerifiedReleaseId = null;
        if (!ManifestExists(releaseId) || IsReleaseCancelled(releaseId))
        {
            return;
        }
        string reportPath = Path.Combine(_projectRoot, "reports", "verifications", $"{releaseId}-remote-deep.json");
        string manifestPath = Path.Combine(_projectRoot, "catalog", "releases", $"{releaseId}.json");
        if (!File.Exists(reportPath))
        {
            return;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            string? reportReleaseId = document.RootElement.GetProperty("releaseId").GetString();
            bool deep = document.RootElement.GetProperty("deep").GetBoolean();
            string? expectedHash = document.RootElement.GetProperty("manifestSha256").GetString();
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)));
            if (document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) &&
                schema.TryGetInt32(out int schemaVersion) && schemaVersion == 2 && deep &&
                string.Equals(reportReleaseId, releaseId, StringComparison.Ordinal) &&
                string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                _deepVerifiedReleaseId = releaseId;
            }
        }
        catch (JsonException)
        {
            // 無法驗證的紀錄視為尚未完成深度驗證。
        }
    }

    private ManagerState? LoadManagerState()
    {
        string path = Path.Combine(_projectRoot, "work", "manager-state.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ManagerState>(File.ReadAllBytes(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveManagerState()
    {
        if (_updatingFields || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }
        long.TryParse(ContentVersionTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long contentVersion);
        ManagerState state = new()
        {
            ReleaseId = ReleaseIdTextBox.Text.Trim(),
            ContentVersion = contentVersion,
            SourceDirectory = SourceTextBox.Text.Trim(),
            UiReplacementFile = UiReplacementTextBox.Text.Trim(),
            Description = DescriptionTextBox.Text.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        string path = Path.Combine(_projectRoot, "work", "manager-state.json");
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(state));
            File.Move(temporaryPath, path, true);
            _savedState = state;
        }
        catch (IOException)
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_busy)
        {
            SaveManagerState();
            return;
        }
        e.Cancel = true;
        AppDialog.Warning(this, "無法關閉", "目前有操作正在執行。為避免留下遠端操作鎖，請等待完成後再關閉。");
    }

    private string SuggestReleaseId()
    {
        string prefix = DateTime.Now.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        string releaseDirectory = Path.Combine(_projectRoot, "catalog", "releases");
        string candidateDirectory = Path.Combine(_projectRoot, "work", "candidates");
        int next = 1;
        if (Directory.Exists(releaseDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(releaseDirectory, $"{prefix}.*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string suffix = name[(prefix.Length + 1)..];
                if (int.TryParse(suffix, out int value))
                {
                    next = Math.Max(next, value + 1);
                }
            }
        }
        if (Directory.Exists(candidateDirectory))
        {
            foreach (string path in Directory.EnumerateDirectories(candidateDirectory, $"{prefix}.*"))
            {
                string name = Path.GetFileName(path);
                string suffix = name[(prefix.Length + 1)..];
                if (int.TryParse(suffix, out int value))
                {
                    next = Math.Max(next, value + 1);
                }
            }
        }
        string cancelledDirectory = Path.Combine(_projectRoot, "catalog", "cancelled");
        if (Directory.Exists(cancelledDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(cancelledDirectory, $"{prefix}.*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string suffix = name[(prefix.Length + 1)..];
                if (int.TryParse(suffix, out int value))
                {
                    next = Math.Max(next, value + 1);
                }
            }
        }
        return $"{prefix}.{next}";
    }

    private static string FindProjectRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "patch.ps1")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("找不到 GamePatchDepot 專案根目錄與 patch.ps1。");
    }

    private sealed record OperationArtifactsSnapshot(
        HashSet<string> StagingDirectories,
        HashSet<string> TemporaryChunks,
        bool CandidateExisted,
        bool ManifestExisted,
        bool SignatureExisted,
        bool BuildReportExisted);

    private sealed class ManagerState
    {
        public string ReleaseId { get; init; } = string.Empty;
        public long ContentVersion { get; init; }
        public string SourceDirectory { get; init; } = string.Empty;
        public string UiReplacementFile { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; init; }
    }
}
