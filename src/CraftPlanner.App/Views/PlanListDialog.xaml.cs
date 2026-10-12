using System.Windows;
using System.Windows.Input;
using CraftPlanner.App.ViewModels;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.Views;

public sealed record PlanRow(Plan Plan, bool IsOpen)
{
    public string Name => Plan.Name;
    public int Nodes => Plan.ItemNodes.Count + Plan.MemoNodes.Count;
    public string State => IsOpen ? "開いています" : "";
}

/// <summary>選択中のゲームデータに属する制作計画の一覧。開く・新規・名前変更・削除。</summary>
public partial class PlanListDialog : Window
{
    private readonly MainViewModel _main;
    public Plan? Selected { get; private set; }

    private PlanListDialog(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        HeaderText.Text = $"ゲームデータ「{main.SelectedGame?.Name}」の制作計画";
        Refresh();
    }

    public static Plan? Show(Window owner, MainViewModel main)
    {
        var dlg = new PlanListDialog(main) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Selected : null;
    }

    private void Refresh()
    {
        var openIds = _main.OpenPlans.Select(p => p.Plan.Id).ToHashSet(StringComparer.Ordinal);
        var rows = _main.PlansOfSelectedGame.Select(p => new PlanRow(p, openIds.Contains(p.Id))).ToList();
        List.ItemsSource = rows;
        if (rows.Count > 0) List.SelectedIndex = 0;
    }

    private PlanRow? Row => List.SelectedItem as PlanRow;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Row == null) { MessageBox.Show(this, "計画を選択してください。", Title); return; }
        Selected = Row.Plan;
        DialogResult = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (Row != null) Open_Click(sender, e); }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var game = _main.SelectedGame;
        if (game == null) return;
        var name = InputDialog.Show(this, "新しい制作計画", "計画の名前", "新しい計画");
        if (name == null) return;
        try
        {
            Selected = _main.Services.CreatePlan(game, name);
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "作成できません", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Row == null) return;
        var name = InputDialog.Show(this, "計画の名前を変更", "新しい名前", Row.Plan.Name);
        if (name == null) return;
        var open = _main.OpenPlans.FirstOrDefault(p => p.Plan.Id == Row.Plan.Id);
        if (open != null) open.Rename(name);
        else
        {
            Row.Plan.Name = name;
            _main.Services.SavePlan(Row.Plan);
        }
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Row == null) return;
        var r = MessageBox.Show(this, $"計画「{Row.Plan.Name}」を削除しますか？\n計画ファイルを削除します（削除前にバックアップを 1 世代残します）。アイテムデータは削除されません。",
            "計画の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            var open = _main.OpenPlans.FirstOrDefault(p => p.Plan.Id == Row.Plan.Id);
            if (open != null)
            {
                open.Close();
                _main.OpenPlans.Remove(open);
                if (_main.ActivePlan == open) _main.ActivePlan = _main.OpenPlans.FirstOrDefault();
            }
            _main.Services.DeletePlan(Row.Plan);
            _main.PersistTabs();
            Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "削除できません", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
