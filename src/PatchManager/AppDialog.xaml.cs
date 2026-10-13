using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GamePatchDepot.Manager;

public enum AppDialogKind
{
    Information,
    Warning,
    Error
}

public partial class AppDialog : Window
{
    private AppDialog(
        Window owner,
        string title,
        string message,
        AppDialogKind kind,
        bool requiresConfirmation,
        string confirmText,
        bool dangerous)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = requiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Style = (Style)FindResource(dangerous ? "DangerButtonStyle" : "PrimaryButtonStyle");

        (IconText.Text, IconText.Foreground, IconBackground.Background) = kind switch
        {
            AppDialogKind.Warning => ("!", Brush("#B45309"), Brush("#FEF3C7")),
            AppDialogKind.Error => ("×", Brush("#B42318"), Brush("#FEE4E2")),
            _ => ("i", Brush("#1D4ED8"), Brush("#DBEAFE"))
        };
    }

    public static void Information(Window owner, string title, string message) =>
        new AppDialog(owner, title, message, AppDialogKind.Information, false, "知道了", false).ShowDialog();

    public static void AnnouncementPreview(Window owner, string title, string body)
    {
        AppDialog preview = new(owner, title, "", AppDialogKind.Information, false, "關閉預覽", false)
        { Width = 640, Height = 640, SizeToContent = SizeToContent.Manual };
        preview.IconBackground.Visibility = Visibility.Collapsed;
        Grid.SetColumn(preview.MessageScrollViewer, 0);
        Grid.SetColumnSpan(preview.MessageScrollViewer, 2);
        preview.MessageScrollViewer.Height = 440;
        preview.MessageScrollViewer.Content = AnnouncementPreviewContent.Build(body);
        preview.ShowDialog();
    }

    public static void Warning(Window owner, string title, string message) =>
        new AppDialog(owner, title, message, AppDialogKind.Warning, false, "知道了", false).ShowDialog();

    public static void Error(Window owner, string title, string message) =>
        new AppDialog(owner, title, message, AppDialogKind.Error, false, "關閉", true).ShowDialog();

    public static bool Confirm(
        Window owner,
        string title,
        string message,
        string confirmText,
        bool dangerous = false) =>
        new AppDialog(owner, title, message, AppDialogKind.Warning, true, confirmText, dangerous).ShowDialog() == true;

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
