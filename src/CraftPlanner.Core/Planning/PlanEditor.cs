using CraftPlanner.Core.Ids;
using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Planning;

/// <summary>編集操作の結果。ユーザーに伝えるべき通知（重なり・循環など）を持つ。</summary>
public sealed class EditOutcome
{
    public List<string> Notices { get; } = new();
    public List<string> CreatedNodeIds { get; } = new();
    public bool HadOverlap { get; set; }

    public void Merge(EditOutcome other)
    {
        Notices.AddRange(other.Notices);
        CreatedNodeIds.AddRange(other.CreatedNodeIds);
        HadOverlap |= other.HadOverlap;
    }
}

/// <summary>コピー＆ペースト用のデータ。選択ノードと、その中で閉じている接続だけを持つ。</summary>
public sealed class PlanClipboardData
{
    public List<ItemNode> ItemNodes { get; set; } = new();
    public List<MemoNode> MemoNodes { get; set; } = new();
    public List<PlanEdge> Edges { get; set; } = new();
    public bool IsEmpty => ItemNodes.Count == 0 && MemoNodes.Count == 0;
}

/// <summary>
/// 制作計画に対する編集操作をまとめたクラス。Plan モデルを直接書き換える。
/// Undo のためのスナップショットは呼び出し側（ビューモデル）が操作前に記録する。
/// ここではアイテムマスター（GameData）を一切変更しない。
/// </summary>
public sealed class PlanEditor
{
    public Plan Plan { get; }
    public GameData Game { get; }
    public AutoLayout Layout { get; }

    public PlanEditor(Plan plan, GameData game, AutoLayout? layout = null)
    {
        Plan = plan;
        Game = game;
        Layout = layout ?? new AutoLayout();
    }

    // ------------------------------------------------------------------
    // ノードの追加・移動・基本編集
    // ------------------------------------------------------------------

    /// <summary>目標としてアイテムノードを追加する。素材ツリーは自動展開しない。</summary>
    public ItemNode AddItemNode(string itemId, int requiredQuantity = 1, double? x = null, double? y = null, EditOutcome? outcome = null)
    {
        var item = Game.FindItem(itemId) ?? throw new ArgumentException($"アイテム「{itemId}」が存在しません。");
        var node = new ItemNode
        {
            Id = NewNodeId(),
            ItemId = itemId,
            RequiredQuantity = Math.Max(0, requiredQuantity),
            SelectedRecipeId = item.Recipes.FirstOrDefault()?.Id,
        };
        if (x.HasValue && y.HasValue)
        {
            node.X = x.Value;
            node.Y = y.Value;
        }
        else
        {
            var (w, h) = AutoLayout.DefaultSize(Plan, "");
            var (px, py, overlaps) = Layout.FindPlaceForNewTarget(Plan, w, h);
            node.X = px;
            node.Y = py;
            if (overlaps && outcome != null) NoteOverlap(outcome);
        }
        Plan.ItemNodes.Add(node);
        outcome?.CreatedNodeIds.Add(node.Id);
        return node;
    }

    public MemoNode AddMemoNode(double x, double y, string text = "")
    {
        var memo = new MemoNode { Id = NewNodeId(), X = x, Y = y, Text = text };
        Plan.MemoNodes.Add(memo);
        return memo;
    }

    public void MoveNode(string nodeId, double x, double y)
    {
        var item = Plan.FindItemNode(nodeId);
        if (item != null) { item.X = x; item.Y = y; return; }
        var memo = Plan.FindMemoNode(nodeId) ?? throw NodeNotFound(nodeId);
        memo.X = x;
        memo.Y = y;
    }

    public void ResizeMemo(string nodeId, double width, double height)
    {
        var memo = Plan.FindMemoNode(nodeId) ?? throw NodeNotFound(nodeId);
        memo.Width = Math.Max(80, width);
        memo.Height = Math.Max(40, height);
    }

    public void SetMemoText(string nodeId, string text)
    {
        var memo = Plan.FindMemoNode(nodeId) ?? throw NodeNotFound(nodeId);
        memo.Text = text;
    }

    public void SetMemoColor(string nodeId, string colorKey)
    {
        var memo = Plan.FindMemoNode(nodeId) ?? throw NodeNotFound(nodeId);
        memo.ColorKey = colorKey;
    }

