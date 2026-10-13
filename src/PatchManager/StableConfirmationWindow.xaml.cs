using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GamePatchDepot.Manager;

public partial class StableConfirmationWindow : Window
{
    private readonly string _releaseId;

    public StableConfirmationWindow(string releaseId)
    {
        _releaseId = releaseId;
        InitializeComponent();
        ReleaseIdText.Text = releaseId;
        UpdateState();
    }

    private void ConfirmationTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (!IsInitialized) return;
        string entered = ConfirmationTextBox.Text.Trim();
        bool matches = string.Equals(entered, _releaseId, StringComparison.Ordinal);
        ConfirmButton.IsEnabled = matches && GameTestCheckBox.IsChecked == true;
        ConfirmationHintText.Text = ConfirmButton.IsEnabled ? "確認條件已完成，可以發布。"
            : entered.Length == 0 ? "請輸入上方版本編號，並勾選下方測試確認。"
            : !matches ? "版本編號不一致，請與上方版本重新核對。"
            : "版本編號正確；請確認已完成遊戲測試後再勾選。";
        ConfirmationHintText.Foreground = ConfirmButton.IsEnabled ? Brushes.DarkGreen
            : entered.Length > 0 && !matches ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("MutedTextBrush");
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateState();
        if (ConfirmButton.IsEnabled) DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
