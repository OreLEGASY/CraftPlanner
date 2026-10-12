namespace CraftPlanner.Core.Models;

public enum DisplayMode
{
    Simple,
    Detailed,
}

/// <summary>
/// 制作計画。ゲームデータとは別ファイルに保存し、<see cref="GameDataId"/> で参照元を識別する。
/// キャンバス上のノード・接続・所持数・表示状態など、復元に必要な情報をすべて持つ。
/// </summary>
public sealed class Plan
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string GameDataId { get; set; } = "";
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Detailed;

    /// <summary>計画内の所持数。アイテム ID → 数量。別の計画とは共有しない。</summary>
    public Dictionary<string, int> Inventory { get; set; } = new();

    public List<ItemNode> ItemNodes { get; set; } = new();
    public List<MemoNode> MemoNodes { get; set; } = new();

    /// <summary>有効な接続。親（製品）ノード → 子（素材）ノード。</summary>
    public List<PlanEdge> Edges { get; set; } = new();

    /// <summary>切断された接続。再接続のために親ノードと素材アイテムだけを記憶する。</summary>
    public List<SeveredEdge> SeveredEdges { get; set; } = new();

    public CanvasView View { get; set; } = new();

    // ---- 参照用ヘルパー（状態は変更しない） ----

    public ItemNode? FindItemNode(string nodeId) => ItemNodes.FirstOrDefault(n => n.Id == nodeId);
    public MemoNode? FindMemoNode(string nodeId) => MemoNodes.FirstOrDefault(n => n.Id == nodeId);

    public PlanEdge? FindParentEdge(string childNodeId) => Edges.FirstOrDefault(e => e.ChildNodeId == childNodeId);

    public IEnumerable<PlanEdge> ChildEdgesOf(string parentNodeId) => Edges.Where(e => e.ParentNodeId == parentNodeId);

    /// <summary>親接続を持たないアイテムノード＝目標。</summary>
    public IEnumerable<ItemNode> TargetNodes()
    {
        var childIds = Edges.Select(e => e.ChildNodeId).ToHashSet();
        return ItemNodes.Where(n => !childIds.Contains(n.Id));
    }

    public bool IsTarget(string nodeId) => FindParentEdge(nodeId) == null;

    /// <summary>指定ノードとその子孫ノードの ID（指定ノードを含む）。</summary>
    public List<string> DescendantIds(string nodeId)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(nodeId);
        var seen = new HashSet<string>();
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            result.Add(id);
            foreach (var e in ChildEdgesOf(id)) stack.Push(e.ChildNodeId);
        }
        return result;
    }

    /// <summary>指定ノードから根までの祖先ノード ID（指定ノードを含まない）。</summary>
    public List<string> AncestorIds(string nodeId)
    {
        var result = new List<string>();
        var current = nodeId;
        var guard = 0;
        while (true)
        {
            var parent = FindParentEdge(current);
            if (parent == null) break;
            result.Add(parent.ParentNodeId);
            current = parent.ParentNodeId;
            if (++guard > 100_000) throw new InvalidOperationException("ノード接続に循環があります。");
        }
        return result;
    }

    public Plan Clone()
    {
        return new Plan
        {
            FormatVersion = FormatVersion,
            Id = Id,
            Name = Name,
            GameDataId = GameDataId,
            DisplayMode = DisplayMode,
            Inventory = new Dictionary<string, int>(Inventory),
            ItemNodes = ItemNodes.Select(n => n.Clone()).ToList(),
            MemoNodes = MemoNodes.Select(n => n.Clone()).ToList(),
            Edges = Edges.Select(e => e.Clone()).ToList(),
            SeveredEdges = SeveredEdges.Select(e => e.Clone()).ToList(),
            View = View.Clone(),
        };
    }
}

/// <summary>アイテムマスターを参照するノード。同じアイテム ID のノードが複数あっても統合しない。</summary>
public sealed class ItemNode
{
    public string Id { get; set; } = "";
    public string ItemId { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>目標（親のないノード）のときに使う必要数量。子ノードでは計算値を使う。</summary>
    public int RequiredQuantity { get; set; } = 1;

    public string? SelectedRecipeId { get; set; }

    /// <summary>レシピを適用して素材ノードを生成済みか。計算で「制作ノード」とみなす条件。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>折りたたみ（表示のみ）。計算には影響しない。</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>展開時に循環を検出した地点。警告表示に使う。</summary>
    public bool IsCycleTerminal { get; set; }

    public ItemNode Clone() => (ItemNode)MemberwiseClone();
}

/// <summary>自由記述のメモ。素材計算には影響しない。</summary>
public sealed class MemoNode
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 120;
    public string Text { get; set; } = "";

    /// <summary>背景色のキー（例: "yellow", "blue"）。色の実体は UI 側で決める。</summary>
    public string ColorKey { get; set; } = "yellow";

    public MemoNode Clone() => (MemoNode)MemberwiseClone();
}

public sealed class PlanEdge
{
    public string Id { get; set; } = "";
    public string ParentNodeId { get; set; } = "";
    public string ChildNodeId { get; set; } = "";

    /// <summary>この接続が運ぶ素材のアイテム ID（子ノードの ItemId と一致する）。</summary>
    public string MaterialItemId { get; set; } = "";

    public PlanEdge Clone() => (PlanEdge)MemberwiseClone();
}

public sealed class SeveredEdge
{
    public string ParentNodeId { get; set; } = "";
    public string MaterialItemId { get; set; } = "";

    public SeveredEdge Clone() => (SeveredEdge)MemberwiseClone();
}

public sealed class CanvasView
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1.0;

    public CanvasView Clone() => (CanvasView)MemberwiseClone();
}
