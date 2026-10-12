using CraftPlanner.Core.Calculation;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using static CraftPlanner.Core.Tests.TestData;

namespace CraftPlanner.Core.Tests;

public class CalculationTests
{
    private static (Plan Plan, PlanEditor Editor, GameData Game) Setup(GameData? game = null)
    {
        var g = game ?? Game();
        var plan = EmptyPlan(g);
        return (plan, new PlanEditor(plan, g), g);
    }

    private static ItemNode ChildOf(Plan plan, string parentId, string itemId)
        => plan.FindItemNode(plan.ChildEdgesOf(parentId).Single(e => e.MaterialItemId == itemId).ChildNodeId)!;

    [Fact]
    public void 基本的な素材ツリー()
    {
        var (plan, ed, g) = Setup();
        var sword = ed.AddItemNode(Sword, 1);
        ed.Expand(sword.Id);

        var r = PlanCalculator.Calculate(plan, g);

        Assert.Equal(1, r.Node(sword.Id)!.Crafts);
        Assert.Equal(3, r.Item(Ingot)!.Gross);
        Assert.Equal(1, r.Item(Ingot)!.Crafts);      // 3 個完成 → 1 回
        Assert.Equal(2, r.Item(Ore)!.Gross);
        Assert.Equal(1, r.Item(Coal)!.Gross);
        Assert.Equal(2, r.Item(Ore)!.Shortage);      // 末端素材は不足
        Assert.Equal(1, r.Item(Hilt)!.Gross);
        Assert.Equal(2, r.Item(Wood)!.Gross);
        Assert.Equal(2, r.Item(Wood)!.Crafts);       // 採取レシピ（素材 0）は制作回数として数える
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void 複数個完成するレシピと余剰()
    {
        var (plan, ed, g) = Setup();
        var ingot = ed.AddItemNode(Ingot, 7);
        ed.Expand(ingot.Id);

        var r = PlanCalculator.Calculate(plan, g);
        var t = r.Item(Ingot)!;
        Assert.Equal(3, t.Crafts);
        Assert.Equal(9, t.Produced);
        Assert.Equal(2, t.Surplus);
        Assert.Equal(6, r.Item(Ore)!.Gross);
        Assert.Equal(3, r.Item(Coal)!.Gross);
    }

    [Fact]
    public void 所持品による不足数の減少()
    {
        var (plan, ed, g) = Setup();
        var ingot = ed.AddItemNode(Ingot, 7);
        ed.Expand(ingot.Id);
        ed.SetInventory(Ingot, 4);
        ed.SetInventory(Ore, 1);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(3, r.Item(Ingot)!.Net);
        Assert.Equal(1, r.Item(Ingot)!.Crafts);
        Assert.Equal(2, r.Item(Ore)!.Gross);
        Assert.Equal(1, r.Item(Ore)!.Shortage);
        Assert.Equal(1, r.Item(Coal)!.Shortage);
    }

    [Fact]
    public void 企画書7_3の例_複数目標の需要集計()
    {
        // 鉄の剣に鉄インゴット 3 個、独立目標として鉄インゴット 5 個、所持 2 → 不足 6
        var (plan, ed, g) = Setup();
        var sword = ed.AddItemNode(Sword, 1);
        plan.ItemNodes.Single().SelectedRecipeId = SwordRecipe;
        sword.IsExpanded = true;
        // 子を手で作る（インゴットは未展開＝末端）
        var ingotChild = new ItemNode { Id = "n-ingot", ItemId = Ingot, SelectedRecipeId = IngotRecipe };
        var hiltChild = new ItemNode { Id = "n-hilt", ItemId = Hilt };
        plan.ItemNodes.Add(ingotChild);
        plan.ItemNodes.Add(hiltChild);
        plan.Edges.Add(new PlanEdge { Id = "e1", ParentNodeId = sword.Id, ChildNodeId = ingotChild.Id, MaterialItemId = Ingot });
        plan.Edges.Add(new PlanEdge { Id = "e2", ParentNodeId = sword.Id, ChildNodeId = hiltChild.Id, MaterialItemId = Hilt });
        ed.AddItemNode(Ingot, 5);
        ed.SetInventory(Ingot, 2);

        var r = PlanCalculator.Calculate(plan, g);
        var t = r.Item(Ingot)!;
        Assert.Equal(8, t.Gross);
        Assert.Equal(2, t.OnHand);
        Assert.Equal(6, t.Shortage);

        // 目標別の参考値
        Assert.Equal(2, r.Targets.Count);
        var swordTarget = r.Targets.Single(x => x.ItemId == Sword);
        var ingotLine = swordTarget.Lines.Single(l => l.ItemId == Ingot);
        Assert.Equal(3, ingotLine.Required);
        Assert.Equal(2, ingotLine.OnHand);
        Assert.Equal(1, ingotLine.ReferenceShortage);
        var ingotTarget = r.Targets.Single(x => x.ItemId == Ingot);
        Assert.Equal(5, ingotTarget.Lines.Single(l => l.ItemId == Ingot).Required);
    }

    [Fact]
    public void 制作余剰の再利用_同じレシピの需要をまとめて制作回数を求める()
    {
        var (plan, ed, g) = Setup();
        var a = ed.AddItemNode(Ingot, 4);
        var b = ed.AddItemNode(Ingot, 4);
        ed.Expand(a.Id);
        ed.Expand(b.Id);

        var r = PlanCalculator.Calculate(plan, g);
        // 別々なら 2 + 2 = 4 回だが、合算 8 個 → 3 回（9 個）で足りる
        Assert.Equal(3, r.Item(Ingot)!.Crafts);
        Assert.Equal(1, r.Item(Ingot)!.Surplus);
        Assert.Equal(6, r.Item(Ore)!.Gross);
        Assert.Equal(3, r.Node(a.Id)!.Crafts + r.Node(b.Id)!.Crafts);
    }

    [Fact]
    public void 制作余剰は同じアイテムの末端ノードの不足に充当される()
    {
        var (plan, ed, g) = Setup();
        var crafted = ed.AddItemNode(Ingot, 4);
        ed.Expand(crafted.Id);               // 4 個 → 2 回 → 6 個、余剰 2
        var leaf = ed.AddItemNode(Ingot, 1);  // 未展開の末端

        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(0, r.Node(leaf.Id)!.Shortage);
        Assert.Equal(0, r.Item(Ingot)!.Shortage);
        Assert.Equal(1, r.Item(Ingot)!.Surplus);
        Assert.Equal(2, r.Item(Ingot)!.Crafts);
    }

    [Fact]
    public void 異なるレシピの選択()
    {
        var (plan, ed, g) = Setup();
        var ingot = ed.AddItemNode(Ingot, 7);
        ed.SetSelectedRecipe(ingot.Id, IngotRecipeAlt);
        ed.Expand(ingot.Id);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(7, r.Item(Ingot)!.Crafts);
        Assert.Equal(7, r.Item(Ore)!.Gross);
        Assert.Null(r.Item(Coal));
    }

    [Fact]
    public void レシピ変更で再計算され共通素材の子ノードは保持される()
    {
        var (plan, ed, g) = Setup();
        var ingot = ed.AddItemNode(Ingot, 3);
        ed.Expand(ingot.Id);
        var oreBefore = ChildOf(plan, ingot.Id, Ore);
        Assert.NotNull(plan.ChildEdgesOf(ingot.Id).SingleOrDefault(e => e.MaterialItemId == Coal));

        ed.SetSelectedRecipe(ingot.Id, IngotRecipeAlt);

        var oreAfter = ChildOf(plan, ingot.Id, Ore);
        Assert.Equal(oreBefore.Id, oreAfter.Id);  // 位置を保つため同じノード
        Assert.Null(plan.ChildEdgesOf(ingot.Id).SingleOrDefault(e => e.MaterialItemId == Coal));
        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(3, r.Item(Ore)!.Gross);
    }

    [Fact]
    public void ノード単位の必要数量変更は同じアイテムの別ノードに影響しない()
    {
        var (plan, ed, g) = Setup();
        var s1 = ed.AddItemNode(Sword, 1);
        var s2 = ed.AddItemNode(Sword, 1);
        ed.Expand(s1.Id);
        ed.Expand(s2.Id);
        ed.SetRequiredQuantity(s1.Id, 2);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(2, r.Node(s1.Id)!.Demand);
        Assert.Equal(1, r.Node(s2.Id)!.Demand);
        Assert.Equal(9, r.Item(Ingot)!.Gross);
        Assert.Throws<InvalidOperationException>(() => ed.SetRequiredQuantity(ChildOf(plan, s1.Id, Ingot).Id, 10));
    }

    [Fact]
    public void 接続の切断と再接続()
    {
        var (plan, ed, g) = Setup();
        var sword = ed.AddItemNode(Sword, 1);
        ed.Expand(sword.Id);
        var ingotEdge = plan.ChildEdgesOf(sword.Id).Single(e => e.MaterialItemId == Ingot);

        ed.Sever(ingotEdge.Id);
        var r1 = PlanCalculator.Calculate(plan, g);
        Assert.Null(r1.Item(Ingot));   // 下流の計算が止まる
        Assert.Null(r1.Item(Ore));
        Assert.NotNull(r1.Item(Hilt)); // 無関係な部分は継続
        Assert.Single(plan.SeveredEdges);
        Assert.Equal(1, r1.Node(sword.Id)!.Crafts);

        var outcome = ed.Reconnect(sword.Id, Ingot);
        Assert.Empty(plan.SeveredEdges);
        Assert.NotEmpty(outcome.CreatedNodeIds);
        var r2 = PlanCalculator.Calculate(plan, g);
        Assert.Equal(3, r2.Item(Ingot)!.Gross);
        Assert.Equal(2, r2.Item(Ore)!.Gross);
    }

    [Fact]
    public void 循環レシピは無限再帰せず不確定として扱う()
    {
        var g = CyclicGame();
        var (plan, ed, _) = Setup(g);
        var a = ed.AddItemNode("a", 1);
        var outcome = ed.Expand(a.Id);

        Assert.Contains(outcome.Notices, n => n.Contains("循環"));
        Assert.Contains(plan.ItemNodes, n => n.IsCycleTerminal);
        // A → B → A'(循環地点、1 世代だけ) → B'', X''
        var cycleNode = plan.ItemNodes.Single(n => n.IsCycleTerminal);
        Assert.Equal("a", cycleNode.ItemId);
        Assert.Equal(2, plan.ChildEdgesOf(cycleNode.Id).Count());
        foreach (var e in plan.ChildEdgesOf(cycleNode.Id))
            Assert.False(plan.FindItemNode(e.ChildNodeId)!.IsExpanded);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.True(r.Item("a")!.IsCyclic);
        Assert.True(r.Item("a")!.IsUncertain);
        Assert.True(r.Item("b")!.IsUncertain);
        Assert.True(r.Item("x")!.IsUncertain);  // 循環の下流
        Assert.True(r.Node(cycleNode.Id)!.IsCycleTerminal);
        Assert.Equal(2, r.Item("x")!.Gross);     // A と A' から 1 個ずつ
    }

    [Fact]
    public void 循環があっても無関係な部分は確定値のまま計算を続ける()
    {
        var g = CyclicGame();
        g.Items.Add(new Item { Id = "y", Name = "Y", Recipes = { new Recipe { Id = "r-y", OutputQuantity = 1, Materials = { M("x", 2) } } } });
        var (plan, ed, _) = Setup(g);
        ed.Expand(ed.AddItemNode("a", 1).Id);
        var y = ed.AddItemNode("y", 3);
        ed.Expand(y.Id);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.False(r.Node(y.Id)!.IsUncertain);
        Assert.Equal(3, r.Node(y.Id)!.Crafts);
        // x は循環側の需要も受けるため不確定だが、y 自体は確定
        Assert.True(r.Item("x")!.IsUncertain);
    }

    [Fact]
    public void 必要素材が0種類のレシピ()
    {
        var (plan, ed, g) = Setup();
        var wood = ed.AddItemNode(Wood, 5);
        ed.Expand(wood.Id);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.True(r.Node(wood.Id)!.IsCrafted);
        Assert.Equal(5, r.Node(wood.Id)!.Crafts);
        Assert.Equal(0, r.Item(Wood)!.Shortage);
        Assert.Empty(plan.Edges);
        Assert.Empty(r.Node(wood.Id)!.Warnings);
    }

    [Fact]
    public void 同じアイテムIDを参照する複数ノードは統合せず所持数は1回だけ数える()
    {
        var (plan, ed, g) = Setup();
        var n1 = ed.AddItemNode(Ore, 3);
        var n2 = ed.AddItemNode(Ore, 4);
        ed.SetInventory(Ore, 5);

        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(2, plan.ItemNodes.Count);
        Assert.Equal(7, r.Item(Ore)!.Gross);
        Assert.Equal(2, r.Item(Ore)!.Shortage);
        Assert.Equal(2, r.Node(n1.Id)!.Shortage + r.Node(n2.Id)!.Shortage);
        Assert.Equal(3, r.Node(n1.Id)!.Demand);
        Assert.Equal(4, r.Node(n2.Id)!.Demand);
    }

    [Fact]
    public void ノードの位置や登録順で計算結果が変わらない()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 2);
        ed.Expand(s.Id);
        var i = ed.AddItemNode(Ingot, 5);
        ed.Expand(i.Id);
        ed.AddItemNode(Ore, 3);
        ed.SetInventory(Ingot, 2);
        ed.SetInventory(Ore, 4);
        var r1 = PlanCalculator.Calculate(plan, g);

