using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Tests;

/// <summary>テスト用のゲームデータ。鉄の剣 ← 鉄インゴット(×3 で 3 個完成... 等) を組み立てる。</summary>
public static class TestData
{
    public const string Sword = "sword";
    public const string Ingot = "ingot";
    public const string Ore = "ore";
    public const string Coal = "coal";
    public const string Hilt = "hilt";
    public const string Wood = "wood";

    public const string SwordRecipe = "r-sword";
    public const string IngotRecipe = "r-ingot";       // 鉄の原石 2 + 石炭 1 → インゴット 3 個
    public const string IngotRecipeAlt = "r-ingot-alt"; // 鉄の原石 1 → インゴット 1 個
    public const string HiltRecipe = "r-hilt";
    public const string WoodRecipe = "r-wood";         // 素材なし（採取）

    public static GameData Game()
    {
        var g = new GameData { Id = "game-test", Name = "テスト" };
        g.Items.Add(new Item
        {
            Id = Sword, Name = "鉄の剣",
            Recipes = { new Recipe { Id = SwordRecipe, Name = "鍛冶", OutputQuantity = 1, Materials = { M(Ingot, 3), M(Hilt, 1) } } },
        });
        g.Items.Add(new Item
        {
            Id = Ingot, Name = "鉄インゴット",
            Recipes =
            {
                new Recipe { Id = IngotRecipe, Name = "精錬", OutputQuantity = 3, Materials = { M(Ore, 2), M(Coal, 1) } },
                new Recipe { Id = IngotRecipeAlt, Name = "簡易精錬", OutputQuantity = 1, Materials = { M(Ore, 1) } },
            },
        });
        g.Items.Add(new Item { Id = Ore, Name = "鉄の原石" });
        g.Items.Add(new Item { Id = Coal, Name = "石炭" });
        g.Items.Add(new Item
        {
            Id = Hilt, Name = "柄",
            Recipes = { new Recipe { Id = HiltRecipe, OutputQuantity = 1, Materials = { M(Wood, 2) } } },
        });
        g.Items.Add(new Item
        {
            Id = Wood, Name = "木材",
            Recipes = { new Recipe { Id = WoodRecipe, Name = "採取", OutputQuantity = 1 } },
        });
        return g;
    }

    /// <summary>循環: A ← B ← A</summary>
    public static GameData CyclicGame()
    {
        var g = new GameData { Id = "game-cycle", Name = "循環" };
        g.Items.Add(new Item { Id = "a", Name = "A", Recipes = { new Recipe { Id = "r-a", OutputQuantity = 1, Materials = { M("b", 1), M("x", 1) } } } });
        g.Items.Add(new Item { Id = "b", Name = "B", Recipes = { new Recipe { Id = "r-b", OutputQuantity = 1, Materials = { M("a", 1) } } } });
        g.Items.Add(new Item { Id = "x", Name = "X" });
        return g;
    }

    public static RecipeMaterial M(string id, int qty) => new() { ItemId = id, Quantity = qty };

    public static Plan EmptyPlan(GameData g) => new() { Id = "plan-test", Name = "テスト計画", GameDataId = g.Id };
}
