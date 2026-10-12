using System.Text.Json;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using CraftPlanner.Core.Storage;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public sealed class TempRoot : IDisposable
{
    public AppPaths Paths { get; }
    public TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CraftPlannerTests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(dir);
        Paths.EnsureDirectories();
    }
    public void Dispose()
    {
        try { Directory.Delete(Paths.Root, recursive: true); } catch { }
    }
}

public class StorageTests
{
    [Fact]
    public void ゲームデータの保存と読み込み()
    {
        using var tmp = new TempRoot();
        var repo = new GameDataRepository(new SafeFileStore(tmp.Paths));
        var g = Game();
        g.FindItem(Ore)!.IconPng = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        repo.Save(g);

        var loaded = repo.Load(g.Id);
        Assert.Equal(g.Items.Count, loaded.Items.Count);
        Assert.Equal(g.FindItem(Ore)!.IconPng, loaded.FindItem(Ore)!.IconPng);
        Assert.Equal(3, loaded.FindRecipe(IngotRecipe)!.OutputQuantity);
        Assert.False(File.Exists(repo.FileOf(g.Id) + ".tmp"));
    }

    [Fact]
    public void 制作計画の保存と読み込みで状態が復元される()
    {
        using var tmp = new TempRoot();
        var repo = new PlanRepository(new SafeFileStore(tmp.Paths));
        var g = Game();
        var plan = EmptyPlan(g);
        var ed = new PlanEditor(plan, g);
        var s = ed.AddItemNode(Sword, 2);
        ed.Expand(s.Id);
        ed.SetCollapsed(s.Id, true);
        ed.SetInventory(Ore, 5);
        ed.Sever(plan.ChildEdgesOf(s.Id).First(e => e.MaterialItemId == Hilt).Id);
        var memo = ed.AddMemoNode(5, 6, "メモ\n改行");
        ed.SetMemoColor(memo.Id, "green");
        plan.DisplayMode = DisplayMode.Simple;
        plan.View = new CanvasView { OffsetX = 10, OffsetY = 20, Zoom = 1.5 };
        repo.Save(plan);

        var loaded = repo.Load(plan.Id);
        Assert.Equal(JsonSerializer.Serialize(plan, JsonOptions.Default), JsonSerializer.Serialize(loaded, JsonOptions.Default));
        Assert.True(loaded.FindItemNode(s.Id)!.IsCollapsed);
        Assert.Single(loaded.SeveredEdges);
        Assert.Equal(5, loaded.Inventory[Ore]);
        Assert.Equal("green", loaded.MemoNodes.Single().ColorKey);
        Assert.Equal(1.5, loaded.View.Zoom);
    }

    [Fact]
    public void 破損したJSONは検出され元ファイルは上書きされない()
    {
        using var tmp = new TempRoot();
        var repo = new GameDataRepository(new SafeFileStore(tmp.Paths));
        var file = repo.FileOf("broken");
        File.WriteAllText(file, "{ this is not json");

        var ex = Assert.Throws<LoadException>(() => repo.Load("broken"));
        Assert.Contains("JSON", ex.Failure.Reason);
        Assert.NotEmpty(ex.Failure.Recovery);
        Assert.Equal("{ this is not json", File.ReadAllText(file));

        var report = repo.LoadAll();
        Assert.Single(report.Failures);
        Assert.Empty(report.Loaded);
    }

    [Fact]
    public void 新しい形式バージョンは読み込みを拒否する()
    {
        using var tmp = new TempRoot();
        var store = new SafeFileStore(tmp.Paths);
        var repo = new GameDataRepository(store);
        var g = Game();
        repo.Save(g);
        var text = File.ReadAllText(repo.FileOf(g.Id)).Replace("\"formatVersion\": 1", "\"formatVersion\": 99");
        File.WriteAllText(repo.FileOf(g.Id), text);

        var ex = Assert.Throws<LoadException>(() => repo.Load(g.Id));
        Assert.Contains("バージョン", ex.Failure.Reason);
    }

