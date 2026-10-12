using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public class TreeLayoutTests
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

    private static double CenterY(Plan plan, string id)
    {
        var b = new AutoLayout().BoundsOf(plan, id);
        return b.Y + b.Height / 2;
    }

    [Fact]
    public void 展開した部分木は列がそろい親が子ブロックの中央に来る()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1, 100, 300);
        ed.Expand(s.Id);

        AssertNoOverlap(plan);
        Assert.Equal((100.0, 300.0), (s.X, s.Y)); // 根は動かない

        var ingot = plan.FindItemNode(plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot).ChildNodeId)!;
        var hilt = plan.FindItemNode(plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Hilt).ChildNodeId)!;
        Assert.Equal(ingot.X, hilt.X);                                   // 同じ深さは同じ列
        Assert.True(ingot.X > s.X);
        Assert.True(ingot.Y < hilt.Y);                                   // レシピの素材順（インゴット → 柄）

        // 親の中心は子ブロック（最初の子の上端〜最後の子の下端）の中心
        var layout = new AutoLayout();
        var top = layout.BoundsOf(plan, ingot.Id).Y;
        var bottom = layout.BoundsOf(plan, hilt.Id).Bottom;
        Assert.Equal((top + bottom) / 2, CenterY(plan, s.Id), 1.0);

        // インゴットの子（原石・石炭）はインゴットを中心に並ぶ
        var ore = plan.FindItemNode(plan.ChildEdgesOf(ingot.Id).Single(e => e.MaterialItemId == Ore).ChildNodeId)!;
        var coal = plan.FindItemNode(plan.ChildEdgesOf(ingot.Id).Single(e => e.MaterialItemId == Coal).ChildNodeId)!;
        Assert.Equal(ore.X, coal.X);
        Assert.Equal((layout.BoundsOf(plan, ore.Id).Y + layout.BoundsOf(plan, coal.Id).Bottom) / 2, CenterY(plan, ingot.Id), 1.0);
    }

    [Fact]
    public void 展開時に既存ノードと重なる場合は子孫だけをずらす()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1, 0, 0);
        // 展開先の右側に無関係なノードを置いておく
        var blocker = ed.AddItemNode(Ore, 1, AutoLayout.DefaultItemNodeWidth + AutoLayout.GapX, 0);
        ed.Expand(s.Id);

        AssertNoOverlap(plan);
        Assert.Equal((0.0, 0.0), (s.X, s.Y));
        Assert.Equal((AutoLayout.DefaultItemNodeWidth + AutoLayout.GapX, 0.0), (blocker.X, blocker.Y)); // 無関係なノードは動かない
    }

    [Fact]
    public void 全体整列はすべてのツリーを縦に積みメモは動かさない()
    {
        var (plan, ed, _) = Setup();
        var a = ed.AddItemNode(Sword, 1, 500, 500);
        ed.Expand(a.Id);
        var b = ed.AddItemNode(Ingot, 3, 0, 0);
        ed.Expand(b.Id);
        var memo = ed.AddMemoNode(900, 900, "メモ");
        // わざと重ねる
        foreach (var n in plan.ItemNodes) { n.X = 10; n.Y = 10; }

        ed.ArrangeAll();

        AssertNoOverlap(plan);
        Assert.Equal((900.0, 900.0), (memo.X, memo.Y));
        var roots = plan.TargetNodes().ToList();
        Assert.All(roots, r => Assert.Equal(40.0, r.X));           // 目標はすべて左端の列
        Assert.True(roots[0].Y != roots[1].Y);
    }

    [Fact]
    public void 再接続で生成した部分木は親の右側にブロックとして置かれる()
    {
        var (plan, ed, _) = Setup();
        var s = ed.AddItemNode(Sword, 1, 0, 0);
        ed.Expand(s.Id);
        var edge = plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot);
        ed.Sever(edge.Id);
        ed.Reconnect(s.Id, Ingot);

        AssertNoOverlap(plan);
        var ingot = plan.FindItemNode(plan.ChildEdgesOf(s.Id).Single(e => e.MaterialItemId == Ingot).ChildNodeId)!;
        Assert.True(ingot.X >= s.X + AutoLayout.DefaultItemNodeWidth + AutoLayout.GapX - 1);
        var ore = plan.FindItemNode(plan.ChildEdgesOf(ingot.Id).Single(e => e.MaterialItemId == Ore).ChildNodeId)!;
        Assert.True(ore.X > ingot.X);
    }
}
