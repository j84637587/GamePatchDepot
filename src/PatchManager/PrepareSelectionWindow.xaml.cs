using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GamePatchDepot.Manager;

public partial class PrepareSelectionWindow : Window
{
    private readonly PrepareSelection _selection;
    private readonly ICollectionView _view;
    private bool _batchUpdate;

    public PrepareSelectionWindow(PrepareSelection selection)
    {
        InitializeComponent();
        _selection = selection;
        SourceText.Text = selection.SourceDirectory;
        _view = CollectionViewSource.GetDefaultView(selection.Files);
        _view.Filter = item => ((SelectionFile)item).Path.Contains(SearchTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        FilesGrid.ItemsSource = _view;
        foreach (SelectionFile file in selection.Files) file.PropertyChanged += Selection_Changed;
        Closed += (_, _) =>
        {
            foreach (SelectionFile file in selection.Files) file.PropertyChanged -= Selection_Changed;
        };
        UpdateSummary();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void Selection_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!_batchUpdate) UpdateSummary();
    }

    private void UpdateSummary()
    {
        List<SelectionFile> included = _selection.Files.Where(file => file.Include).ToList();
        int removed = _selection.Files.Count(file => file.Remove);
        SummaryText.Text = $"共 {_selection.Files.Count} 個檔案　收錄 {included.Count} 個（{included.Sum(file => file.Size) / 1073741824d:N2} GiB）" +
            $"　不收錄 {_selection.Files.Count - included.Count - removed} 個　從新版移除 {removed} 個";
        ValidationText.Text = removed > 0 ? $"注意：{removed} 個檔案將列入刪除清單；新版套用時才會移除玩家端檔案，不會刪除來源檔案。" : string.Empty;
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e) => SetVisible(true);
    private void ClearVisible_Click(object sender, RoutedEventArgs e) => SetVisible(false);

    private void SetVisible(bool include)
    {
        _batchUpdate = true;
        foreach (SelectionFile file in _view.Cast<SelectionFile>().Where(file => file.CanInclude)) file.Include = include;
        _batchUpdate = false;
        UpdateSummary();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _batchUpdate = true;
        foreach (SelectionFile file in _selection.Files)
        {
            file.Remove = false;
            file.Include = file.RecommendedInclude;
        }
        _batchUpdate = false;
        UpdateSummary();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        HashSet<string> selected = _selection.Files.Where(file => file.Include).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.Any(path => string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)))
        {
            ValidationText.Text = "請至少收錄一個遊戲 EXE；完整客戶端不能只有資源檔案。";
            return;
        }
        List<string> missing = _selection.RequiredFiles.Where(path => !selected.Contains(path)).ToList();
        if (missing.Count > 0)
        {
            ValidationText.Text = $"必要檔案不能排除：{string.Join("、", missing)}";
            return;
        }
        DialogResult = true;
    }
}
