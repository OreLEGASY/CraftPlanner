using CraftPlanner.Core.Data;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public class GameDataServiceTests
{
    [Fact]
    public void アイテムの登録_自動IDと指定ID()
    {
        var svc = new GameDataService(Game());
        var auto = svc.CreateItem("新素材");
        Assert.StartsWith("item-", auto.Id);
        Assert.NotNull(svc.Data.FindItem(auto.Id));

        var custom = svc.CreateItem("銀", "silver_ingot");
        Assert.Equal("silver_ingot", custom.Id);

        Assert.Throws<ArgumentException>(() => svc.CreateItem(""));                 // 名前は必須
        Assert.Throws<ArgumentException>(() => svc.CreateItem("x", "bad id!"));     // 不正な文字
        Assert.Throws<InvalidOperationException>(() => svc.CreateItem("別物", Sword)); // ID 重複
    }

    [Fact]
    public void 同名でもIDが違えば別アイテム()
    {
        var svc = new GameDataService(Game());
        var a = svc.CreateItem("鉄の剣");
        Assert.NotEqual(Sword, a.Id);
        Assert.Equal(2, svc.Data.Items.Count(i => i.Name == "鉄の剣"));
    }

    [Fact]
    public void アイテムの編集()
    {
        var svc = new GameDataService(Game());
        svc.UpdateItem(Ore, "鉄鉱石", "概要", "詳細", "鉱山で採掘", "AAAA");
        var item = svc.Data.FindItem(Ore)!;
        Assert.Equal("鉄鉱石", item.Name);
        Assert.Equal("鉱山で採掘", item.Acquisition);
        Assert.Equal("AAAA", item.IconPng);
        Assert.Throws<ArgumentException>(() => svc.UpdateItem(Ore, " ", "", "", "", null));
    }

    [Fact]
    public void 参照されているアイテムは削除できず参照箇所を示す()
    {
        var g = Game();
        var svc = new GameDataService(g);
        var plan = EmptyPlan(g);
        new PlanEditor(plan, g).AddItemNode(Ore, 1);

        var ex = Assert.Throws<ItemReferencedException>(() => svc.DeleteItem(Ore, new[] { plan }));
        Assert.Contains(ex.References, r => r.Type == ItemReference.Kind.RecipeMaterial && r.RecipeId == IngotRecipe);
        Assert.Contains(ex.References, r => r.Type == ItemReference.Kind.PlanNode && r.PlanId == plan.Id);
        Assert.NotNull(g.FindItem(Ore)); // 削除されていない
    }

    [Fact]
    public void 参照を解消すれば削除できる()
    {
        var g = Game();
        var svc = new GameDataService(g);
        // 石炭はインゴットの精錬レシピだけが使う
        svc.UpdateRecipe(IngotRecipe, "精錬", 3, new[] { M(Ore, 2) });
        Assert.Empty(svc.FindReferences(Coal));
        svc.DeleteItem(Coal);
        Assert.Null(g.FindItem(Coal));
    }

    [Fact]
    public void 使用先はレシピから導出される()
    {
        var g = Game();
        var usages = g.FindUsages(Ore);
        Assert.Equal(2, usages.Count);
        Assert.All(usages, u => Assert.Equal(Ingot, u.Product.Id));
    }

    [Fact]
    public void レシピの追加_編集_削除()
    {
        var g = Game();
        var svc = new GameDataService(g);
        var r = svc.AddRecipe(Ore, "購入", 10);
        Assert.Equal(10, r.OutputQuantity);
        Assert.Empty(r.Materials);          // 素材 0 種類を許可
        Assert.Single(g.FindItem(Ore)!.Recipes);

        svc.UpdateRecipe(r.Id, "採掘", 2, new[] { M(Coal, 1) });
        Assert.Equal("採掘", g.FindRecipe(r.Id)!.Name);
        Assert.Single(g.FindRecipe(r.Id)!.Materials);

        Assert.Throws<ArgumentException>(() => svc.UpdateRecipe(r.Id, "", 0, Array.Empty<RecipeMaterial>()));   // 完成数 0
        Assert.Throws<ArgumentException>(() => svc.UpdateRecipe(r.Id, "", 1, new[] { M(Coal, 0) }));           // 素材数 0
        Assert.Throws<ArgumentException>(() => svc.UpdateRecipe(r.Id, "", 1, new[] { M(Coal, 1), M(Coal, 2) })); // 重複素材
        Assert.Throws<ArgumentException>(() => svc.UpdateRecipe(r.Id, "", 1, new[] { M("unknown", 1) }));       // 未登録素材

        svc.DeleteRecipe(r.Id);
        Assert.Null(g.FindRecipe(r.Id));
        Assert.Equal(2, g.FindItem(Ingot)!.Recipes.Count); // ほかのレシピは無事
    }

    [Fact]
    public void 検証は重複IDや不正な数量を検出する()
    {
        var g = Game();
        g.Items.Add(new Item { Id = Sword, Name = "重複" });
        g.FindItem(Ingot)!.Recipes[0].OutputQuantity = 0;
        var v = Validators.ValidateGameData(g);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("重複"));
        Assert.Contains(v.Errors, e => e.Contains("完成数量"));
    }
}
