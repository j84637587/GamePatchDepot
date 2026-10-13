using System.Windows;

namespace GamePatchDepot.Manager;

public sealed record RollbackReleaseOption(string ReleaseId, long ContentVersion, bool FullClient = false)
{
    public string DisplayText => $"{ReleaseId}（Content {ContentVersion}／{(FullClient ? "完整客戶端" : "舊版 NX")}）";
}

public partial class RollbackWindow : Window
{
    public RollbackWindow(
        IReadOnlyList<RollbackReleaseOption> releases,
        string newReleaseId,
        long newContentVersion)
    {
        InitializeComponent();
        NewReleaseIdTextBox.Text = newReleaseId;
        NewContentVersionTextBox.Text = newContentVersion.ToString();
        ReleaseComboBox.ItemsSource = releases;
        ReleaseComboBox.SelectedIndex = releases.Count > 0 ? 0 : -1;
    }

    public RollbackReleaseOption? SelectedRelease => ReleaseComboBox.SelectedItem as RollbackReleaseOption;

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRelease is null)
        {
            AppDialog.Warning(this, "尚未選擇版本", "請先選擇一個舊版本。");
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