        var shuffled = plan.Clone();
        shuffled.ItemNodes.Reverse();
        shuffled.Edges.Reverse();
        var rnd = new Random(42);
        foreach (var n in shuffled.ItemNodes) { n.X = rnd.Next(0, 5000); n.Y = rnd.Next(0, 5000); }
        var r2 = PlanCalculator.Calculate(shuffled, g);

        foreach (var kv in r1.Items)
        {
            var o = r2.Item(kv.Key)!;
            Assert.Equal(kv.Value.Gross, o.Gross);
            Assert.Equal(kv.Value.Crafts, o.Crafts);
            Assert.Equal(kv.Value.Shortage, o.Shortage);
            Assert.Equal(kv.Value.Surplus, o.Surplus);
        }
        foreach (var kv in r1.Nodes)
        {
            var o = r2.Node(kv.Key)!;
            Assert.Equal(kv.Value.Demand, o.Demand);
            Assert.Equal(kv.Value.Crafts, o.Crafts);
            Assert.Equal(kv.Value.Shortage, o.Shortage);
        }
    }

    [Fact]
    public void 折りたたみは計算結果を変えない()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);
        var before = PlanCalculator.Calculate(plan, g);
        ed.SetCollapsed(s.Id, true);
        var after = PlanCalculator.Calculate(plan, g);
        Assert.Equal(before.Item(Ore)!.Gross, after.Item(Ore)!.Gross);
        Assert.Equal(before.Nodes.Count, after.Nodes.Count);
    }

    [Fact]
    public void 制作可能の判定()
    {
        var (plan, ed, g) = Setup();
        var s = ed.AddItemNode(Sword, 1);
        ed.Expand(s.Id);

        ed.SetInventory(Ingot, 3);
        ed.SetInventory(Hilt, 1);
        Assert.Equal(Craftability.Direct, PlanCalculator.Calculate(plan, g).Node(s.Id)!.Craftability);

        ed.SetInventory(Ingot, 0);
        ed.SetInventory(Hilt, 0);
        ed.SetInventory(Ore, 2);
        ed.SetInventory(Coal, 1);
        ed.SetInventory(Wood, 2);
        var r = PlanCalculator.Calculate(plan, g);
        Assert.Equal(Craftability.Indirect, r.Node(s.Id)!.Craftability);
        Assert.Equal(Craftability.Direct, r.Node(ChildOf(plan, s.Id, Ingot).Id)!.Craftability);

        ed.SetInventory(Coal, 0);
        Assert.Equal(Craftability.None, PlanCalculator.Calculate(plan, g).Node(s.Id)!.Craftability);
    }

    [Fact]
    public void 比率配分は合計を保ち需要0には配らない()
    {
        var alloc = PlanCalculator.AllocateProportional(5, new long[] { 0, 3, 7 });
        Assert.Equal(5, alloc.Sum());
        Assert.Equal(0, alloc[0]);
        Assert.Equal(0, PlanCalculator.AllocateProportional(0, new long[] { 1, 2 }).Sum());
        Assert.Equal(3, PlanCalculator.CeilDiv(7, 3));
        Assert.Equal(0, PlanCalculator.CeilDiv(0, 3));
    }
}
