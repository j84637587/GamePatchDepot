using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GamePatchDepot.Manager;

// Keep the supported syntax aligned with NovaLink's announcement-format.ts.
public static class AnnouncementPreviewContent
{
    public static StackPanel Build(string body)
    {
        StackPanel content = new();
        string[] lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<string> paragraph = [];
        void Flush()
        {
            if (paragraph.Count == 0) return;
            content.Children.Add(Text(string.Join('\n', paragraph)));
            paragraph.Clear();
        }
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match card = Regex.Match(line, @"^:::(notice|maintenance|important) (.+)$");
            int end = card.Success ? Array.IndexOf(lines, ":::", index + 1) : -1;
            if (card.Success && end >= 0)
            {
                Flush();
                string tone = card.Groups[1].Value;
                Brush marker = tone switch
                {
                    "maintenance" => Brushes.DodgerBlue,
                    "important" => Brushes.IndianRed,
                    _ => (Brush)Application.Current.FindResource("AccentBrush")
                };
                StackPanel cardBody = new();
                TextBlock title = Text(card.Groups[2].Value, true);
                title.Foreground = tone == "maintenance" ? (Brush)Application.Current.FindResource("TextBrush") : marker;
                DockPanel header = new();
                header.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new(4), Background = marker,
                    Margin = new(0, 0, 8, 8), VerticalAlignment = VerticalAlignment.Center });
                header.Children.Add(title);
                cardBody.Children.Add(header);
                TextBlock text = Text(string.Join('\n', lines[(index + 1)..end]));
                text.Foreground = (Brush)Application.Current.FindResource("MutedTextBrush");
                cardBody.Children.Add(text);
                content.Children.Add(new Border { Style = (Style)Application.Current.FindResource("CardBorderStyle"),
                    Background = (Brush)Application.Current.FindResource("PageBrush"), Padding = new(14, 12, 14, 4),
                    Margin = new(0, 0, 0, 12), Child = cardBody });
                index = end;
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                content.Children.Add(Text(line[3..], true));
            }
            else if (string.IsNullOrWhiteSpace(line)) Flush();
            else paragraph.Add(line);
        }
        Flush();
        return content;
    }

    private static TextBlock Text(string value, bool heading = false)
    {
        TextBlock block = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8),
            FontSize = heading ? 15 : 14, LineHeight = 23,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        int offset = 0;
        foreach (Match token in Regex.Matches(value, @"\*\*([^*\n]+)\*\*|\[([^\]\n]+)\]\(([^\s)]+)\)"))
        {
            if (token.Index > offset) block.Inlines.Add(new Run(value[offset..token.Index]));
            if (token.Groups[1].Success)
                block.Inlines.Add(new Bold(new Run(token.Groups[1].Value)));
            else if (Uri.TryCreate(token.Groups[3].Value, UriKind.Absolute, out Uri? uri) &&
                     uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host))
            {
                Hyperlink link = new(new Run(token.Groups[2].Value)) { NavigateUri = uri,
                    Foreground = (Brush)Application.Current.FindResource("AccentBrush") };
                link.RequestNavigate += (_, e) =>
                {
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    {
                        if (Window.GetWindow(block) is Window owner)
                            AppDialog.Warning(owner, "無法開啟連結", error.Message);
                    }
                };
                block.Inlines.Add(link);
            }
            else block.Inlines.Add(new Run(token.Value));
            offset = token.Index + token.Length;
        }
        if (offset < value.Length) block.Inlines.Add(new Run(value[offset..]));
        return block;
    }
}
