using System.Text.Json;
using CraftPlanner.Core.ImportExport;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Storage;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public class ImportExportTests
{
    [Fact]
    public void CSVの読み書き_引用符と改行()
    {
        var rows = new List<string[]> { new[] { "a", "b,c", "d\"e", "f\ng" } };
        var text = Csv.Write(rows);
        var parsed = Csv.Parse(text);
        Assert.Single(parsed);
        Assert.Equal(rows[0], parsed[0]);
        Assert.Empty(Csv.Parse("﻿\r\n\r\n"));
    }

    [Fact]
    public void アイテムCSVの往復()
    {
        var g = Game();
        g.FindItem(Ore)!.Summary = "概要, カンマ付き";
        var csv = ItemCsv.Export(g.Items);
        var read = ItemCsv.Read(csv);
        Assert.Empty(read.Errors);
        Assert.Equal(g.Items.Count, read.Items.Count);
        var ore = read.Items.Single(x => x.Item.Id == Ore).Item;
        Assert.Equal("概要, カンマ付き", ore.Summary);
        Assert.Equal("鉄の原石", ore.Name);
    }

    [Fact]
    public void CSVの空IDは選択なしに取り込まれない()
    {
        var g = Game();
        var read = ItemCsv.Read("id,name\n,名無し\nnew1,新規\n");
        var preview = MergePlanner.Preview(g, read.Items, incomingHasRecipes: false);
        var empty = preview.Entries.Single(e => e.Kind == MergeEntryKind.EmptyId);
        Assert.Equal(MergeDecision.Skip, empty.Decision);
        Assert.Equal(2, empty.SourceRow);

        var applied = MergePlanner.Apply(g, preview);
        Assert.DoesNotContain(applied.Items, i => i.Name == "名無し");
        Assert.Contains(applied.Items, i => i.Id == "new1");

        empty.Decision = MergeDecision.CreateNew;
        var applied2 = MergePlanner.Apply(g, preview);
        var created = applied2.Items.Single(i => i.Name == "名無し");
        Assert.StartsWith("item-", created.Id);
        Assert.Equal(Game().Items.Count, g.Items.Count); // 元データは不変
    }

    [Fact]
    public void ID競合は更新_新ID_除外を選べ差分を表示する()
    {
        var g = Game();
        var csv = $"id,name,summary\n{Ore},鉄鉱石,新しい概要\n";
        var preview = MergePlanner.Preview(g, ItemCsv.Read(csv).Items, incomingHasRecipes: false);
        var conflict = preview.Entries.Single();
        Assert.Equal(MergeEntryKind.Conflict, conflict.Kind);
        Assert.Contains(conflict.FieldChanges, c => c.Field == "名前" && c.Before == "鉄の原石" && c.After == "鉄鉱石");

        conflict.Decision = MergeDecision.Update;
        var updated = MergePlanner.Apply(g, preview);
        Assert.Equal("鉄鉱石", updated.FindItem(Ore)!.Name);
        Assert.Equal(g.Items.Count, updated.Items.Count);

        conflict.Decision = MergeDecision.CreateNew;
        var dup = MergePlanner.Apply(g, preview);
        Assert.Equal(g.Items.Count + 1, dup.Items.Count);
        Assert.Equal("鉄の原石", dup.FindItem(Ore)!.Name);

        conflict.Decision = MergeDecision.Skip;
        var skipped = MergePlanner.Apply(g, preview);
        Assert.Equal("鉄の原石", skipped.FindItem(Ore)!.Name);
    }

    [Fact]
    public void アイテムCSVの更新ではレシピを変更しない()
    {
        var g = Game();
        var csv = $"id,name\n{Ingot},鉄のインゴット\n";
        var preview = MergePlanner.Preview(g, ItemCsv.Read(csv).Items, incomingHasRecipes: false);
        preview.Entries.Single().Decision = MergeDecision.Update;
        var updated = MergePlanner.Apply(g, preview);
        Assert.Equal(2, updated.FindItem(Ingot)!.Recipes.Count);
        Assert.Equal(3, updated.FindRecipe(IngotRecipe)!.OutputQuantity);
    }

    [Fact]
    public void CSV内でIDが重複している場合は取り込みを中止する()
    {
        var g = Game();
        var csv = "id,name\nx1,A\nx1,B\n";
        var preview = MergePlanner.Preview(g, ItemCsv.Read(csv).Items, false);
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Errors, e => e.Contains("x1"));
        Assert.Throws<InvalidOperationException>(() => MergePlanner.Apply(g, preview));
    }

    [Fact]
    public void 同じ内容の行はIdenticalになり変更しない()
    {
        var g = Game();
        var preview = MergePlanner.Preview(g, ItemCsv.Read(ItemCsv.Export(g.Items)).Items, false);
        Assert.All(preview.Entries, e => Assert.Equal(MergeEntryKind.Identical, e.Kind));
        Assert.Empty(preview.NeedsDecision);
    }

    [Fact]
    public void JSONゲームデータの往復でアイコンとレシピを保持する()
    {
        var g = Game();
        g.FindItem(Sword)!.IconPng = Convert.ToBase64String(new byte[] { 137, 80, 78, 71 });
        var path = Path.Combine(Path.GetTempPath(), "CraftPlannerTests", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            JsonTransfer.ExportGameData(g, path);
            var back = JsonTransfer.ReadGameDataFile(path);
            Assert.Equal(JsonSerializer.Serialize(g, JsonOptions.Default), JsonSerializer.Serialize(back, JsonOptions.Default));
            Assert.Equal(g.FindItem(Sword)!.IconPng, back.FindItem(Sword)!.IconPng);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JSON統合でレシピも含めて競合を解決し素材参照を付け替える()
    {
        var g = Game();
        var incoming = Game();
        incoming.FindItem(Ore)!.Name = "鉄鉱石（別ゲーム版）";
        incoming.FindItem(Ingot)!.Recipes[0].OutputQuantity = 5;
        incoming.Items.Add(new Item { Id = "gem", Name = "宝石" });
        var rows = incoming.Items.Select((i, idx) => (idx + 1, i)).ToList();

        var preview = MergePlanner.Preview(g, rows, incomingHasRecipes: true);
        var oreEntry = preview.Entries.Single(e => e.Incoming.Id == Ore);
        var ingotEntry = preview.Entries.Single(e => e.Incoming.Id == Ingot);
        Assert.Contains(ingotEntry.RecipeChanges, c => c.Contains("置換"));
        Assert.Equal(MergeEntryKind.New, preview.Entries.Single(e => e.Incoming.Id == "gem").Kind);

        // 原石は新 ID で別アイテムとして取り込む → インゴットのレシピの素材参照も付け替わる
        oreEntry.Decision = MergeDecision.CreateNew;
        ingotEntry.Decision = MergeDecision.Update;
        var merged = MergePlanner.Apply(g, preview);

        var newOre = merged.Items.Single(i => i.Name == "鉄鉱石（別ゲーム版）");
        Assert.NotEqual(Ore, newOre.Id);
        var ingotRecipe = merged.FindRecipe(IngotRecipe)!;
        Assert.Equal(5, ingotRecipe.OutputQuantity);
        Assert.Contains(ingotRecipe.Materials, m => m.ItemId == newOre.Id);
        Assert.True(Validators.ValidateGameData(merged).IsValid);
        Assert.Equal(3, g.FindRecipe(IngotRecipe)!.OutputQuantity); // 元は不変
    }

    [Fact]
    public void 別ゲームデータの計画JSONは読み込みを拒否する()
    {
        var g = Game();
        var plan = EmptyPlan(g);
        var path = Path.Combine(Path.GetTempPath(), "CraftPlannerTests", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            JsonTransfer.ExportPlan(plan, path);
            Assert.Equal(plan.Id, JsonTransfer.ReadPlanFile(path, g.Id).Id);
            var ex = Assert.Throws<PlanGameMismatchException>(() => JsonTransfer.ReadPlanFile(path, "other-game"));
            Assert.Contains("元のゲームデータ", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CSVに無い列は既存の値を消さない()
    {
        var g = Game();
        g.FindItem(Ore)!.Description = "残すべき詳細";
        g.FindItem(Ore)!.Acquisition = "残すべき入手方法";
        var read = ItemCsv.Read($"id,name\n{Ore},鉄鉱石\n");
        Assert.Equal(new[] { "name" }, read.PresentFields.OrderBy(x => x));

        var preview = MergePlanner.Preview(g, read.Items, false, read.PresentFields);
        var entry = preview.Entries.Single();
        Assert.Single(entry.FieldChanges);                 // 名前の変更だけが差分になる
        entry.Decision = MergeDecision.Update;
        var updated = MergePlanner.Apply(g, preview);
        var ore = updated.FindItem(Ore)!;
        Assert.Equal("鉄鉱石", ore.Name);
        Assert.Equal("残すべき詳細", ore.Description);
        Assert.Equal("残すべき入手方法", ore.Acquisition);
    }

    [Fact]
    public void レシピCSVのエクスポート形式()
    {
        var csv = RecipeCsv.Export(Game());
        var rows = Csv.Parse(csv);
        Assert.Equal(RecipeCsv.Header, rows[0]);
        // 素材 0 種類のレシピは素材列が空の 1 行
        var wood = rows.Single(r => r[0] == WoodRecipe);
        Assert.Equal("", wood[5]);
        Assert.Equal(2, rows.Count(r => r[0] == IngotRecipe));
    }
}
