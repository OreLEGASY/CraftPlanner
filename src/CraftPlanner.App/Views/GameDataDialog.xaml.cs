using System.Windows;
using CraftPlanner.App.ViewModels;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.Views;

public sealed record GameRow(GameData Game, int Plans)
{
    public string Name => Game.Name;
    public int Items => Game.Items.Count;
    public string Id => Game.Id;
}

/// <summary>ゲームデータ（データセット）の作成・名前変更・削除。</summary>
public partial class GameDataDialog : Window
{
    private readonly MainViewModel _main;

    private GameDataDialog(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Refresh();
    }

    public static void Show(Window owner, MainViewModel main) => new GameDataDialog(main) { Owner = owner }.ShowDialog();

    private void Refresh()
    {
        var s = _main.Services;
        var rows = s.LoadedGames.OrderBy(g => g.Name, StringComparer.CurrentCulture).Select(g => new GameRow(g, s.PlansOf(g.Id).Count())).ToList();
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r.Game.Id == _main.SelectedGame?.Id) ?? rows.FirstOrDefault();
    }

    private GameRow? Row => List.SelectedItem as GameRow;

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "新しいゲームデータ", "ゲームの名前", "");
        if (name == null) return;
        try
        {
            var g = _main.Services.CreateGame(name);
            _main.RefreshGames();
            _main.SelectedGame = _main.Games.First(x => x.Id == g.Id);
            Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "作成できません", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Row == null) return;
        var name = InputDialog.Show(this, "名前の変更", "新しい名前", Row.Game.Name);
        if (name == null) return;
        Row.Game.Name = name;
        if (_main.Services.SaveGame(Row.Game))
        {
            _main.RefreshGames();
            Refresh();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Row == null) return;
        if (Row.Plans > 0)
        {
            MessageBox.Show(this, $"このゲームデータを参照する制作計画が {Row.Plans} 件あります。先に計画を削除してください（「計画」メニュー →「制作計画を開く…」→ 削除）。", "削除できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var r = MessageBox.Show(this, $"ゲームデータ「{Row.Game.Name}」（アイテム {Row.Items} 件）を削除しますか？\n削除前にバックアップを 1 世代残します。",
            "ゲームデータの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            _main.Services.DeleteGame(Row.Game);
            _main.RefreshGames();
            Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "削除できません", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (Row != null) _main.SelectedGame = _main.Games.FirstOrDefault(g => g.Id == Row.Game.Id);
        Close();
    }
}
