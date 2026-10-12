using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Calculation;

/// <summary>
/// 制作計画の必要数量・不足数量を計算する。UI・ノード位置・登録順に依存しない純粋な処理。
///
/// 手順（設計書 5 章）:
///  1. 有効な接続からアイテム間グラフを作り、強連結成分（SCC）で循環を見つける。
///  2. 製品側から素材側へ SCC の位相順にアイテムを処理する。
///  3. アイテムごとに全ノードの需要を合計し、所持数を 1 回だけ差し引き、
///     同じレシピの制作ノードはまとめて制作回数を求める（余剰を他の需要へ充当するため）。
///  4. 制作回数をノードへ配分し、有効な接続に沿って子ノードの需要を決める。
///  5. 循環に属するアイテムはノード単位で計算し、「不確定」として返す。
/// </summary>
public static class PlanCalculator
{
    public static CalculationResult Calculate(Plan plan, GameData game)
    {
        var result = new CalculationResult();

        // ---- 計画全体（確定値） ----
        var core = ComputeCore(plan.ItemNodes, plan.Edges, plan.SeveredEdges, plan.Inventory, game, result.Warnings);
        foreach (var kv in core.Nodes) result.Nodes[kv.Key] = kv.Value;
        foreach (var kv in core.Items) result.Items[kv.Key] = kv.Value;

        // ---- 目標別の参考値（所持数を配分しない） ----
        foreach (var target in plan.TargetNodes().OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var subtreeIds = plan.DescendantIds(target.Id).ToHashSet();
            var subNodes = plan.ItemNodes.Where(n => subtreeIds.Contains(n.Id)).ToList();
            var subEdges = plan.Edges.Where(e => subtreeIds.Contains(e.ParentNodeId) && subtreeIds.Contains(e.ChildNodeId)).ToList();
            var subSevered = plan.SeveredEdges.Where(e => subtreeIds.Contains(e.ParentNodeId)).ToList();
            var sub = ComputeCore(subNodes, subEdges, subSevered, new Dictionary<string, int>(), game, new List<string>());

            var summary = new TargetSummary { TargetNodeId = target.Id, ItemId = target.ItemId, RequiredQuantity = target.RequiredQuantity };
            foreach (var item in sub.Items.Values.OrderBy(i => i.ItemId, StringComparer.Ordinal))
            {
                var crafted = sub.Nodes.Values.Any(n => n.ItemId == item.ItemId && n.IsCrafted);
                summary.Lines.Add(new TargetItemLine
                {
                    ItemId = item.ItemId,
                    Required = item.Gross,
                    OnHand = plan.Inventory.GetValueOrDefault(item.ItemId),
                    IsIntermediate = crafted,
                    IsUncertain = item.IsUncertain,
                });
            }
            result.Targets.Add(summary);
        }

        return result;
    }

    // =====================================================================
    // 中核の計算。計画全体と目標別の両方から呼ぶ。
    // =====================================================================

    private sealed class CoreResult
    {
        public Dictionary<string, NodeResult> Nodes { get; } = new();
        public Dictionary<string, ItemTotal> Items { get; } = new();
    }

    private sealed class NodeInfo
    {
        public required ItemNode Node;
        public Recipe? Recipe;            // 有効なレシピ（制作ノードのときだけ非 null）
        public bool IsCrafted;
        public List<PlanEdge> ChildEdges = new();
        public PlanEdge? ParentEdge;
        public int Depth;
        public NodeResult Result = null!;
    }

    private static CoreResult ComputeCore(
        IReadOnlyList<ItemNode> nodes,
        IReadOnlyList<PlanEdge> edges,
        IReadOnlyList<SeveredEdge> severed,
        IReadOnlyDictionary<string, int> inventory,
        GameData game,
        List<string> warnings)
    {
        var core = new CoreResult();
        if (nodes.Count == 0) return core;

        // ---- ノード情報の準備 ----
        var infos = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            var info = new NodeInfo { Node = n };
            info.Result = new NodeResult { NodeId = n.Id, ItemId = n.ItemId, IsCycleTerminal = n.IsCycleTerminal };
            if (n.IsExpanded && n.SelectedRecipeId != null)
            {
                var recipe = game.FindRecipe(n.SelectedRecipeId);
                var owner = recipe == null ? null : game.FindRecipeOwner(recipe.Id);
                if (recipe == null || owner == null || owner.Id != n.ItemId)
                {
                    info.Result.Warnings.Add("選択されたレシピが見つかりません。末端素材として計算します。");
                }
                else
                {
                    info.Recipe = recipe;
                    info.IsCrafted = true;
                    info.Result.IsCrafted = true;
                    info.Result.RecipeId = recipe.Id;
                    info.Result.OutputQuantity = recipe.OutputQuantity;
                }
            }
            infos[n.Id] = info;
        }
        foreach (var e in edges)
        {
            if (infos.TryGetValue(e.ParentNodeId, out var p) && infos.TryGetValue(e.ChildNodeId, out var c))
            {
                p.ChildEdges.Add(e);
                c.ParentEdge = e;
            }
        }
        // 深さ（祖先数）。循環アイテムの処理順に使う。
        foreach (var info in infos.Values)
        {
            var d = 0;
            var cur = info;
            while (cur.ParentEdge != null && infos.TryGetValue(cur.ParentEdge.ParentNodeId, out var parent))
            {
                d++;
                cur = parent;
                if (d > infos.Count) throw new InvalidOperationException("ノード接続に循環があります。");
            }
            info.Depth = d;
        }

