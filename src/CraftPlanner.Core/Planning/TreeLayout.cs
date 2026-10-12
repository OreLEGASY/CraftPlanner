using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Planning;

/// <summary>
/// 階層レイアウト（左が製品、右が素材）。
/// 親ごとに子ノード群を 1 つのブロックとして縦に積み、親はそのブロックの中央に置く。
/// 同じ深さのノードは同じ列（x）にそろえる。既存の無関係なノードとは重ならない位置を探す。
/// </summary>
public sealed class TreeLayout
{
    private readonly AutoLayout _layout;

    public TreeLayout(AutoLayout layout) => _layout = layout;

    public sealed class Block
    {
        public Dictionary<string, (double X, double Y)> Positions { get; } = new(StringComparer.Ordinal);
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>子ノードをレシピの素材順に並べる（素材順が分からないものは後ろ）。</summary>
    public static List<string> OrderedChildren(Plan plan, GameData game, string nodeId)
    {
        var node = plan.FindItemNode(nodeId);
        var edges = plan.ChildEdgesOf(nodeId).ToList();
        if (node == null || edges.Count == 0) return new List<string>();
        var recipe = node.SelectedRecipeId == null ? null : game.FindRecipe(node.SelectedRecipeId);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        if (recipe != null)
            for (var i = 0; i < recipe.Materials.Count; i++) order.TryAdd(recipe.Materials[i].ItemId, i);
        return edges
            .OrderBy(e => order.TryGetValue(e.MaterialItemId, out var i) ? i : int.MaxValue)
            .ThenBy(e => e.MaterialItemId, StringComparer.Ordinal)
            .Select(e => e.ChildNodeId)
            .ToList();
    }

    /// <summary>部分木の配置を計算する（まだ適用しない）。ブロックの左上を (left, top) とする。</summary>
    public Block Compute(Plan plan, GameData game, string rootId, double left, double top)
    {
        var block = new Block();
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var size = new Dictionary<string, (double W, double H)>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Visit(string id, int d)
        {
            depth[id] = d;
            var (w, h) = _layout.SizeOfNode(plan, id);
            size[id] = (w, h);
            var kids = OrderedChildren(plan, game, id);
            children[id] = kids;
            foreach (var c in kids) Visit(c, d + 1);
        }
        Visit(rootId, 0);

        // 列ごとの幅（同じ深さは同じ x にそろえる）
        var maxDepth = depth.Values.Max();
        var colWidth = new double[maxDepth + 1];
        foreach (var kv in depth) colWidth[kv.Value] = Math.Max(colWidth[kv.Value], size[kv.Key].W);
        var colX = new double[maxDepth + 1];
        var x = left;
        for (var d = 0; d <= maxDepth; d++)
        {
            colX[d] = x;
            x += colWidth[d] + AutoLayout.GapX;
        }
        block.Width = x - AutoLayout.GapX - left;

        // 部分木ごとの高さ（子ブロックの合計と自分の高さの大きい方）
        var blockHeight = new Dictionary<string, double>(StringComparer.Ordinal);
        double HeightOf(string id)
        {
            if (blockHeight.TryGetValue(id, out var cached)) return cached;
            var kids = children[id];
            var own = size[id].H;
            var total = kids.Count == 0 ? 0 : kids.Sum(HeightOf) + AutoLayout.GapY * (kids.Count - 1);
            var h = Math.Max(own, total);
            blockHeight[id] = h;
            return h;
        }
        block.Height = HeightOf(rootId);

        void Place(string id, double blockTop)
        {
            var own = size[id].H;
            var h = blockHeight[id];
            block.Positions[id] = (colX[depth[id]], Math.Round(blockTop + (h - own) / 2));
            var kids = children[id];
            if (kids.Count == 0) return;
            var total = kids.Sum(c => blockHeight[c]) + AutoLayout.GapY * (kids.Count - 1);
            var cursor = blockTop + (h - total) / 2;
            foreach (var c in kids)
            {
                Place(c, cursor);
                cursor += blockHeight[c] + AutoLayout.GapY;
            }
            // 親は「最初の子の中心」と「最後の子の中心」の中点に合わせる（子ブロックの余白に引きずられない）
            var firstCenter = block.Positions[kids[0]].Y + size[kids[0]].H / 2;
            var lastCenter = block.Positions[kids[^1]].Y + size[kids[^1]].H / 2;
            block.Positions[id] = (colX[depth[id]], Math.Round((firstCenter + lastCenter) / 2 - own / 2));
        }
        Place(rootId, top);
        return block;
    }

