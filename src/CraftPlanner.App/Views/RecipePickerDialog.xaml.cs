using System.Windows;
using CraftPlanner.App.ViewModels;
using CraftPlanner.Core.Calculation;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.Views;

public sealed class RecipeOption
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string CurrentMark { get; init; } = "";
    public string CraftsText { get; init; } = "";
    public List<string> Lines { get; init; } = new();
}

/// <summary>ノードのアイテムが持つレシピを比較表示し、採用するレシピを選ぶ。プレビューだけでは計算を変えない。</summary>
public partial class RecipePickerDialog : Window
{
    public string? SelectedRecipeId { get; private set; }

    private RecipePickerDialog(PlanViewModel plan, ItemNodeViewModel node)
    {
        InitializeComponent();
        var item = plan.Game.FindItem(node.ItemId);
        var demand = Math.Max(1, node.Demand);
        HeaderText.Text = $"「{node.Name}」を {demand} 個用意する場合";
        var options = new List<RecipeOption>();
        if (item != null)
        {
            foreach (var r in item.Recipes)
            {
                var crafts = PlanCalculator.CeilDiv(demand, r.OutputQuantity);
                var produced = crafts * r.OutputQuantity;
                var lines = r.Materials.Count == 0
                    ? new List<string> { "素材なし（採取・購入など）" }
                    : r.Materials.Select(m => $"・{plan.Game.FindItem(m.ItemId)?.Name ?? m.ItemId} ×{m.Quantity} → 合計 {crafts * m.Quantity}（所持 {plan.Plan.Inventory.GetValueOrDefault(m.ItemId)}）").ToList();
                options.Add(new RecipeOption
                {
                    Id = r.Id,
                    Title = string.IsNullOrWhiteSpace(r.Name) ? $"レシピ（×{r.OutputQuantity}）" : $"{r.Name}（×{r.OutputQuantity}）",
                    CurrentMark = r.Id == node.Model.SelectedRecipeId ? "  ← 現在のレシピ" : "",
                    CraftsText = $"制作 {crafts} 回 → {produced} 個（余剰 {produced - demand}）",
                    Lines = lines,
                });
            }
        }
        List.ItemsSource = options;
        List.SelectedItem = options.FirstOrDefault(o => o.Id == node.Model.SelectedRecipeId) ?? options.FirstOrDefault();
    }

    public static string? Pick(Window? owner, PlanViewModel plan, ItemNodeViewModel node)
    {
        var dlg = new RecipePickerDialog(plan, node) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.SelectedRecipeId : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not RecipeOption o) { MessageBox.Show(this, "レシピを選択してください。", Title); return; }
        SelectedRecipeId = o.Id;
        DialogResult = true;
    }
}
