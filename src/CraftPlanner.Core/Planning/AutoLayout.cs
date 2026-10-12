using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Planning;

/// <summary>矩形。ノードの重なり判定に使う。</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Intersects(Rect other, double margin = 0)
    {
        return X < other.Right + margin && Right + margin > other.X
            && Y < other.Bottom + margin && Bottom + margin > other.Y;
    }
}

/// <summary>ノードの表示サイズを UI から受け取るための関数。Core はサイズを描画しないので既定値を持つだけ。</summary>
public delegate (double Width, double Height) NodeSizeProvider(Plan plan, string nodeId);

/// <summary>
/// 新規ノードの自動配置。製品を左、素材を右に置く。
/// 既存ノードは動かさず、空いている場所を探す。見つからなければ重ねて置き、その事実を返す。
/// </summary>
public sealed class AutoLayout
{
    public const double DefaultItemNodeWidth = 220;
    public const double DefaultItemNodeHeight = 150;
    public const double SimpleItemNodeHeight = 64;
    public const double GapX = 120;
    public const double GapY = 16;
    public const int MaxRowOffset = 40;
    public const int MaxColumns = 20;

    private readonly NodeSizeProvider _sizeOf;

    public AutoLayout(NodeSizeProvider? sizeOf = null)
    {
        _sizeOf = sizeOf ?? DefaultSize;
    }

    public static (double, double) DefaultSize(Plan plan, string nodeId)
    {
        var memo = plan.FindMemoNode(nodeId);
        if (memo != null) return (memo.Width, memo.Height);
        return plan.DisplayMode == DisplayMode.Simple
            ? (DefaultItemNodeWidth, SimpleItemNodeHeight)
            : (DefaultItemNodeWidth, DefaultItemNodeHeight);
    }

    /// <summary>ノードの表示サイズ（UI の実測値、未計測なら既定値）。</summary>
    public (double Width, double Height) SizeOfNode(Plan plan, string nodeId) => _sizeOf(plan, nodeId);

    public Rect BoundsOf(Plan plan, string nodeId)
    {
        var (w, h) = _sizeOf(plan, nodeId);
        var item = plan.FindItemNode(nodeId);
        if (item != null) return new Rect(item.X, item.Y, w, h);
        var memo = plan.FindMemoNode(nodeId)!;
        return new Rect(memo.X, memo.Y, w, h);
    }

    /// <summary>親ノードの右側に、サイズ (w,h) のノードを置ける位置を探す。</summary>
    public (double X, double Y, bool Overlaps) FindPlaceRightOf(Plan plan, string parentNodeId, double width, double height, IEnumerable<string>? ignoreNodeIds = null)
    {
        var parent = BoundsOf(plan, parentNodeId);
        return FindFreePlace(plan, parent.Right + GapX, parent.Y, width, height, ignoreNodeIds);
    }

    /// <summary>
    /// (startX, startY) を起点に、同じ列で上下 ±MaxRowOffset 段、次に右の列へと最大 MaxColumns 列探索する。
    /// 見つからなければ起点を返し Overlaps = true とする。
    /// </summary>
    public (double X, double Y, bool Overlaps) FindFreePlace(Plan plan, double startX, double startY, double width, double height, IEnumerable<string>? ignoreNodeIds = null)
    {
        var ignore = ignoreNodeIds?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var occupied = AllBounds(plan).Where(b => !ignore.Contains(b.Id)).Select(b => b.Bounds).ToList();
        var rowStep = height + GapY;
        var colStep = width + GapX;

        for (var col = 0; col < MaxColumns; col++)
        {
            var x = startX + col * colStep;
            for (var k = 0; k <= MaxRowOffset; k++)
            {
                foreach (var sign in k == 0 ? new[] { 1 } : new[] { 1, -1 })
                {
                    var y = startY + sign * k * rowStep;
                    var candidate = new Rect(x, y, width, height);
                    if (!occupied.Any(o => o.Intersects(candidate, 4)))
                        return (x, y, false);
                }
            }
        }
        return (startX, startY, true);
    }

    /// <summary>目標ノードを新規に置く場所。既存ノード群の左下に積む。</summary>
    public (double X, double Y, bool Overlaps) FindPlaceForNewTarget(Plan plan, double width, double height)
    {
        var all = AllBounds(plan).Select(b => b.Bounds).ToList();
        if (all.Count == 0) return (40, 40, false);
        var minX = all.Min(b => b.X);
        var maxBottom = all.Max(b => b.Bottom);
        return FindFreePlace(plan, minX, maxBottom + GapY * 2, width, height);
    }

    public IEnumerable<(string Id, Rect Bounds)> AllBounds(Plan plan)
    {
        foreach (var n in plan.ItemNodes) yield return (n.Id, BoundsOf(plan, n.Id));
        foreach (var n in plan.MemoNodes) yield return (n.Id, BoundsOf(plan, n.Id));
    }
}