    /// <summary>目標ノードの必要数量。子ノードの数量は計算で決まるため変更できない。</summary>
    public void SetRequiredQuantity(string nodeId, int quantity)
    {
        var node = Plan.FindItemNode(nodeId) ?? throw NodeNotFound(nodeId);
        if (!Plan.IsTarget(nodeId))
            throw new InvalidOperationException("子ノードの必要数量は親の制作回数から計算されるため、直接変更できません。");
        node.RequiredQuantity = Math.Max(0, quantity);
    }

    public void SetInventory(string itemId, int quantity)
    {
        if (quantity <= 0) Plan.Inventory.Remove(itemId);
        else Plan.Inventory[itemId] = quantity;
    }

    public void SetDisplayMode(DisplayMode mode) => Plan.DisplayMode = mode;

    public void SetCollapsed(string nodeId, bool collapsed)
    {
        var node = Plan.FindItemNode(nodeId) ?? throw NodeNotFound(nodeId);
        node.IsCollapsed = collapsed;
    }

    // ------------------------------------------------------------------
    // 展開・レシピ変更
    // ------------------------------------------------------------------

    /// <summary>
    /// ノードの素材ツリーを再帰的に生成する。既に子がある素材・切断済みの素材は触らない。
    /// 祖先に同じアイテムがある場合は循環地点として 1 世代だけ生成する。
    /// </summary>
    public EditOutcome Expand(string nodeId)
    {
        var outcome = new EditOutcome();
        var node = Plan.FindItemNode(nodeId) ?? throw NodeNotFound(nodeId);
        var item = Game.FindItem(node.ItemId);
        if (item == null) { outcome.Notices.Add("アイテムがゲームデータに存在しません。"); return outcome; }

        var recipe = ResolveRecipe(node, item);
        if (recipe == null)
        {
            outcome.Notices.Add($"「{item.Name}」にはレシピがないため展開できません。");
            return outcome;
        }
        node.SelectedRecipeId = recipe.Id;
        node.IsExpanded = true;
        node.IsCollapsed = false;

        var ancestorItems = Plan.AncestorIds(nodeId).Select(id => Plan.FindItemNode(id)!.ItemId).ToHashSet(StringComparer.Ordinal);
        if (ancestorItems.Contains(node.ItemId))
        {
            node.IsCycleTerminal = true;
            outcome.Notices.Add($"「{item.Name}」は上位でも使われているため循環になります。素材は 1 世代だけ表示します。");
            GenerateChildren(node, recipe, ancestorItems, recurse: false, outcome);
        }
        else
        {
            ancestorItems.Add(node.ItemId);
            GenerateChildren(node, recipe, ancestorItems, recurse: true, outcome);
        }
        // 根は動かさず、生成した子孫を階層配置する
        if (Tree.ApplyKeepingRoot(Plan, Game, nodeId)) NoteOverlap(outcome);
        return outcome;
    }

    private TreeLayout Tree => new(Layout);

    /// <summary>明示的な整列操作。すべての目標ツリーを左上から縦に積み直す。メモは動かさない。</summary>
    public void ArrangeAll() => Tree.ArrangeAll(Plan, Game);

    /// <summary>
    /// 選択レシピを変更する。展開済みなら子を再生成する。新旧で共通の素材の子ノードは位置を保つ。
    /// </summary>
    public EditOutcome SetSelectedRecipe(string nodeId, string? recipeId)
    {
        var outcome = new EditOutcome();
        var node = Plan.FindItemNode(nodeId) ?? throw NodeNotFound(nodeId);
        var item = Game.FindItem(node.ItemId) ?? throw new InvalidOperationException("アイテムがゲームデータに存在しません。");
        Recipe? recipe = null;
        if (recipeId != null)
        {
            recipe = item.Recipes.FirstOrDefault(r => r.Id == recipeId)
                ?? throw new ArgumentException($"レシピ「{recipeId}」は「{item.Name}」のレシピではありません。");
        }
        node.SelectedRecipeId = recipe?.Id;
        if (!node.IsExpanded) return outcome;
        if (recipe == null)
        {
            // レシピなし＝末端に戻す。子ツリーは削除する。
            foreach (var e in Plan.ChildEdgesOf(nodeId).ToList()) RemoveSubtree(e.ChildNodeId);
            Plan.Edges.RemoveAll(e => e.ParentNodeId == nodeId);
            Plan.SeveredEdges.RemoveAll(s => s.ParentNodeId == nodeId);
            node.IsExpanded = false;
            return outcome;
        }

        var newMaterials = recipe.Materials.Select(m => m.ItemId).ToHashSet(StringComparer.Ordinal);
        // 新レシピに含まれない素材の子ツリーと切断記録を取り除く
        foreach (var e in Plan.ChildEdgesOf(nodeId).Where(e => !newMaterials.Contains(e.MaterialItemId)).ToList())
        {
            RemoveSubtree(e.ChildNodeId);
            Plan.Edges.Remove(e);
        }
        Plan.SeveredEdges.RemoveAll(s => s.ParentNodeId == nodeId && !newMaterials.Contains(s.MaterialItemId));

        var ancestorItems = Plan.AncestorIds(nodeId).Select(id => Plan.FindItemNode(id)!.ItemId).ToHashSet(StringComparer.Ordinal);
        ancestorItems.Add(node.ItemId);
        var before = Plan.ChildEdgesOf(nodeId).Select(e => e.ChildNodeId).ToHashSet(StringComparer.Ordinal);
        GenerateChildren(node, recipe, ancestorItems, recurse: !node.IsCycleTerminal, outcome);
        // 新しく生えた直下の子だけをブロックとして配置し、残した子は動かさない
        foreach (var e in Plan.ChildEdgesOf(nodeId).Where(e => !before.Contains(e.ChildNodeId)).ToList())
            if (Tree.PlaceBlockRightOf(Plan, Game, nodeId, e.ChildNodeId)) NoteOverlap(outcome);
        return outcome;
    }

