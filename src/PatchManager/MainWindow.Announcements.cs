using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GamePatchDepot.Manager;

public partial class MainWindow
{
    private AnnouncementFeed _announcementFeed = new();
    private bool _announcementLoaded;
    private string _announcementId = Guid.NewGuid().ToString("N");
    private string AnnouncementDraftPath => Path.Combine(_projectRoot, "work", "announcements", "draft.json");
    private string AnnouncementRemotePath => Path.Combine(_projectRoot, "work", "announcements", "remote.json");

    private void InitializeAnnouncementPane()
    {
        if (!File.Exists(AnnouncementDraftPath)) return;
        try
        {
            AnnouncementDraft? draft = JsonSerializer.Deserialize(File.ReadAllBytes(AnnouncementDraftPath), AnnouncementJsonContext.Default.AnnouncementDraft);
            if (draft is not null) SetAnnouncementDraft(draft);
        }
        catch (Exception e) when (e is IOException or JsonException)
        { AnnouncementStatusText.Text = "無法讀取草稿，原檔案保留：" + e.Message; }
    }

    private AnnouncementDraft AnnouncementDraft() => new()
    {
        Id = _announcementId, Title = AnnouncementTitleTextBox.Text.Trim(), Body = AnnouncementBodyTextBox.Text.Trim(),
        Category = (string)((ComboBoxItem)AnnouncementCategoryBox.SelectedItem).Tag,
        Popup = AnnouncementPopupBox.IsChecked == true, RemindAgain = AnnouncementRemindBox.IsChecked == true
    };

    private void SetAnnouncementDraft(AnnouncementDraft draft)
    {
        _announcementId = draft.Id;
        AnnouncementTitleTextBox.Text = draft.Title;
        AnnouncementBodyTextBox.Text = draft.Body;
        AnnouncementCategoryBox.SelectedIndex = draft.Category switch { "maintenance" => 1, "update" => 2, _ => 0 };
        AnnouncementPopupBox.IsChecked = draft.Popup;
        AnnouncementRemindBox.IsChecked = draft.RemindAgain;
        AnnouncementIdentityText.Text = $"公告 ID：{draft.Id}";
    }

    private void AnnouncementNew_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AnnouncementBodyTextBox.Text) &&
            !AppDialog.Confirm(this, "新增公告", "編輯欄位將清空，請先儲存需要保留的草稿。", "新增")) return;
        AnnouncementList.SelectedItem = null;
        SetAnnouncementDraft(new() { Id = Guid.NewGuid().ToString("N"), Title = "", Body = "" });
    }

    private bool SaveAnnouncementDraft(bool showStatus = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AnnouncementDraftPath)!);
            string temporary = AnnouncementDraftPath + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(AnnouncementDraft(), AnnouncementJsonContext.Default.AnnouncementDraft));
            File.Move(temporary, AnnouncementDraftPath, true);
            if (showStatus) AnnouncementStatusText.Text = "草稿已儲存；尚未發布。";
            return true;
        }
        catch (IOException e) { AppDialog.Error(this, "無法儲存草稿", e.Message); return false; }
    }

    private void AnnouncementSave_Click(object sender, RoutedEventArgs e) => SaveAnnouncementDraft();
    private void AnnouncementPreview_Click(object sender, RoutedEventArgs e) =>
        AppDialog.AnnouncementPreview(this, AnnouncementTitleTextBox.Text, AnnouncementBodyTextBox.Text);

    private async void AnnouncementRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAnnouncementsAsync();
    private async Task RefreshAnnouncementsAsync()
    {
        if (_busy) return;
        _announcementLoaded = false;
        AnnouncementStatusText.Text = "正在讀取已發布公告…";
        if (!await RunCommandAsync("讀取已發布公告", ["announcement-status", "--output", AnnouncementRemotePath]))
        { AnnouncementStatusText.Text = "讀取失敗，請重新整理；未變更草稿。"; return; }
        LoadConfirmedAnnouncementOutput();
    }

    private bool LoadConfirmedAnnouncementOutput()
    {
        try
        {
            AnnouncementFeed feed = JsonSerializer.Deserialize(File.ReadAllBytes(AnnouncementRemotePath), AnnouncementJsonContext.Default.AnnouncementFeed)
                ?? throw new JsonException("公告清單為空。");
            ApplyConfirmedAnnouncementFeed(feed);
            return true;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _announcementLoaded = false;
            AnnouncementStatusText.Text = "無法載入已確認清單，請重新整理；未變更草稿。";
            AppendLog("無法讀取公告：" + e.Message, true);
            return false;
        }
    }

    private void ApplyConfirmedAnnouncementFeed(AnnouncementFeed feed)
    {
        _announcementFeed = feed;
        AnnouncementList.ItemsSource = feed.Items;
        _announcementLoaded = true;
        AnnouncementStatusText.Text = $"已確認遠端版次 {feed.Revision}，共 {feed.Items.Count} 則公告。";
    }

    private void AnnouncementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnnouncementList.SelectedItem is not AnnouncementEntry entry) return;
        SetAnnouncementDraft(new() { Id = entry.Id, Title = entry.Title, Body = entry.Body, Category = entry.Category, Popup = entry.Popup });
    }

    private async void AnnouncementPublish_Click(object sender, RoutedEventArgs e) => await ChangeAnnouncementAsync(false);
    private async void AnnouncementWithdraw_Click(object sender, RoutedEventArgs e) => await ChangeAnnouncementAsync(true);
    private async Task ChangeAnnouncementAsync(bool withdraw)
    {
        if (_busy) return;
        if (!_announcementLoaded) { AppDialog.Warning(this, "尚未確認遠端公告", "請先重新整理已發布公告。"); return; }
        AnnouncementEntry? published = _announcementFeed.Items.SingleOrDefault(item => item.Id == _announcementId);
        if (withdraw && (published is null || published.Withdrawn)) { AppDialog.Warning(this, "無法下架", "請選取目前已發布的公告。"); return; }
        if (string.IsNullOrWhiteSpace(AnnouncementTitleTextBox.Text) || string.IsNullOrWhiteSpace(AnnouncementBodyTextBox.Text))
        { AppDialog.Warning(this, "資料不完整", "請填寫公告標題與內容。"); return; }
        if (!AppDialog.Confirm(this, withdraw ? "下架公告" : "發布公告",
            $"即將{(withdraw ? "下架" : "發布")}「{AnnouncementTitleTextBox.Text}」。\n\n這會直接影響玩家讀取的公告，不改動遊戲或啟動器版本。", withdraw ? "確認下架" : "確認發布", withdraw)) return;
        if (!SaveAnnouncementDraft()) return;
        AnnouncementStatusText.Text = withdraw ? "正在下架並確認結果…" : "正在發布並確認結果…";
        _announcementLoaded = false;
        bool succeeded = await RunCommandAsync(withdraw ? "下架公告" : "發布公告",
            [withdraw ? "announcement-withdraw" : "announcement-publish", "--draft", AnnouncementDraftPath,
             "--expected-revision", _announcementFeed.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "--confirm", "--output", AnnouncementRemotePath]);
        // The successful command already read back R2 and wrote its confirmed feed.
        if (!succeeded || !LoadConfirmedAnnouncementOutput()) await RefreshAnnouncementsAsync();
        if (succeeded) { AnnouncementRemindBox.IsChecked = false; SaveAnnouncementDraft(false); }
    }
}