    [Fact]
    public void 保存途中で検証に失敗しても既存ファイルは残る()
    {
        using var tmp = new TempRoot();
        var store = new SafeFileStore(tmp.Paths);
        var file = tmp.Paths.GameFile("g");
        store.WriteJson(file, new GameData { Id = "g", Name = "元" }, Validators.ValidateGameData);
        var original = File.ReadAllText(file);

        var broken = new GameData { Id = "g", Name = "壊れた" };
        broken.Items.Add(new Item { Id = "", Name = "ID なし" });
        Assert.Throws<InvalidDataException>(() => store.WriteJson(file, broken, Validators.ValidateGameData));

        Assert.Equal(original, File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void 上書き時にバックアップが作られ世代数が守られる()
    {
        using var tmp = new TempRoot();
        var store = new SafeFileStore(tmp.Paths, () => 3);
        var repo = new GameDataRepository(store);
        var g = Game();
        for (var i = 0; i < 6; i++)
        {
            g.Name = "v" + i;
            repo.Save(g);
            Thread.Sleep(5);
        }
        var backups = repo.Backups(g.Id);
        Assert.Equal(3, backups.Count);
        // 最新のバックアップは直前の内容（v4）
        var newest = JsonSerializer.Deserialize<GameData>(File.ReadAllText(backups[0].FilePath), JsonOptions.Default)!;
        Assert.Equal("v4", newest.Name);
        Assert.Equal("v5", repo.Load(g.Id).Name);
    }

    [Fact]
    public void バックアップから復元でき復元前の状態も退避される()
    {
        using var tmp = new TempRoot();
        var store = new SafeFileStore(tmp.Paths);
        var repo = new GameDataRepository(store);
        var g = Game();
        g.Name = "最初";
        repo.Save(g);
        Thread.Sleep(5);
        g.Name = "2 回目";
        repo.Save(g);
        var backup = repo.Backups(g.Id).Single();

        repo.Restore(g.Id, backup.FilePath);

        Assert.Equal("最初", repo.Load(g.Id).Name);
        var afterBackups = repo.Backups(g.Id);
        Assert.Equal(2, afterBackups.Count);
        var preserved = JsonSerializer.Deserialize<GameData>(File.ReadAllText(afterBackups[0].FilePath), JsonOptions.Default)!;
        Assert.Equal("2 回目", preserved.Name);
    }

    [Fact]
    public void 壊れたバックアップでは復元しない()
    {
        using var tmp = new TempRoot();
        var store = new SafeFileStore(tmp.Paths);
        var repo = new GameDataRepository(store);
        var g = Game();
        repo.Save(g);
        var dir = tmp.Paths.BackupDirFor(repo.FileOf(g.Id));
        Directory.CreateDirectory(dir);
        var bad = Path.Combine(dir, "00000000-000000-000.json");
        File.WriteAllText(bad, "broken");

        Assert.Throws<LoadException>(() => repo.Restore(g.Id, bad));
        Assert.Equal(g.Items.Count, repo.Load(g.Id).Items.Count);
    }

    [Fact]
    public void 設定の保存と復元_開いていたタブ()
    {
        using var tmp = new TempRoot();
        var repo = new SettingsRepository(new SafeFileStore(tmp.Paths));
        var s = new AppSettings { OpenPlanIds = { "p1", "p2" }, ActivePlanId = "p2", LastGameDataId = "g" };
        repo.Save(s);
        var loaded = repo.LoadOrDefault(out var failure);
        Assert.Null(failure);
        Assert.Equal(new[] { "p1", "p2" }, loaded.OpenPlanIds);
        Assert.Equal("p2", loaded.ActivePlanId);
    }

    [Fact]
    public void 壊れた設定は既定値を返しつつ失敗を報告する()
    {
        using var tmp = new TempRoot();
        File.WriteAllText(tmp.Paths.SettingsFile, "???");
        var repo = new SettingsRepository(new SafeFileStore(tmp.Paths));
        var loaded = repo.LoadOrDefault(out var failure);
        Assert.NotNull(failure);
        Assert.Empty(loaded.OpenPlanIds);
        Assert.Equal("???", File.ReadAllText(tmp.Paths.SettingsFile)); // 上書きしない
    }

    [Fact]
    public void アプリ領域外への書き込みは拒否される()
    {
        using var tmp = new TempRoot();
        var store = new SafeFileStore(tmp.Paths);
        var outside = Path.Combine(Path.GetTempPath(), "CraftPlannerOutside.json");
        Assert.Throws<UnauthorizedAccessException>(() => store.WriteJson(outside, new AppSettings()));
        Assert.Throws<UnauthorizedAccessException>(() => tmp.Paths.BackupDirFor(outside));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void 削除は事前にバックアップを残す()
    {
        using var tmp = new TempRoot();
        var repo = new PlanRepository(new SafeFileStore(tmp.Paths));
        var plan = EmptyPlan(Game());
        repo.Save(plan);
        repo.Delete(plan.Id);
        Assert.False(File.Exists(repo.FileOf(plan.Id)));
        Assert.Single(repo.Backups(plan.Id));
    }
}