        // ---- 目標ノードの需要 ----
        var demand = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var info in infos.Values)
        {
            demand[info.Node.Id] = info.ParentEdge == null ? info.Node.RequiredQuantity : 0;
        }

        // ---- アイテム間グラフと SCC ----
        var itemIds = infos.Values.Select(i => i.Node.ItemId).Distinct(StringComparer.Ordinal).ToList();
        var itemGraph = itemIds.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var info in infos.Values)
        {
            foreach (var e in info.ChildEdges)
            {
                var childItem = infos[e.ChildNodeId].Node.ItemId;
                itemGraph[info.Node.ItemId].Add(childItem);
            }
        }
        var sccs = Tarjan(itemIds, itemGraph);  // 製品側（根側）から素材側への順

        var uncertainItems = new HashSet<string>(StringComparer.Ordinal);
        var nodesByItem = infos.Values
            .GroupBy(i => i.Node.ItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Node.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        foreach (var scc in sccs)
        {
            var isCyclic = scc.Count > 1 || itemGraph[scc[0]].Contains(scc[0]);
            // 上流に不確定なアイテムがあれば、この SCC も不確定
            var upstreamUncertain = scc.Any(itemId => nodesByItem[itemId].Any(n =>
                n.ParentEdge != null && uncertainItems.Contains(infos[n.ParentEdge.ParentNodeId].Node.ItemId)));

            if (isCyclic)
            {
                foreach (var id in scc) uncertainItems.Add(id);
                ProcessCyclicGroup(scc, nodesByItem, infos, demand, inventory, core);
            }
            else
            {
                var itemId = scc[0];
                if (upstreamUncertain) uncertainItems.Add(itemId);
                ProcessItem(itemId, nodesByItem[itemId], infos, demand, inventory, core, uncertainItems.Contains(itemId));
            }
        }

        // ---- 不足している素材ノードへの警告（データ不整合の検出） ----
        foreach (var info in infos.Values)
        {
            if (!info.IsCrafted) continue;
            var severedHere = severed.Where(s => s.ParentNodeId == info.Node.Id).Select(s => s.MaterialItemId).ToHashSet(StringComparer.Ordinal);
            foreach (var m in info.Recipe!.Materials)
            {
                var hasEdge = info.ChildEdges.Any(e => e.MaterialItemId == m.ItemId);
                if (!hasEdge && !severedHere.Contains(m.ItemId))
                {
                    var name = game.FindItem(m.ItemId)?.Name ?? m.ItemId;
                    info.Result.Warnings.Add($"素材「{name}」のノードがありません（計算に含まれません）。");
                }
            }
        }

        // ---- 制作可能の判定 ----
        var memo = new Dictionary<string, Craftability>(StringComparer.Ordinal);
        foreach (var info in infos.Values)
        {
            info.Result.Craftability = EvaluateCraftability(info, infos, inventory, memo, new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var info in infos.Values) core.Nodes[info.Node.Id] = info.Result;
        return core;
    }

    /// <summary>循環のない 1 アイテムを処理する。すべての親ノードの制作回数は確定済み。</summary>
    private static void ProcessItem(
        string itemId,
        List<NodeInfo> nodesOfItem,
        Dictionary<string, NodeInfo> infos,
        Dictionary<string, long> demand,
        IReadOnlyDictionary<string, int> inventory,
        CoreResult core,
        bool uncertain)
    {
        long gross = 0;
        foreach (var n in nodesOfItem) gross += demand[n.Node.Id];
        var onHand = inventory.GetValueOrDefault(itemId);
        var net = Math.Max(0, gross - onHand);

        // 需要比で按分。端数の優先順位: 末端ノード → レシピ ID → ノード ID（位置や登録順に依存しない）
        var ordered = nodesOfItem
            .OrderBy(n => n.IsCrafted ? 1 : 0)
            .ThenBy(n => n.Recipe?.Id ?? "", StringComparer.Ordinal)
            .ThenBy(n => n.Node.Id, StringComparer.Ordinal)
            .ToList();
        var weights = ordered.Select(n => demand[n.Node.Id]).ToArray();
        var netAlloc = AllocateProportional(net, weights);

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Result.Demand = checked((int)demand[ordered[i].Node.Id]);
            ordered[i].Result.NetDemand = checked((int)netAlloc[i]);
            ordered[i].Result.IsUncertain = uncertain;
        }

        // 同じレシピの制作ノードはまとめて制作回数を求める
        long totalCrafts = 0, totalProduced = 0, totalSurplus = 0;
        foreach (var group in ordered.Where(n => n.IsCrafted).GroupBy(n => n.Recipe!.Id, StringComparer.Ordinal))
        {
            var members = group.ToList();
            var output = members[0].Recipe!.OutputQuantity;
            long groupNet = members.Sum(m => (long)m.Result.NetDemand);
            var crafts = CeilDiv(groupNet, output);
            var produced = crafts * output;
            totalCrafts += crafts;
            totalProduced += produced;
            totalSurplus += produced - groupNet;

            var craftAlloc = AllocateProportional(crafts, members.Select(m => (long)m.Result.NetDemand).ToArray());
            for (var i = 0; i < members.Count; i++)
            {
                members[i].Result.Crafts = checked((int)craftAlloc[i]);
                PropagateToChildren(members[i], infos, demand);
            }
        }

        // 末端ノードの不足。制作余剰をまず充当する（需要の大きい順、同数ならノード ID 順）。
        long shortage = 0;
        var leaves = ordered.Where(n => !n.IsCrafted)
            .OrderByDescending(n => n.Result.NetDemand)
            .ThenBy(n => n.Node.Id, StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            long s = leaf.Result.NetDemand;
            var use = Math.Min(s, totalSurplus);
            s -= use;
            totalSurplus -= use;
            leaf.Result.Shortage = checked((int)s);
            shortage += s;
        }

        core.Items[itemId] = new ItemTotal
        {
            ItemId = itemId,
            Gross = checked((int)gross),
            OnHand = onHand,
            Net = checked((int)net),
            Crafts = checked((int)totalCrafts),
            Produced = checked((int)totalProduced),
            Surplus = checked((int)totalSurplus),
            Shortage = checked((int)shortage),
            IsUncertain = uncertain,
            IsCyclic = false,
        };
    }

    /// <summary>
    /// 循環を含むアイテム群。正確な集計ができないため、森の深さ順にノード単位で ceil 計算し、
    /// 所持数は末端合計に 1 回だけ適用する。結果はすべて不確定扱い。
    /// </summary>
    private static void ProcessCyclicGroup(
        List<string> itemIds,
        Dictionary<string, List<NodeInfo>> nodesByItem,
        Dictionary<string, NodeInfo> infos,
        Dictionary<string, long> demand,
        IReadOnlyDictionary<string, int> inventory,
        CoreResult core)
    {
        var members = itemIds.SelectMany(id => nodesByItem[id])
            .OrderBy(n => n.Depth)
            .ThenBy(n => n.Node.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var n in members)
        {
            var d = demand[n.Node.Id];
            n.Result.Demand = checked((int)d);
            n.Result.NetDemand = checked((int)d);
            n.Result.IsUncertain = true;
            if (n.IsCrafted)
            {
                n.Result.Crafts = checked((int)CeilDiv(d, n.Recipe!.OutputQuantity));
                PropagateToChildren(n, infos, demand);
            }
            else
            {
                n.Result.Shortage = checked((int)d);
            }
        }

        foreach (var itemId in itemIds)
        {
            var ns = nodesByItem[itemId];
            long gross = ns.Sum(n => (long)n.Result.Demand);
            var onHand = inventory.GetValueOrDefault(itemId);
            long leafTotal = ns.Where(n => !n.IsCrafted).Sum(n => (long)n.Result.Shortage);
            long crafts = ns.Sum(n => (long)n.Result.Crafts);
            long produced = ns.Sum(n => (long)n.Result.Produced);
            core.Items[itemId] = new ItemTotal
            {
                ItemId = itemId,
                Gross = checked((int)gross),
                OnHand = onHand,
                Net = checked((int)Math.Max(0, gross - onHand)),
                Crafts = checked((int)crafts),
                Produced = checked((int)produced),
                Surplus = checked((int)Math.Max(0, produced - ns.Where(n => n.IsCrafted).Sum(n => (long)n.Result.NetDemand))),
                Shortage = checked((int)Math.Max(0, leafTotal - onHand)),
                IsUncertain = true,
                IsCyclic = true,
            };
        }
    }

    /// <summary>制作ノードの制作回数から、有効な接続に沿って子ノードの需要を加算する。切断された素材には流さない。</summary>
    private static void PropagateToChildren(NodeInfo node, Dictionary<string, NodeInfo> infos, Dictionary<string, long> demand)
    {
        if (!node.IsCrafted) return;
        foreach (var e in node.ChildEdges)
        {
            var mat = node.Recipe!.Materials.FirstOrDefault(m => m.ItemId == e.MaterialItemId);
            if (mat == null)
            {
                // レシピに存在しない素材への接続（レシピ変更後の古い接続など）。需要は流さず警告する。
                node.Result.Warnings.Add($"接続先「{e.MaterialItemId}」は現在のレシピの素材ではありません。");
                continue;
            }
            if (!infos.ContainsKey(e.ChildNodeId)) continue;
            demand[e.ChildNodeId] += (long)node.Result.Crafts * mat.Quantity;
        }
    }

    private static Craftability EvaluateCraftability(
        NodeInfo node,
        Dictionary<string, NodeInfo> infos,
        IReadOnlyDictionary<string, int> inventory,
        Dictionary<string, Craftability> memo,
        HashSet<string> visiting)
    {
        if (memo.TryGetValue(node.Node.Id, out var cached)) return cached;
        if (!visiting.Add(node.Node.Id)) return Craftability.None;

        Craftability result;
        if (node.Result.Demand > 0 && node.Result.NetDemand == 0)
        {
            result = Craftability.Covered;
        }
        else if (!node.IsCrafted || node.Result.Crafts == 0)
        {
            result = Craftability.None;
        }
        else
        {
            var allDirect = true;
            var allSatisfied = true;
            foreach (var m in node.Recipe!.Materials)
            {
                long required = (long)node.Result.Crafts * m.Quantity;
                var onHand = inventory.GetValueOrDefault(m.ItemId);
                if (required <= onHand) continue;
                allDirect = false;
                var edge = node.ChildEdges.FirstOrDefault(e => e.MaterialItemId == m.ItemId);
                if (edge != null && infos.TryGetValue(edge.ChildNodeId, out var child))
                {
                    var c = EvaluateCraftability(child, infos, inventory, memo, visiting);
                    if (c == Craftability.None) allSatisfied = false;
                }
                else
                {
                    allSatisfied = false;
                }
            }
            result = node.Recipe.Materials.Count == 0 ? Craftability.Direct
                : allDirect ? Craftability.Direct
                : allSatisfied ? Craftability.Indirect
                : Craftability.None;
        }

        visiting.Remove(node.Node.Id);
        memo[node.Node.Id] = result;
        return result;
    }

    // =====================================================================
    // 補助
    // =====================================================================

    public static long CeilDiv(long value, long divisor)
    {
        if (divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        if (value <= 0) return 0;
        return (value + divisor - 1) / divisor;
    }

    /// <summary>
    /// total を weights の比率で整数配分する（最大剰余法）。端数は配列の前方を優先する。
    /// 呼び出し側が weights を決定的な順に並べておくことで、結果が位置・登録順に依存しなくなる。
    /// </summary>
    public static long[] AllocateProportional(long total, long[] weights)
    {
        var n = weights.Length;
        var result = new long[n];
        if (n == 0 || total <= 0) return result;
        long sum = 0;
        foreach (var w in weights) sum += w;
        if (sum <= 0) return result;

        long assigned = 0;
        var remainders = new long[n];
        for (var i = 0; i < n; i++)
        {
            var exact = total * weights[i];          // long: 2^31 * 2^31 でも収まる
            result[i] = exact / sum;
            remainders[i] = exact % sum;
            assigned += result[i];
        }
        var left = total - assigned;
        // 剰余の大きい順、同じなら前方優先
        var order = Enumerable.Range(0, n).OrderByDescending(i => remainders[i]).ThenBy(i => i).ToList();
        foreach (var i in order)
        {
            if (left <= 0) break;
            if (weights[i] == 0) continue;  // 需要 0 のノードには配らない
            result[i]++;
            left--;
        }
        return result;
    }

    /// <summary>
    /// Tarjan のアルゴリズムで強連結成分を求め、製品側（グラフの上流）から素材側への位相順で返す。
    /// Tarjan は末端側の成分から順に出力するため、最後に反転する。
    /// 入力順に依存しないよう、頂点は ID 順で探索する。
    /// </summary>
    private static List<List<string>> Tarjan(List<string> vertices, Dictionary<string, HashSet<string>> graph)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;

        void StrongConnect(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in graph[v].OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!index.ContainsKey(w))
                {
                    StrongConnect(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }
            if (low[v] == index[v])
            {
                var comp = new List<string>();
                string x;
                do
                {
                    x = stack.Pop();
                    onStack.Remove(x);
                    comp.Add(x);
                } while (x != v);
                comp.Sort(StringComparer.Ordinal);
                result.Add(comp);
            }
        }

        foreach (var v in vertices.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!index.ContainsKey(v)) StrongConnect(v);
        }
        result.Reverse();
        return result;
    }
}
