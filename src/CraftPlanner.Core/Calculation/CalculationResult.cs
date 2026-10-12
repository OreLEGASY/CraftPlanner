namespace CraftPlanner.Core.Calculation;

/// <summary>所持品から制作できるかの判定。表示の強調にだけ使い、数量計算には影響しない。</summary>
public enum Craftability
{
    /// <summary>判定対象外、または所持品では足りない。</summary>
    None,
    /// <summary>このノードの需要は所持数・余剰だけで満たされており、制作も調達も不要。</summary>
    Covered,
    /// <summary>素材をすべて所持しており、直接制作できる。</summary>
    Direct,
    /// <summary>下位素材まで含めれば所持品から制作できる。</summary>
    Indirect,
}

/// <summary>ノード 1 つ分の計算結果。</summary>
public sealed class NodeResult
{
    public string NodeId { get; init; } = "";
    public string ItemId { get; init; } = "";

    /// <summary>このノードに割り当てられた需要（目標なら必要数量、子なら親の制作回数×素材数）。</summary>
    public int Demand { get; set; }

    /// <summary>所持数を差し引いた後にこのノードが負担する需要。</summary>
    public int NetDemand { get; set; }

    /// <summary>レシピを適用して制作回数を計算したノードか。</summary>
    public bool IsCrafted { get; set; }
    public string? RecipeId { get; set; }
    public int OutputQuantity { get; set; }
    public int Crafts { get; set; }
    public int Produced => Crafts * OutputQuantity;

    /// <summary>末端ノードで、所持数・余剰を充当しても足りない数（調達が必要な数）。制作ノードでは 0。</summary>
    public int Shortage { get; set; }

    /// <summary>循環の影響を受け、確定値として扱えない。</summary>
    public bool IsUncertain { get; set; }

    /// <summary>展開時に循環を検出した地点。</summary>
    public bool IsCycleTerminal { get; set; }

    public Craftability Craftability { get; set; }

    /// <summary>このノード固有の警告（レシピが見つからない等）。</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>アイテム 1 種類分の計画全体の集計。</summary>
public sealed class ItemTotal
{
    public string ItemId { get; init; } = "";
    /// <summary>全ノードの需要の合計。</summary>
    public int Gross { get; set; }
    public int OnHand { get; set; }
    /// <summary>所持数を差し引いた需要。</summary>
    public int Net { get; set; }
    public int Crafts { get; set; }
    public int Produced { get; set; }
    /// <summary>制作数のうち需要に充当できず余った数。</summary>
    public int Surplus { get; set; }
    /// <summary>調達が必要な数（末端ノードの不足合計）。</summary>
    public int Shortage { get; set; }
    public bool IsUncertain { get; set; }
    public bool IsCyclic { get; set; }
}

/// <summary>目標 1 つ分の参考値。所持品を配分しない純粋な展開結果。</summary>
public sealed class TargetSummary
{
    public string TargetNodeId { get; init; } = "";
    public string ItemId { get; init; } = "";
    public int RequiredQuantity { get; init; }
    public List<TargetItemLine> Lines { get; } = new();
}

public sealed class TargetItemLine
{
    public string ItemId { get; init; } = "";
    /// <summary>この目標だけを見た場合の必要数。</summary>
    public int Required { get; init; }
    /// <summary>計画全体の所持数（参考表示）。</summary>
    public int OnHand { get; init; }
    /// <summary>参考不足数 = max(0, Required − OnHand)。確定値ではない。</summary>
    public int ReferenceShortage => Math.Max(0, Required - OnHand);
    /// <summary>この目標の中で制作される中間素材か（false なら調達する末端素材）。</summary>
    public bool IsIntermediate { get; init; }
    public bool IsUncertain { get; init; }
}

public sealed class CalculationResult
{
    public Dictionary<string, NodeResult> Nodes { get; } = new();
    public Dictionary<string, ItemTotal> Items { get; } = new();
    public List<TargetSummary> Targets { get; } = new();
    public List<string> Warnings { get; } = new();

    public NodeResult? Node(string nodeId) => Nodes.TryGetValue(nodeId, out var n) ? n : null;
    public ItemTotal? Item(string itemId) => Items.TryGetValue(itemId, out var i) ? i : null;
}
