using System.Text.Json;
using CraftPlanner.Core.History;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using CraftPlanner.Core.Storage;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public class HistoryTests
{
    private static string Json(Plan p) => JsonSerializer.Serialize(p, JsonOptions.Default);

    [Fact]
    public void 削除をUndoで元に戻しRedoでやり直せる()
    {
        var g = Game();
        var plan = EmptyPlan(g);
        var ed = new PlanEditor(plan, g);
        var history = new HistoryManager<Plan>(p => p.Clone());
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var before = Json(plan);

        history.Record(plan);
        ed.DeleteNodes(new[] { s.Id });
        var after = Json(plan);
        Assert.NotEqual(before, after);

        plan = history.Undo(plan);
        Assert.Equal(before, Json(plan));
        Assert.True(Validators.ValidatePlan(plan, g).IsValid);

        plan = history.Redo(plan);
        Assert.Equal(after, Json(plan));
        Assert.True(Validators.ValidatePlan(plan, g).IsValid);
    }

    [Fact]
    public void 新しい編集でRedo履歴は破棄される()
    {
        var plan = EmptyPlan(Game());
        var history = new HistoryManager<Plan>(p => p.Clone());
        history.Record(plan);
        plan.Name = "A";
        plan = history.Undo(plan);
        Assert.True(history.CanRedo);
        history.Record(plan);
        plan.Name = "B";
        Assert.False(history.CanRedo);
        Assert.Throws<InvalidOperationException>(() => history.Redo(plan));
    }

    [Fact]
    public void 履歴は上限を超えると古いものから捨てる()
    {
        var plan = EmptyPlan(Game());
        var history = new HistoryManager<Plan>(p => p.Clone(), capacity: 3);
        for (var i = 0; i < 10; i++) { history.Record(plan); plan.Name = i.ToString(); }
        Assert.Equal(3, history.UndoCount);
    }

    [Fact]
    public void スナップショットは独立した複製であり後の編集に影響されない()
    {
        var g = Game();
        var plan = EmptyPlan(g);
        var ed = new PlanEditor(plan, g);
        var history = new HistoryManager<Plan>(p => p.Clone());
        var memo = ed.AddMemoNode(0, 0, "元");
        history.Record(plan);
        ed.SetMemoText(memo.Id, "変更");
        ed.SetInventory(Ore, 9);
        var restored = history.Undo(plan);
        Assert.Equal("元", restored.FindMemoNode(memo.Id)!.Text);
        Assert.False(restored.Inventory.ContainsKey(Ore));
    }
}
