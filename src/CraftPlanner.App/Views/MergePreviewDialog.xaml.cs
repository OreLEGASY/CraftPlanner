using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CraftPlanner.Core.ImportExport;

namespace CraftPlanner.App.Views;

public sealed record MergeOption(MergeDecision Decision, string Label);

/// <summary>取り込み 1 行分の表示と選択。</summary>
public sealed partial class MergeRow : ObservableObject
{
    public MergeEntry Entry { get; }

    public MergeRow(MergeEntry entry, bool incomingHasRecipes)
    {
        Entry = entry;
        Options = entry.Kind switch
        {
            MergeEntryKind.EmptyId => new List<MergeOption> { new(MergeDecision.CreateNew, "新しい ID を発行して取り込む"), new(MergeDecision.Skip, "取り込まない") },
            MergeEntryKind.Conflict => new List<MergeOption> { new(MergeDecision.Update, "既存アイテムを更新"), new(MergeDecision.CreateNew, "新しい ID で別アイテム"), new(MergeDecision.Skip, "この行を除外") },
            MergeEntryKind.New => new List<MergeOption> { new(MergeDecision.Update, "追加する"), new(MergeDecision.Skip, "取り込まない") },
            _ => new List<MergeOption> { new(MergeDecision.Skip, "変更なし") },
        };
        _selectedOption = Options.FirstOrDefault(o => o.Decision == entry.Decision) ?? Options[0];
        CanIncludeRecipes = incomingHasRecipes && entry.Kind != MergeEntryKind.Identical;
        _includeRecipes = entry.IncludeRecipes;
    }

    public List<MergeOption> Options { get; }
    public bool CanChoose => Entry.Kind != MergeEntryKind.Identical;
    public bool CanIncludeRecipes { get; }
    public string RowText => Entry.SourceRow > 0 ? Entry.SourceRow.ToString() : "";
    public string Id => Entry.DisplayId;
    public string Name => Entry.Incoming.Name;
    public string KindText => Entry.Kind switch
    {
        MergeEntryKind.New => "新規",
        MergeEntryKind.EmptyId => "ID 空欄",
        MergeEntryKind.Conflict => "ID 競合",
        _ => "同一",
    };
    public string ChangesText
    {
        get
        {
            var parts = Entry.FieldChanges.Select(c => $"{c.Field}: 「{Short(c.Before)}」→「{Short(c.After)}」").ToList();
            parts.AddRange(Entry.RecipeChanges);
            if (parts.Count == 0) return Entry.Kind == MergeEntryKind.New ? "（新しいアイテム）" : Entry.Kind == MergeEntryKind.EmptyId ? "（ID を決める必要があります）" : "";
            return string.Join("\n", parts);
        }
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    [ObservableProperty] private MergeOption _selectedOption;
    [ObservableProperty] private bool _includeRecipes;

    partial void OnSelectedOptionChanged(MergeOption value) => Entry.Decision = value.Decision;
    partial void OnIncludeRecipesChanged(bool value) => Entry.IncludeRecipes = value;

    public void Set(MergeDecision d)
    {
        var o = Options.FirstOrDefault(x => x.Decision == d);
        if (o != null) SelectedOption = o;
    }
}

/// <summary>取り込み前の確認画面。空 ID・ID 競合の行ごとに処理を選び、問題があれば適用できない。</summary>
public partial class MergePreviewDialog : Window
{
    private readonly MergePreview _preview;
    private readonly List<MergeRow> _rows;

    private MergePreviewDialog(MergePreview preview, string title)
    {
        InitializeComponent();
        Title = title;
        _preview = preview;
        var hasRecipes = preview.Entries.Any(e => e.IncludeRecipes);
        _rows = preview.Entries
            .OrderBy(e => e.Kind switch { MergeEntryKind.Conflict => 0, MergeEntryKind.EmptyId => 1, MergeEntryKind.New => 2, _ => 3 })
            .ThenBy(e => e.SourceRow)
            .Select(e => new MergeRow(e, hasRecipes)).ToList();
        Rows.ItemsSource = _rows;

        var n = preview.Entries.Count;
        var conflicts = preview.Entries.Count(e => e.Kind == MergeEntryKind.Conflict);
        var empty = preview.Entries.Count(e => e.Kind == MergeEntryKind.EmptyId);
        var identical = preview.Entries.Count(e => e.Kind == MergeEntryKind.Identical);
        var news = preview.Entries.Count(e => e.Kind == MergeEntryKind.New);
        SummaryText.Text = $"{n} 件を読み込みました: 新規 {news} / ID 競合 {conflicts} / ID 空欄 {empty} / 同一 {identical}。" +
                           "競合と空欄の行は処理を選んでください。適用はすべて一括で行い、途中失敗時は何も変更しません（適用前に現在のデータをバックアップします）。";
        if (!preview.CanApply)
        {
            ErrorBox.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = preview.Errors;
            ApplyButton.IsEnabled = false;
        }
    }

    public static bool Confirm(Window owner, MergePreview preview, string title)
    {
        var dlg = new MergePreviewDialog(preview, title) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    private void AllUpdate_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows.Where(r => r.Entry.Kind == MergeEntryKind.Conflict)) r.Set(MergeDecision.Update); }
    private void AllSkip_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows.Where(r => r.Entry.Kind == MergeEntryKind.Conflict)) r.Set(MergeDecision.Skip); }
    private void AllEmptyNew_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows.Where(r => r.Entry.Kind == MergeEntryKind.EmptyId)) r.Set(MergeDecision.CreateNew); }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_preview.CanApply) return;
        var applying = _preview.Entries.Count(x => x.Decision != MergeDecision.Skip && x.Kind != MergeEntryKind.Identical);
        if (applying == 0)
        {
            MessageBox.Show(this, "取り込む行がありません（すべて除外または同一です）。", Title);
            return;
        }
        var updates = _preview.Entries.Count(x => x.Kind == MergeEntryKind.Conflict && x.Decision == MergeDecision.Update);
        var r = MessageBox.Show(this, $"{applying} 件を取り込みます（うち既存アイテムの更新 {updates} 件）。実行しますか？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        DialogResult = true;
    }
}