    /// <summary>
    /// 根の位置を固定したまま子孫を階層配置する。部分木の外のノードと重なる場合は子孫だけを上下にずらして空きを探す。
    /// 戻り値は「重なりが解消できなかった」か。
    /// </summary>
    public bool ApplyKeepingRoot(Plan plan, GameData game, string rootId)
    {
        var root = plan.FindItemNode(rootId) ?? throw new ArgumentException($"ノード「{rootId}」が存在しません。");
        var (_, rootH) = _layout.SizeOfNode(plan, rootId);
        var probe = Compute(plan, game, rootId, root.X, 0);
        var top = root.Y - (probe.Height - rootH) / 2;
        var block = Compute(plan, game, rootId, root.X, top);
        // 根の y は固定（計算上の根の位置との差を子孫にも足す）
        var shiftRoot = root.Y - block.Positions[rootId].Y;
        var descendants = block.Positions.Keys.Where(id => id != rootId).ToList();
        if (descendants.Count == 0) return false;

        var outside = OutsideBounds(plan, block.Positions.Keys);
        var rowStep = AutoLayout.DefaultItemNodeHeight / 2 + AutoLayout.GapY;
        foreach (var offset in Offsets(rowStep))
        {
            if (!Collides(block, descendants, plan, 0, shiftRoot + offset, outside))
            {
                Apply(plan, block, descendants, 0, shiftRoot + offset);
                return false;
            }
        }
        Apply(plan, block, descendants, 0, shiftRoot);
        return true;
    }

    /// <summary>部分木を 1 つのブロックとして親の右側に置く。親の中央に合わせ、重なる場合は上下にずらす。</summary>
    public bool PlaceBlockRightOf(Plan plan, GameData game, string parentId, string subtreeRootId)
    {
        var parent = _layout.BoundsOf(plan, parentId);
        var probe = Compute(plan, game, subtreeRootId, parent.Right + AutoLayout.GapX, 0);
        var top = parent.Y + parent.Height / 2 - probe.Height / 2;
        var block = Compute(plan, game, subtreeRootId, parent.Right + AutoLayout.GapX, top);
        var ids = block.Positions.Keys.ToList();
        var outside = OutsideBounds(plan, ids);
        var rowStep = AutoLayout.DefaultItemNodeHeight / 2 + AutoLayout.GapY;
        foreach (var offset in Offsets(rowStep))
        {
            if (!Collides(block, ids, plan, 0, offset, outside))
            {
                Apply(plan, block, ids, 0, offset);
                return false;
            }
        }
        Apply(plan, block, ids, 0, 0);
        return true;
    }

    /// <summary>すべての目標ツリーを左上から縦に積んで配置し直す。メモは動かさない。</summary>
    public void ArrangeAll(Plan plan, GameData game, double left = 40, double top = 40)
    {
        var targets = plan.TargetNodes().OrderBy(t => t.Y).ThenBy(t => t.X).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        var cursor = top;
        foreach (var t in targets)
        {
            var block = Compute(plan, game, t.Id, left, cursor);
            Apply(plan, block, block.Positions.Keys.ToList(), 0, 0);
            cursor += block.Height + AutoLayout.GapY * 2;
        }
    }

    // ------------------------------------------------------------------

    private static IEnumerable<double> Offsets(double step)
    {
        yield return 0;
        for (var k = 1; k <= AutoLayout.MaxRowOffset; k++)
        {
            yield return k * step;
            yield return -k * step;
        }
    }

    private List<Rect> OutsideBounds(Plan plan, IEnumerable<string> excluded)
    {
        var ex = excluded.ToHashSet(StringComparer.Ordinal);
        return _layout.AllBounds(plan).Where(b => !ex.Contains(b.Id)).Select(b => b.Bounds).ToList();
    }

    private bool Collides(Block block, List<string> ids, Plan plan, double dx, double dy, List<Rect> outside)
    {
        foreach (var id in ids)
        {
            var (x, y) = block.Positions[id];
            var (w, h) = _layout.SizeOfNode(plan, id);
            var r = new Rect(x + dx, y + dy, w, h);
            if (outside.Any(o => o.Intersects(r, 4))) return true;
        }
        return false;
    }

    private static void Apply(Plan plan, Block block, List<string> ids, double dx, double dy)
    {
        foreach (var id in ids)
        {
            var node = plan.FindItemNode(id);
            if (node == null) continue;
            var (x, y) = block.Positions[id];
            node.X = Math.Round(x + dx);
            node.Y = Math.Round(y + dy);
        }
    }
}
