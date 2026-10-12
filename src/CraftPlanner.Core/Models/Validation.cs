namespace CraftPlanner.Core.Models;

/// <summary>検証結果。エラーがなければ有効。警告は読み込みを妨げない。</summary>
public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;

    public void Error(string message) => Errors.Add(message);
    public void Warn(string message) => Warnings.Add(message);

    public override string ToString() => string.Join(Environment.NewLine, Errors.Concat(Warnings));
}

/// <summary>
/// 保存・読み込み時のデータ検証。壊れたファイルや参照切れを「読み込めない」ではなく
/// 「どこがどう壊れているか」として報告するために使う。
/// </summary>
public static class Validators
{
    public static ValidationResult ValidateGameData(GameData data)
    {
        var r = new ValidationResult();
        if (data.FormatVersion > GameData.CurrentFormatVersion)
            r.Error($"ゲームデータの形式バージョン {data.FormatVersion} はこのアプリ（{GameData.CurrentFormatVersion}）より新しいため読み込めません。");
        if (string.IsNullOrWhiteSpace(data.Id)) r.Error("ゲームデータ ID が空です。");

        var itemIds = new HashSet<string>();
        var recipeIds = new HashSet<string>();
        foreach (var item in data.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) { r.Error($"アイテム「{item.Name}」の ID が空です。"); continue; }
            if (!itemIds.Add(item.Id)) r.Error($"アイテム ID「{item.Id}」が重複しています。");
            foreach (var recipe in item.Recipes)
            {
                if (string.IsNullOrWhiteSpace(recipe.Id)) { r.Error($"アイテム「{item.Name}」のレシピに ID のないものがあります。"); continue; }
                if (!recipeIds.Add(recipe.Id)) r.Error($"レシピ ID「{recipe.Id}」が重複しています。");
                if (recipe.OutputQuantity < 1) r.Error($"レシピ「{recipe.Id}」の完成数量が 1 未満です。");
                var matIds = new HashSet<string>();
                foreach (var m in recipe.Materials)
                {
                    if (m.Quantity < 1) r.Error($"レシピ「{recipe.Id}」の素材「{m.ItemId}」の数量が 1 未満です。");
                    if (!matIds.Add(m.ItemId)) r.Error($"レシピ「{recipe.Id}」に素材「{m.ItemId}」が重複して登録されています。");
                }
            }
        }
        // 参照切れは警告（データ自体は読み込める。UI で「不明なアイテム」として表示する）
        foreach (var item in data.Items)
            foreach (var recipe in item.Recipes)
                foreach (var m in recipe.Materials)
                    if (!itemIds.Contains(m.ItemId))
                        r.Warn($"レシピ「{recipe.Id}」（{item.Name}）が存在しないアイテム「{m.ItemId}」を参照しています。");
        return r;
    }

    public static ValidationResult ValidatePlan(Plan plan, GameData? game)
    {
        var r = new ValidationResult();
        if (plan.FormatVersion > Plan.CurrentFormatVersion)
            r.Error($"制作計画の形式バージョン {plan.FormatVersion} はこのアプリ（{Plan.CurrentFormatVersion}）より新しいため読み込めません。");
        if (string.IsNullOrWhiteSpace(plan.Id)) r.Error("計画 ID が空です。");
        if (string.IsNullOrWhiteSpace(plan.GameDataId)) r.Error("参照元ゲームデータ ID が空です。");

        var nodeIds = new HashSet<string>();
        foreach (var n in plan.ItemNodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) { r.Error("ID のないアイテムノードがあります。"); continue; }
            if (!nodeIds.Add(n.Id)) r.Error($"ノード ID「{n.Id}」が重複しています。");
            if (n.RequiredQuantity < 0) r.Error($"ノード「{n.Id}」の必要数量が負です。");
            if (game != null && game.FindItem(n.ItemId) == null)
                r.Warn($"ノード「{n.Id}」が参照するアイテム「{n.ItemId}」はゲームデータに存在しません。");
        }
        foreach (var n in plan.MemoNodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) { r.Error("ID のないメモノードがあります。"); continue; }
            if (!nodeIds.Add(n.Id)) r.Error($"ノード ID「{n.Id}」が重複しています。");
        }
        var childSeen = new HashSet<string>();
        foreach (var e in plan.Edges)
        {
            if (!nodeIds.Contains(e.ParentNodeId)) r.Error($"接続「{e.Id}」の親ノード「{e.ParentNodeId}」が存在しません。");
            if (!nodeIds.Contains(e.ChildNodeId)) r.Error($"接続「{e.Id}」の子ノード「{e.ChildNodeId}」が存在しません。");
            if (!childSeen.Add(e.ChildNodeId)) r.Error($"ノード「{e.ChildNodeId}」に親接続が 2 本以上あります。");
            if (e.ParentNodeId == e.ChildNodeId) r.Error($"接続「{e.Id}」が自分自身を指しています。");
        }
        // ノードグラフの循環検出（森構造であることの確認）
        if (r.IsValid)
        {
            try
            {
                foreach (var n in plan.ItemNodes) plan.AncestorIds(n.Id);
            }
            catch (InvalidOperationException ex)
            {
                r.Error(ex.Message);
            }
        }
        foreach (var kv in plan.Inventory)
            if (kv.Value < 0) r.Error($"所持数「{kv.Key}」が負です。");
        return r;
    }
}