    private Recipe? ResolveRecipe(ItemNode node, Item item)
    {
        if (node.SelectedRecipeId != null)
        {
            var r = item.Recipes.FirstOrDefault(x => x.Id == node.SelectedRecipeId);
            if (r != null) return r;
        }
        return item.Recipes.FirstOrDefault();
    }

    /// <summary>レシピの素材ごとに子ノードを生成する。既存の子・切断済みは飛ばす。</summary>
    private void GenerateChildren(ItemNode parent, Recipe recipe, HashSet<string> ancestorItems, bool recurse, EditOutcome outcome)
    {
        var existing = Plan.ChildEdgesOf(parent.Id).Select(e => e.MaterialItemId).ToHashSet(StringComparer.Ordinal);
        var severed = Plan.SeveredEdges.Where(s => s.ParentNodeId == parent.Id).Select(s => s.MaterialItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var m in recipe.Materials)
        {
            if (existing.Contains(m.ItemId) || severed.Contains(m.ItemId)) continue;
            CreateChild(parent, m.ItemId, ancestorItems, recurse, outcome);
        }
    }

    /// <summary>親の右側に素材ノードを 1 つ作って接続し、必要なら再帰的に展開する。</summary>
    private ItemNode CreateChild(ItemNode parent, string materialItemId, HashSet<string> ancestorItems, bool recurse, EditOutcome outcome)
    {
        var item = Game.FindItem(materialItemId);
        var child = new ItemNode
        {
            Id = NewNodeId(),
            ItemId = materialItemId,
            RequiredQuantity = 0,
            SelectedRecipeId = item?.Recipes.FirstOrDefault()?.Id,
        };
        // 仮の位置。生成が終わった後に TreeLayout でブロック配置する
        child.X = parent.X + AutoLayout.DefaultItemNodeWidth + AutoLayout.GapX;
        child.Y = parent.Y;

        Plan.ItemNodes.Add(child);
        Plan.Edges.Add(new PlanEdge { Id = NewEdgeId(), ParentNodeId = parent.Id, ChildNodeId = child.Id, MaterialItemId = materialItemId });
        outcome.CreatedNodeIds.Add(child.Id);

        if (item == null)
        {
            outcome.Notices.Add($"素材「{materialItemId}」はゲームデータに存在しません。");
            return child;
        }
        var recipe = item.Recipes.FirstOrDefault();
        if (recipe == null || !recurse) return child;

        child.IsExpanded = true;
        if (ancestorItems.Contains(materialItemId))
        {
            // 循環: ここで止め、素材を 1 世代だけ見せる
            child.IsCycleTerminal = true;
            outcome.Notices.Add($"「{item.Name}」で循環レシピを検出しました。素材は 1 世代だけ表示します。");
            GenerateChildren(child, recipe, ancestorItems, recurse: false, outcome);
        }
        else
        {
            var next = new HashSet<string>(ancestorItems, StringComparer.Ordinal) { materialItemId };
            GenerateChildren(child, recipe, next, recurse: true, outcome);
        }
        return child;
    }

