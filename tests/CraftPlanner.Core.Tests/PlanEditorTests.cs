using CraftPlanner.Core.Calculation;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public class PlanEditorTests
{
    private static (Plan Plan, PlanEditor Editor, GameData Game) Setup()
    {
        var g = Game();
        var plan = EmptyPlan(g);
        return (plan, new PlanEditor(plan, g), g);
    }

    private static void AssertNoOverlap(Plan plan)
    {
        var layout = new AutoLayout();
        var all = layout.AllBounds(plan).ToList();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                Assert.False(all[i].Bounds.Intersects(all[j].Bounds), $"{all[i].Id} と {all[j].Id} が重なっています");
    }

    [Fact]
    public void 展開で生成されたノードは重ならず製品の右側に並ぶ()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        var outcome = ed.Expand(s.Id);

        Assert.False(outcome.HadOverlap);
        AssertNoOverlap(plan);
        foreach (var e in plan.Edges)
        {
            var p = plan.FindItemNode(e.ParentNodeId)!;
            var c = plan.FindItemNode(e.ChildNodeId)!;
            Assert.True(c.X > p.X, "素材は製品の右側に置く");
        }
        // 剣 → インゴット, 柄 → 原石, 石炭, 木材
        Assert.Equal(6, plan.ItemNodes.Count);
    }

    [Fact]
    public void 追加しただけでは展開されない()
    {
        var (plan, ed, _) = Setup();
        ed.AddItemNode(Sword, 1);
        Assert.Single(plan.ItemNodes);
        Assert.Empty(plan.Edges);
    }

    [Fact]
    public void 新規目標は既存ノードと重ならない位置に置かれる()
    {
        var (plan, ed, _) = Setup();
        for (var i = 0; i < 5; i++) ed.AddItemNode(Ore, 1);
        ed.Expand(ed.AddItemNode(Sword, 1).Id);
        AssertNoOverlap(plan);
    }

    [Fact]
    public void 空き場所がない場合は既存ノードを動かさず通知する()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1, 0, 0);
        // 右側を巨大なメモで塞ぐ
        var memo = ed.AddMemoNode(100, -100000);
        ed.ResizeMemo(memo.Id, 100000, 200000);

        var outcome = ed.Expand(s.Id);

        Assert.True(outcome.HadOverlap);
        Assert.Contains(outcome.Notices, n => n.Contains("重なって"));
        Assert.Equal((0.0, 0.0), (s.X, s.Y));
        Assert.Equal(100, memo.X);
    }

    [Fact]
    public void 折りたたみと再展開で位置と接続を保持する()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var snapshot = plan.ItemNodes.ToDictionary(n => n.Id, n => (n.X, n.Y));
        var edgeCount = plan.Edges.Count;

        ed.SetCollapsed(s.Id, true);
        Assert.True(plan.FindItemNode(s.Id)!.IsCollapsed);
        Assert.Equal(edgeCount, plan.Edges.Count);
        ed.SetCollapsed(s.Id, false);
        foreach (var n in plan.ItemNodes) Assert.Equal(snapshot[n.Id], (n.X, n.Y));
    }

    [Fact]
    public void 切断は下流ノードを削除し再接続で再生成する_無関係なノードは動かない()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var hilt = plan.FindItemNode(plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Hilt).ChildNodeId)!;
        var hiltPos = (hilt.X, hilt.Y);
        var ingotEdge = plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot);

        ed.Sever(ingotEdge.Id);
        Assert.Equal(3, plan.ItemNodes.Count);  // 剣, 柄, 木材
        Assert.DoesNotContain(plan.ItemNodes, n => n.ItemId == Ore);

        ed.Reconnect(s.Id, Ingot);
        Assert.Equal(6, plan.ItemNodes.Count);
        Assert.Equal(hiltPos, (hilt.X, hilt.Y));
        AssertNoOverlap(plan);
    }

    [Fact]
    public void ノード削除は子孫と接続線も削除し親には切断記録を残す()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var ingot = plan.FindItemNode(plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot).ChildNodeId)!;
        var ore = plan.FindItemNode(plan.ChildEdgesOf(ingot.Id).Single(e => e.MaterialItemId == Ore).ChildNodeId)!;
        var r = PlanCalculator.Calculate(plan, g);

        ed.DeleteNodes(new[] { ingot.Id }, id => r.Node(id)!.Demand);

        Assert.Null(plan.FindItemNode(ingot.Id));
        Assert.DoesNotContain(plan.Edges, e => e.ParentNodeId == ingot.Id || e.ChildNodeId == ingot.Id);
        Assert.Contains(plan.SeveredEdges, sv => sv.ParentNodeId == s.Id && sv.MaterialItemId == Ingot);
        // 子孫（原石など）もまとめて削除される
        Assert.Null(plan.FindItemNode(ore.Id));
        Assert.Equal(3, plan.ItemNodes.Count); // 剣・柄・木材
        // アイテムマスターは変更されない
        Assert.NotNull(g.FindItem(Ingot));
        Assert.Equal(6, g.Items.Count);
    }

    [Fact]
    public void 接続線の削除は選択した線だけを対象にする()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var edge = plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot);
        var count = plan.ItemNodes.Count;
        var r = PlanCalculator.Calculate(plan, g);

        ed.DeleteEdge(edge.Id, id => r.Node(id)!.Demand);

        Assert.Equal(count, plan.ItemNodes.Count);
        Assert.True(plan.IsTarget(edge.ChildNodeId));
        Assert.Equal(3, plan.FindItemNode(edge.ChildNodeId)!.RequiredQuantity);
        Assert.Contains(plan.SeveredEdges, sv => sv.ParentNodeId == s.Id && sv.MaterialItemId == Ingot);
    }

    [Fact]
    public void 手動接続の検証()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        var ingot = ed.AddItemNode(Ingot, 1);
        var ore = ed.AddItemNode(Ore, 1);

        Assert.Throws<InvalidOperationException>(() => ed.Connect(s.Id, ore.Id)); // 剣の素材ではない
        var outcome = ed.Connect(s.Id, ingot.Id);
        Assert.True(plan.FindItemNode(s.Id)!.IsExpanded);
        Assert.False(plan.IsTarget(ingot.Id));
        Assert.Contains(plan.SeveredEdges, sv => sv.ParentNodeId == s.Id && sv.MaterialItemId == Hilt); // 残りは切断扱い
        Assert.Throws<InvalidOperationException>(() => ed.Connect(s.Id, ingot.Id));       // 既に親あり
        Assert.Throws<InvalidOperationException>(() => ed.Connect(ingot.Id, s.Id));       // 循環
        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(3, r.Item(Ingot)!.Gross);
    }

    [Fact]
    public void メモノードの編集()
    {
        var (plan, ed, g) = Setup();
        var memo = ed.AddMemoNode(10, 20, "はじめ");
        ed.SetMemoText(memo.Id, "変更後");
        ed.SetMemoColor(memo.Id, "blue");
        ed.ResizeMemo(memo.Id, 300, 150);
        ed.MoveNode(memo.Id, 50, 60);
        var m = plan.FindMemoNode(memo.Id)!;
        Assert.Equal("変更後", m.Text);
        Assert.Equal("blue", m.ColorKey);
        Assert.Equal((300, 150), (m.Width, m.Height));
        Assert.Equal((50, 60), (m.X, m.Y));
        // 計算に影響しない
        var r = PlanCalculator.Calculate(plan, g);
        Assert.Empty(r.Nodes);
        ed.DeleteNodes(new[] { memo.Id });
        Assert.Empty(plan.MemoNodes);
    }

    [Fact]
    public void コピーと貼り付けは新しいIDで複製する()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 2);
        ed.Expand(s.Id);
        var memo = ed.AddMemoNode(0, 0, "メモ");
        var ids = plan.ItemNodes.Select(n => n.Id).Append(memo.Id).ToList();
        var r = PlanCalculator.Calculate(plan, g);

        var data = ed.Copy(ids, id => r.Node(id)!.Demand);
        var created = ed.Paste(data, 30, 30);

        Assert.Equal(ids.Count, created.Count);
        Assert.Equal(12, plan.ItemNodes.Count);
        Assert.Equal(2, plan.MemoNodes.Count);
        Assert.Empty(created.Intersect(ids));
        var r2 = PlanCalculator.Calculate(plan, g);
        Assert.Equal(r.Item(Ore)!.Gross * 2, r2.Item(Ore)!.Gross);
    }

    [Fact]
    public void 部分的にコピーした子ノードは目標になり需要を引き継ぐ()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var ingot = plan.FindItemNode(plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot).ChildNodeId)!;
        var r = PlanCalculator.Calculate(plan, g);

        var created = ed.Paste(ed.Copy(new[] { ingot.Id }, id => r.Node(id)!.Demand));

        var pasted = plan.FindItemNode(created.Single())!;
        Assert.True(plan.IsTarget(pasted.Id));
        Assert.Equal(3, pasted.RequiredQuantity);
        // 子がコピーされていない素材は切断扱い（再接続できる）
        Assert.Equal(2, plan.SeveredEdges.Count(sv => sv.ParentNodeId == pasted.Id));
    }

    [Fact]
    public void 整列は指定した部分木だけを動かす()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var other = ed.AddItemNode(Ore, 1, 2000, 2000);
        foreach (var n in plan.ItemNodes.Where(n => n.Id != s.Id && n.Id != other.Id)) { n.X = 0; n.Y = 0; }

        var outcome = ed.ArrangeSubtree(s.Id);

        Assert.False(outcome.HadOverlap);
        AssertNoOverlap(plan);
        Assert.Equal((2000, 2000), (other.X, other.Y));
    }
}
