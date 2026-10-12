using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CraftPlanner.App.ViewModels;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.Views;

/// <summary>検索しながらアイテムを 1 つ選ぶ。未登録ならその場で新規作成できる。</summary>
public partial class ItemPickerDialog : Window
{
    private readonly MainViewModel _main;
    private readonly GameData _game;
    private readonly Func<Item, bool>? _filter;
    public Item? Selected { get; private set; }

    private ItemPickerDialog(MainViewModel main, GameData game, Func<Item, bool>? filter)
    {
        InitializeComponent();
        _main = main;
        _game = game;
        _filter = filter;
        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); };
    }

    public static Item? Pick(Window? owner, MainViewModel main, GameData game, string title, Func<Item, bool>? filter = null)
    {
        var dlg = new ItemPickerDialog(main, game, filter) { Owner = owner, Title = title };
        return dlg.ShowDialog() == true ? dlg.Selected : null;
    }

    /// <summary>「制作目標として展開」か「ノードだけ追加」かを選びながらアイテムを 1 つ選ぶ。</summary>
    public static (Item Item, bool Expand)? PickWithMode(Window? owner, MainViewModel main, GameData game, string title)
    {
        var dlg = new ItemPickerDialog(main, game, null) { Owner = owner, Title = title };
        dlg.ModePanel.Visibility = Visibility.Visible;
        if (dlg.ShowDialog() != true || dlg.Selected == null) return null;
        return (dlg.Selected, dlg.ExpandRadio.IsChecked == true);
    }

    private void Refresh()
    {
        var q = SearchBox.Text.Trim();
        IEnumerable<Item> items = _game.Items;
        if (_filter != null) items = items.Where(_filter);
        if (q.Length > 0)
            items = items.Where(i => Has(i.Name, q) || Has(i.Summary, q) || Has(i.Description, q) || Has(i.Acquisition, q) || Has(i.Id, q));
        var cards = items.OrderBy(i => i.Name, StringComparer.CurrentCulture).Select(i => new ItemCardViewModel(i, 0)).ToList();
        List.ItemsSource = cards;
        if (cards.Count > 0) List.SelectedIndex = 0;
        CountText.Text = $"{cards.Count} 件";
    }

    private static bool Has(string? s, string q) => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.CurrentCultureIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Min(List.SelectedIndex + 1, List.Items.Count - 1);
            List.ScrollIntoView(List.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Max(List.SelectedIndex - 1, 0);
            List.ScrollIntoView(List.SelectedItem);
            e.Handled = true;
        }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ItemCardViewModel card)
        {
            MessageBox.Show(this, "アイテムを選択してください。見つからない場合は「新規作成…」で登録できます。", Title);
            return;
        }
        Selected = card.Item;
        DialogResult = true;
    }

    private void CreateNew_Click(object sender, RoutedEventArgs e)
    {
        var created = ItemEditorDialog.CreateNew(this, _main, _game, SearchBox.Text.Trim());
        if (created == null) return;
        Selected = created;
        DialogResult = true;
    }
}