    // ------------------------------------------------------------------
    // 切断・再接続・接続・削除
    // ------------------------------------------------------------------

    /// <summary>接続を切断し、下流ノードを削除する。切断記録を残し、再接続できるようにする。</summary>
    public void Sever(string edgeId)
    {
        var edge = Plan.Edges.FirstOrDefault(e => e.Id == edgeId) ?? throw new ArgumentException($"接続「{edgeId}」が存在しません。");
        RemoveSubtree(edge.ChildNodeId);
        Plan.Edges.Remove(edge);
        if (!Plan.SeveredEdges.Any(s => s.ParentNodeId == edge.ParentNodeId && s.MaterialItemId == edge.MaterialItemId))
            Plan.SeveredEdges.Add(new SeveredEdge { ParentNodeId = edge.ParentNodeId, MaterialItemId = edge.MaterialItemId });
    }

    /// <summary>切断された接続を再接続し、下流を再生成・自動配置する。以前の位置は復元しない。</summary>
    public EditOutcome Reconnect(string parentNodeId, string materialItemId)
    {
        var outcome = new EditOutcome();
        var parent = Plan.FindItemNode(parentNodeId) ?? throw NodeNotFound(parentNodeId);
        var severed = Plan.SeveredEdges.FirstOrDefault(s => s.ParentNodeId == parentNodeId && s.MaterialItemId == materialItemId)
            ?? throw new ArgumentException("該当する切断記録がありません。");
        Plan.SeveredEdges.Remove(severed);

        var item = Game.FindItem(parent.ItemId);
        var recipe = item == null ? null : ResolveRecipe(parent, item);
        if (recipe == null || !recipe.Materials.Any(m => m.ItemId == materialItemId))
        {
            outcome.Notices.Add("現在のレシピにこの素材が含まれていないため再接続しませんでした。");
            return outcome;
        }
        parent.IsExpanded = true;
        var ancestorItems = Plan.AncestorIds(parentNodeId).Select(id => Plan.FindItemNode(id)!.ItemId).ToHashSet(StringComparer.Ordinal);
        ancestorItems.Add(parent.ItemId);
        var created = CreateChild(parent, materialItemId, ancestorItems, recurse: !parent.IsCycleTerminal, outcome);
        if (Tree.PlaceBlockRightOf(Plan, Game, parentNodeId, created.Id)) NoteOverlap(outcome);
        return outcome;
    }

    /// <summary>接続線だけを削除する。子は独立した目標になり、親側には切断記録を残す。</summary>
    public void DeleteEdge(string edgeId, Func<string, int>? demandOf = null)
    {
        var edge = Plan.Edges.FirstOrDefault(e => e.Id == edgeId) ?? throw new ArgumentException($"接続「{edgeId}」が存在しません。");
        Plan.Edges.Remove(edge);
        var child = Plan.FindItemNode(edge.ChildNodeId);
        if (child != null && demandOf != null) child.RequiredQuantity = Math.Max(0, demandOf(child.Id));
        if (!Plan.SeveredEdges.Any(s => s.ParentNodeId == edge.ParentNodeId && s.MaterialItemId == edge.MaterialItemId))
            Plan.SeveredEdges.Add(new SeveredEdge { ParentNodeId = edge.ParentNodeId, MaterialItemId = edge.MaterialItemId });
    }

