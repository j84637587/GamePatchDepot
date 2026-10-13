using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GamePatchDepot.Manager;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            App app = new();
            app.InitializeComponent();
            // Never Show or Run: Loaded would query the real R2 channels.
            MainWindow window = new();
            Check(window.Icon is not null, "Shared window icon resource missing");
            TabControl tabs = (TabControl)window.Content;
            TextBox gameLog = (TextBox)window.FindName("LogTextBox");
            TextBox launcherLog = (TextBox)window.FindName("LauncherLogTextBox");
            Check(!BindingOperations.IsDataBound(launcherLog, TextBox.TextProperty), "Whole-log binding returned");
            Check(!gameLog.IsUndoEnabled && !launcherLog.IsUndoEnabled, "Read-only logs retain undo history");
            Invoke(window, "FlushPendingLog");
            RunThemeTest(app);
            RunAnnouncementExamplesTest(window);
            RunAnnouncementFormattingTest();
            RunAnnouncementFeedTest(window);

            foreach (int selectedTab in new[] { 2, 1, 0 })
            {
                tabs.SelectedIndex = selectedTab;
                tabs.Measure(new Size(1344, 902));
                tabs.Arrange(new Rect(0, 0, 1344, 902));
                tabs.UpdateLayout();
                RunOutputTest(window, gameLog, launcherLog, selectedTab);
            }

            Invoke(window, "SetBusy", true, "測試操作中");
            Check(!((Grid)window.FindName("AnnouncementPane")).IsEnabled, "Announcement edit enabled while busy");
            Check(!((Button)window.FindName("LauncherBrowseButton")).IsEnabled, "Browse enabled while busy");
            Check(!((Button)window.FindName("LauncherPublishButton")).IsEnabled, "Publish enabled while busy");
            Invoke(window, "SetBusy", false, "就緒");
            Check(((Grid)window.FindName("AnnouncementPane")).IsEnabled, "Announcement edit stays disabled after completion");
            Check(((Button)window.FindName("LauncherBrowseButton")).IsEnabled, "Browse stays disabled after completion");
            Invoke(window, "AppendLog", "最後一筆本機紀錄", false);
            Invoke(window, "FlushPendingLog");
            Check(gameLog.Text == launcherLog.Text && gameLog.Text.EndsWith("最後一筆本機紀錄" + Environment.NewLine), "Final local log lost");
            Console.WriteLine("PASS: responsive input, ordered/lossless logs, both tabs, EOF drain, error prefix, completion and busy controls. No R2 requests.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RunAnnouncementExamplesTest(MainWindow window)
    {
        Expander examples = (Expander)window.FindName("AnnouncementExamplesExpander");
        TextBox sample = (TextBox)window.FindName("AnnouncementExampleTextBox");
        TextBox body = (TextBox)window.FindName("AnnouncementBodyTextBox");
        string draft = body.Text;
        Check(!examples.IsExpanded, "Examples consume editor space by default");
        Check(sample.IsReadOnly && !sample.IsUndoEnabled && sample.Text.Split('\n').Length > 1, "Example is not selectable read-only multiline text");
        Check(examples.Content is ScrollViewer { MaxHeight: 230, VerticalScrollBarVisibility: ScrollBarVisibility.Auto }, "Examples have no bounded scrolling area");
        examples.IsExpanded = true;
        sample.SelectAll();
        Check(sample.SelectionLength == sample.Text.Length, "Example cannot be selected for copying");
        examples.IsExpanded = false;
        Check(body.Text == draft, "Viewing examples replaced the current draft");
        Console.WriteLine("PASS: collapsible announcement examples, bounded scrolling, selectable sample and unchanged draft.");
    }

    private static void RunAnnouncementFormattingTest()
    {
        StackPanel content = AnnouncementPreviewContent.Build("第一段\r\n第二行\r\n\r\n## 重點\n:::notice NovaLink\n請 **注意** [下載](https://example.com/file)\n:::");
        Check(content.Children.Count == 3, "Paragraph, heading or card parsing differs from NovaLink");
        TextBlock paragraph = (TextBlock)content.Children[0];
        Check(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text == "第一段\n第二行", "Paragraph line breaks lost");
        Check(((TextBlock)content.Children[1]).FontWeight == FontWeights.SemiBold, "Heading emphasis missing");
        Border card = (Border)content.Children[2];
        TextBlock cardText = (TextBlock)((StackPanel)card.Child).Children[1];
        Inline[] inlines = cardText.Inlines.ToArray();
        Check(inlines.Length == 4 && inlines[1] is Bold && inlines[3] is Hyperlink { NavigateUri.AbsoluteUri: "https://example.com/file" }, "Strong or safe link formatting missing");
        foreach (string tone in new[] { "notice", "maintenance", "important" })
        {
            Border toneCard = (Border)AnnouncementPreviewContent.Build($":::{tone} 標題\n內容\n:::").Children[0];
            DockPanel header = (DockPanel)((StackPanel)toneCard.Child).Children[0];
            Color marker = ((SolidColorBrush)((Border)header.Children[0]).Background).Color;
            Color expected = tone switch { "maintenance" => Colors.DodgerBlue, "important" => Colors.IndianRed,
                _ => ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color };
            Check(marker == expected, "Card tone marker missing");
        }
        foreach (string source in new[] { ":::notice 標題\n沒有結尾", ":::custom 標題\n文字\n:::", "<script>alert(1)</script>",
            "[連結](javascript:alert)", "[連結](data:text/html,test)", "[連結](file:///C:/test)", "[連結](https://user:password@example.com)", "[連結](broken)" })
        {
            StackPanel literal = AnnouncementPreviewContent.Build(source);
            Check(literal.Children.Count == 1 && literal.Children[0] is TextBlock, "Invalid markup became a card");
            TextBlock text = (TextBlock)literal.Children[0];
            Check(text.Inlines.All(inline => inline is Run) && new TextRange(text.ContentStart, text.ContentEnd).Text == source,
                "Unsafe link, HTML or incomplete card was interpreted or discarded");
        }
        Console.WriteLine("PASS: announcement paragraphs, headings, card tones, emphasis, HTTPS links and literal unsafe/malformed input. No links opened.");
    }

    private static void RunAnnouncementFeedTest(MainWindow window)
    {
        TextBox body = (TextBox)window.FindName("AnnouncementBodyTextBox");
        string draft = body.Text;
        ListBox list = (ListBox)window.FindName("AnnouncementList");
        GamePatchDepot.AnnouncementFeed first = new() { Revision = 1, Items = [new() { Id = new string('a', 32), Title = "第一版", Body = "內容" }] };
        Invoke(window, "ApplyConfirmedAnnouncementFeed", first);
        Check(ReferenceEquals(list.Items[0], first.Items[0]) && body.Text == draft, "Initial feed failed to populate list or replaced draft");
        GamePatchDepot.AnnouncementFeed next = new() { Revision = 2, Items = [first.Items[0] with { Revision = 2, Body = "更新內容" }] };
        Invoke(window, "ApplyConfirmedAnnouncementFeed", next);
        Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], next.Items[0]) && body.Text == draft, "Confirmed publication did not update list or replaced draft");
        Invoke(window, "ApplyConfirmedAnnouncementFeed", new GamePatchDepot.AnnouncementFeed());
        Check(list.Items.Count == 0 && body.Text == draft, "Empty confirmed feed failed to clear list or replaced draft");
        Console.WriteLine("PASS: initial, updated and empty confirmed announcement feeds update the list without overwriting drafts. No R2 requests.");
    }

    private static void RunThemeTest(App app)
    {
        ComboBox category = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        category.Items.Add(new ComboBoxItem { Content = "一般公告", Tag = "notice" });
        category.Items.Add(new ComboBoxItem { Content = "維護公告", Tag = "maintenance" });
        category.SelectedIndex = 1;
        RollbackReleaseOption release = new("2026.10.04.1", 3, true);
        ComboBox rollback = new() { Width = 500, DisplayMemberPath = nameof(RollbackReleaseOption.DisplayText) };
        rollback.Items.Add(release);
        rollback.SelectedIndex = 0;
        CheckBox check = new() { Content = "開啟啟動器時彈出", Margin = new Thickness(0, 12, 0, 12) };
        ToggleState source = new();
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleState.Value)) { Source = source, Mode = BindingMode.TwoWay });
        ListBox list = new() { Height = 150 };
        list.Items.Add("一般公告：遊戲更新通知");
        list.Items.Add("維護公告：本週維護時間");
        list.Items.Add("[已下架] 舊版公告");
        list.SelectedIndex = 1;
        StackPanel panel = new() { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Patch Manager 共用控制項", FontSize = 22, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(category);
        panel.Children.Add(rollback);
        panel.Children.Add(check);
        panel.Children.Add(new CheckBox { Content = "修改後再次提醒（停用）", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(list);
        Border fixture = new() { Width = 600, Height = 430, Background = (Brush)app.FindResource("PageBrush"), Child = panel };
        TextElement.SetFontFamily(fixture, new FontFamily("Microsoft JhengHei UI"));
        TextElement.SetFontSize(fixture, 14);
        fixture.Measure(new Size(600, 430));
        fixture.Arrange(new Rect(0, 0, 600, 430));
        fixture.UpdateLayout();
        ContentPresenter selection = (ContentPresenter)category.Template.FindName("SelectionContent", category);
        Check(Equals(selection.Content, ((ComboBoxItem)category.SelectedItem).Content), "Category displays a container instead of its label");
        ContentPresenter rollbackSelection = (ContentPresenter)rollback.Template.FindName("SelectionContent", rollback);
        Check(Equals(rollbackSelection.Content, release) && VisualText(rollbackSelection) == release.DisplayText, "DisplayMemberPath projection lost");
        Check(check.Template.FindName("CheckBorder", check) is Border, "Shared checkbox template missing");
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        check.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        Check(source.Value && ((System.Windows.Shapes.Path)check.Template.FindName("CheckMark", check)).Visibility == Visibility.Visible, "Checkbox binding or checked indicator lost");
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        check.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        Check(!source.Value, "Checkbox cannot be cleared");
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        ListBoxItem item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        Check(item.IsSelected && item.Template.FindName("ListItemBorder", item) is Border, "List selection or shared template lost");
        Console.WriteLine("PASS: shared templates, selected labels, DisplayMemberPath, checkbox two-way binding and list selection. No windows shown.");
    }

    private sealed class ToggleState { public bool Value { get; set; } }

    private static string VisualText(DependencyObject element)
    {
        if (element is TextBlock text) return text.Text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            string value = VisualText(VisualTreeHelper.GetChild(element, index));
            if (value.Length > 0) return value;
        }
        return "";
    }

    private static void RunOutputTest(MainWindow window, TextBox gameLog, TextBox launcherLog, int selectedTab)
    {
        const int count = 1500;
        typeof(MainWindow).GetField("_runningLauncher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        string lines = string.Concat(Enumerable.Range(0, count)
            .Select(i => $"[{i * 25 / count}/100] 啟動器：R2 回驗：NovaLink.exe（{i * 12d / count:F2} MiB）\n"));
        const string finalLine = "[100/100] 啟動器：離線測試完成";
        using StreamReader stdout = Reader(lines + finalLine + "\n");
        using StreamReader stderr = Reader("模擬錯誤一\n模擬錯誤二\n");
        int initialLength = gameLog.Text.Length;
        int ticks = 0;
        Stopwatch watch = Stopwatch.StartNew();
        DispatcherTimer inputTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        inputTimer.Tick += (_, _) => ticks++;
        DispatcherFrame frame = new();
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Task.WhenAll((Task)Invoke(window, "PumpOutputAsync", stdout, false)!,
                    (Task)Invoke(window, "PumpOutputAsync", stderr, true)!);
            }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        }));
        inputTimer.Start();
        Dispatcher.PushFrame(frame);
        inputTimer.Stop();
        if (failure is not null) throw failure;
        Check(ticks > 0, "Output starved input-priority events");
        Check(gameLog.Text == launcherLog.Text, "Tab logs differ");
        string appended = gameLog.Text[initialLength..];
        string[] actualLines = appended.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Check(actualLines.Length == count + 3, "Output lost or duplicated lines");
        Check(actualLines.Contains("[錯誤] 模擬錯誤一") && actualLines.Contains("[錯誤] 模擬錯誤二"), "Error prefix lost");
        Check(actualLines.Where(line => !line.StartsWith("[錯誤]", StringComparison.Ordinal))
            .SequenceEqual((lines + finalLine + "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)), "Stdout order changed");
        Check(((ProgressBar)window.FindName("OperationProgressBar")).Value == 100, "Final progress lost");
        Check(((TextBlock)window.FindName("OperationStatusText")).Text == "啟動器：離線測試完成", "Final status lost");
        Console.WriteLine($"tab={selectedTab} lines={count} elapsed_ms={watch.ElapsedMilliseconds} input_ticks={ticks}");
    }

    private static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    private static object? Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