    /// <summary>
    /// 既存の 2 ノードを親（製品）→子（素材）として接続する。
    /// 子は親を持たず、親の祖先でもなく、親のレシピの素材でなければならない。
    /// </summary>
    public EditOutcome Connect(string parentNodeId, string childNodeId)
    {
        var outcome = new EditOutcome();
        var parent = Plan.FindItemNode(parentNodeId) ?? throw NodeNotFound(parentNodeId);
        var child = Plan.FindItemNode(childNodeId) ?? throw NodeNotFound(childNodeId);
        if (parentNodeId == childNodeId) throw new InvalidOperationException("同じノード同士は接続できません。");
        if (!Plan.IsTarget(childNodeId)) throw new InvalidOperationException("接続先のノードには既に親があります。先にその接続を削除してください。");
        if (Plan.DescendantIds(childNodeId).Contains(parentNodeId)) throw new InvalidOperationException("子孫ノードを親として接続すると循環になるため接続できません。");

        var item = Game.FindItem(parent.ItemId) ?? throw new InvalidOperationException("アイテムがゲームデータに存在しません。");
        var recipe = ResolveRecipe(parent, item) ?? throw new InvalidOperationException($"「{item.Name}」にはレシピがないため素材を接続できません。");
        if (!recipe.Materials.Any(m => m.ItemId == child.ItemId))
            throw new InvalidOperationException($"「{Game.FindItem(child.ItemId)?.Name ?? child.ItemId}」は「{item.Name}」の選択中レシピの素材ではありません。");
        if (Plan.ChildEdgesOf(parentNodeId).Any(e => e.MaterialItemId == child.ItemId))
            throw new InvalidOperationException("この素材は既に接続されています。");

        parent.SelectedRecipeId = recipe.Id;
        if (!parent.IsExpanded)
        {
            // 未展開の親に手動で接続した場合、残りの素材は切断状態として記録し、後から再接続できるようにする
            parent.IsExpanded = true;
            foreach (var m in recipe.Materials.Where(m => m.ItemId != child.ItemId))
            {
                if (!Plan.SeveredEdges.Any(s => s.ParentNodeId == parentNodeId && s.MaterialItemId == m.ItemId))
                    Plan.SeveredEdges.Add(new SeveredEdge { ParentNodeId = parentNodeId, MaterialItemId = m.ItemId });
            }
            outcome.Notices.Add("残りの素材は切断状態として表示します。必要なら切断線をクリックして再接続してください。");
        }
        Plan.SeveredEdges.RemoveAll(s => s.ParentNodeId == parentNodeId && s.MaterialItemId == child.ItemId);
        Plan.Edges.Add(new PlanEdge { Id = NewEdgeId(), ParentNodeId = parentNodeId, ChildNodeId = childNodeId, MaterialItemId = child.ItemId });
        child.RequiredQuantity = 0;
        return outcome;
    }

    /// <summary>
    /// ノードを削除する。つながる接続線も削除する。アイテムマスターは変更しない。
    /// 親が残る場合は切断記録を残し、子が残る場合は独立した目標にする。
    /// </summary>
    public void DeleteNodes(IEnumerable<string> nodeIds, Func<string, int>? demandOf = null)
    {
        var ids = nodeIds.ToHashSet(StringComparer.Ordinal);
        // アイテムノードを削除するときは、その子孫（素材ツリー）もまとめて削除する
        foreach (var id in ids.ToList())
            if (Plan.FindItemNode(id) != null)
                foreach (var d in Plan.DescendantIds(id)) ids.Add(d);
        foreach (var id in ids)
        {
            var memo = Plan.FindMemoNode(id);
            if (memo != null) { Plan.MemoNodes.Remove(memo); continue; }
            var node = Plan.FindItemNode(id);
            if (node == null) continue;

            var parentEdge = Plan.FindParentEdge(id);
            if (parentEdge != null)
            {
                Plan.Edges.Remove(parentEdge);
                if (!ids.Contains(parentEdge.ParentNodeId)
                    && !Plan.SeveredEdges.Any(s => s.ParentNodeId == parentEdge.ParentNodeId && s.MaterialItemId == parentEdge.MaterialItemId))
                {
                    Plan.SeveredEdges.Add(new SeveredEdge { ParentNodeId = parentEdge.ParentNodeId, MaterialItemId = parentEdge.MaterialItemId });
                }
            }
            foreach (var e in Plan.ChildEdgesOf(id).ToList())
            {
                Plan.Edges.Remove(e);
                var child = Plan.FindItemNode(e.ChildNodeId);
                if (child != null && !ids.Contains(child.Id) && demandOf != null)
                    child.RequiredQuantity = Math.Max(0, demandOf(child.Id));
            }
            Plan.SeveredEdges.RemoveAll(s => s.ParentNodeId == id);
            Plan.ItemNodes.Remove(node);
        }
    }

    /// <summary>指定ノードとその子孫をすべて削除する（切断の内部処理）。</summary>
    private void RemoveSubtree(string rootNodeId)
    {
        var ids = Plan.DescendantIds(rootNodeId).ToHashSet(StringComparer.Ordinal);
        Plan.Edges.RemoveAll(e => ids.Contains(e.ParentNodeId) || ids.Contains(e.ChildNodeId));
        Plan.SeveredEdges.RemoveAll(s => ids.Contains(s.ParentNodeId));
        Plan.ItemNodes.RemoveAll(n => ids.Contains(n.Id));
    }

    // ------------------------------------------------------------------
    // コピー・貼り付け・整列
    // ------------------------------------------------------------------

    public PlanClipboardData Copy(IEnumerable<string> nodeIds, Func<string, int>? demandOf = null)
    {
        var ids = nodeIds.ToHashSet(StringComparer.Ordinal);
        var data = new PlanClipboardData();
        foreach (var n in Plan.ItemNodes.Where(n => ids.Contains(n.Id)))
        {
            var clone = n.Clone();
            // 貼り付け後に親を失うノードは目標になるので、現在の需要を必要数量として焼き込む
            if (!Plan.IsTarget(n.Id) && demandOf != null) clone.RequiredQuantity = Math.Max(0, demandOf(n.Id));
            data.ItemNodes.Add(clone);
        }
        foreach (var n in Plan.MemoNodes.Where(n => ids.Contains(n.Id))) data.MemoNodes.Add(n.Clone());
        foreach (var e in Plan.Edges.Where(e => ids.Contains(e.ParentNodeId) && ids.Contains(e.ChildNodeId))) data.Edges.Add(e.Clone());
        return data;
    }

    /// <summary>新しい ID を振り直して貼り付ける。戻り値は作成したノード ID。</summary>
    public List<string> Paste(PlanClipboardData data, double offsetX = 30, double offsetY = 30)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = new List<string>();
        var nodesWithParent = data.Edges.Select(e => e.ChildNodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var n in data.ItemNodes)
        {
            var clone = n.Clone();
            clone.Id = NewNodeId();
            clone.X += offsetX;
            clone.Y += offsetY;
            if (!nodesWithParent.Contains(n.Id) && clone.RequiredQuantity <= 0) clone.RequiredQuantity = 1;
            idMap[n.Id] = clone.Id;
            Plan.ItemNodes.Add(clone);
            created.Add(clone.Id);
        }
        foreach (var n in data.MemoNodes)
        {
            var clone = n.Clone();
            clone.Id = NewNodeId();
            clone.X += offsetX;
            clone.Y += offsetY;
            Plan.MemoNodes.Add(clone);
            created.Add(clone.Id);
        }
        foreach (var e in data.Edges)
        {
            if (!idMap.TryGetValue(e.ParentNodeId, out var p) || !idMap.TryGetValue(e.ChildNodeId, out var c)) continue;
            Plan.Edges.Add(new PlanEdge { Id = NewEdgeId(), ParentNodeId = p, ChildNodeId = c, MaterialItemId = e.MaterialItemId });
        }
        // 貼り付けたノードの切断記録: 子がコピーされなかった素材は切断扱いにして再接続可能にする
        foreach (var n in data.ItemNodes)
        {
            var newNode = Plan.FindItemNode(idMap[n.Id])!;
            if (!newNode.IsExpanded || newNode.SelectedRecipeId == null) continue;
            var recipe = Game.FindRecipe(newNode.SelectedRecipeId);
            if (recipe == null) continue;
            var present = Plan.ChildEdgesOf(newNode.Id).Select(e => e.MaterialItemId).ToHashSet(StringComparer.Ordinal);
            foreach (var m in recipe.Materials.Where(m => !present.Contains(m.ItemId)))
                Plan.SeveredEdges.Add(new SeveredEdge { ParentNodeId = newNode.Id, MaterialItemId = m.ItemId });
        }
        return created;
    }

    /// <summary>明示的な整列操作。指定ノードの子孫だけを左右方向に並べ直す。ほかのノードは動かさない。</summary>
    public EditOutcome ArrangeSubtree(string rootNodeId)
    {
        var outcome = new EditOutcome();
        if (Plan.FindItemNode(rootNodeId) == null) throw NodeNotFound(rootNodeId);
        if (Tree.ApplyKeepingRoot(Plan, Game, rootNodeId)) NoteOverlap(outcome);
        return outcome;
    }

    // ------------------------------------------------------------------

    private string NewNodeId() => IdGenerator.NewNodeId(id => Plan.FindItemNode(id) != null || Plan.FindMemoNode(id) != null);
    private string NewEdgeId() => IdGenerator.NewEdgeId(id => Plan.Edges.Any(e => e.Id == id));

    private static void NoteOverlap(EditOutcome outcome)
    {
        if (!outcome.HadOverlap)
            outcome.Notices.Add("空いている場所が見つからなかったため、一部のノードが重なって配置されました。手で移動してください。");
        outcome.HadOverlap = true;
    }

    private static ArgumentException NodeNotFound(string nodeId) => new($"ノード「{nodeId}」が存在しません。");
}
