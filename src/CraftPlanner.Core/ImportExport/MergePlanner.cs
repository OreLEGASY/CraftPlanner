using CraftPlanner.Core.Ids;
using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.ImportExport;

public enum MergeDecision
{
    /// <summary>この行を取り込まない。</summary>
    Skip,
    /// <summary>既存アイテムを更新する。</summary>
    Update,
    /// <summary>新しい ID を発行して別アイテムとして取り込む。</summary>
    CreateNew,
}

public enum MergeEntryKind
{
    /// <summary>ID が未使用。そのまま追加する。</summary>
    New,
    /// <summary>ID が空欄。ユーザーの選択が必要。</summary>
    EmptyId,
    /// <summary>既存アイテムと同じ ID で内容が異なる。ユーザーの選択が必要。</summary>
    Conflict,
    /// <summary>既存アイテムと同じ内容。取り込む必要がない。</summary>
    Identical,
}

public sealed record FieldChange(string Field, string Before, string After);

public sealed class MergeEntry
{
    public MergeEntryKind Kind { get; init; }
    public Item Incoming { get; init; } = new();
    public Item? Existing { get; init; }
    public int SourceRow { get; init; } = -1;
    public List<FieldChange> FieldChanges { get; } = new();
    public List<string> RecipeChanges { get; } = new();
    public MergeDecision Decision { get; set; }
    /// <summary>更新・追加時にレシピも反映するか。CSV（アイテム）からの取り込みでは常に false。</summary>
    public bool IncludeRecipes { get; set; }

    /// <summary>取り込みデータに存在する項目。null はすべての項目。存在しない項目は既存の値を保つ。</summary>
    public IReadOnlySet<string>? PresentFields { get; init; }

    public bool Has(string field) => PresentFields == null || PresentFields.Contains(field);

    public string DisplayId => string.IsNullOrEmpty(Incoming.Id) ? "(空)" : Incoming.Id;
}

public sealed class MergePreview
{
    public List<MergeEntry> Entries { get; } = new();
    public List<string> Errors { get; } = new();
    public bool CanApply => Errors.Count == 0;
    public IEnumerable<MergeEntry> NeedsDecision => Entries.Where(e => e.Kind is MergeEntryKind.EmptyId or MergeEntryKind.Conflict);
}

/// <summary>
/// 取り込みデータを既存のゲームデータへ統合する計画を立て、確認後に一括適用する。
/// Preview は何も変更しない。Apply は複製を返し、途中失敗で元データを壊さない。
/// </summary>
public static class MergePlanner
{
    public static MergePreview Preview(GameData target, IReadOnlyList<(int Row, Item Item)> incoming, bool incomingHasRecipes, IReadOnlySet<string>? presentFields = null)
    {
        var preview = new MergePreview();

        // 取り込みデータ内の ID 重複は中止（提案: 安全側）
        var dup = incoming.Where(x => !string.IsNullOrEmpty(x.Item.Id))
            .GroupBy(x => x.Item.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var g in dup)
        {
            var rows = string.Join(", ", g.Select(x => x.Row > 0 ? $"{x.Row} 行目" : x.Item.Name));
            preview.Errors.Add($"取り込みデータ内で ID「{g.Key}」が重複しています（{rows}）。取り込み元を修正してください。");
        }
        foreach (var (row, item) in incoming)
        {
            if (!string.IsNullOrEmpty(item.Id) && !IdGenerator.IsValidUserId(item.Id))
                preview.Errors.Add($"{RowLabel(row, item)}: ID「{item.Id}」に使えない文字が含まれています。");
            if (string.IsNullOrWhiteSpace(item.Name))
                preview.Errors.Add($"{RowLabel(row, item)}: 名前が空です。");
        }

        foreach (var (row, item) in incoming)
        {
            if (string.IsNullOrEmpty(item.Id))
            {
                preview.Entries.Add(new MergeEntry { Kind = MergeEntryKind.EmptyId, Incoming = item, SourceRow = row, Decision = MergeDecision.Skip, IncludeRecipes = incomingHasRecipes });
                continue;
            }
            var existing = target.FindItem(item.Id);
            if (existing == null)
            {
                preview.Entries.Add(new MergeEntry { Kind = MergeEntryKind.New, Incoming = item, SourceRow = row, Decision = MergeDecision.Update, IncludeRecipes = incomingHasRecipes });
                continue;
            }
            var entry = new MergeEntry { Kind = MergeEntryKind.Conflict, Incoming = item, Existing = existing, SourceRow = row, IncludeRecipes = incomingHasRecipes, PresentFields = presentFields };
            Diff(entry, existing, item, incomingHasRecipes);
            if (entry.FieldChanges.Count == 0 && entry.RecipeChanges.Count == 0)
            {
                preview.Entries.Add(new MergeEntry { Kind = MergeEntryKind.Identical, Incoming = item, Existing = existing, SourceRow = row, Decision = MergeDecision.Skip });
            }
            else
            {
                entry.Decision = MergeDecision.Update;
                preview.Entries.Add(entry);
            }
        }
        return preview;
    }

    private static void Diff(MergeEntry entry, Item existing, Item incoming, bool withRecipes)
    {
        void F(string field, string name, string before, string after)
        {
            if (entry.Has(field) && before != after) entry.FieldChanges.Add(new FieldChange(name, before, after));
        }
        F("name", "名前", existing.Name, incoming.Name);
        F("summary", "概要", existing.Summary, incoming.Summary);
        F("description", "詳細説明", existing.Description, incoming.Description);
        F("acquisition", "入手方法", existing.Acquisition, incoming.Acquisition);
        if (entry.Has("icon") && (existing.IconPng ?? "") != (incoming.IconPng ?? ""))
            entry.FieldChanges.Add(new FieldChange("アイコン", existing.IconPng == null ? "(なし)" : "(あり)", incoming.IconPng == null ? "(なし)" : "(あり)"));
        if (!withRecipes) return;
        foreach (var r in incoming.Recipes)
        {
            var old = existing.Recipes.FirstOrDefault(x => x.Id == r.Id);
            if (old == null) entry.RecipeChanges.Add($"レシピ追加: {RecipeLabel(r)}");
            else if (!SameRecipe(old, r)) entry.RecipeChanges.Add($"レシピ置換: {RecipeLabel(old)} → {RecipeLabel(r)}");
        }
        foreach (var old in existing.Recipes.Where(o => incoming.Recipes.All(r => r.Id != o.Id)))
            entry.RecipeChanges.Add($"レシピ維持（取り込みデータに無いため残す）: {RecipeLabel(old)}");
    }

    private static bool SameRecipe(Recipe a, Recipe b)
        => a.Name == b.Name && a.OutputQuantity == b.OutputQuantity
           && a.Materials.Count == b.Materials.Count
           && a.Materials.OrderBy(m => m.ItemId, StringComparer.Ordinal).Zip(b.Materials.OrderBy(m => m.ItemId, StringComparer.Ordinal))
               .All(p => p.First.ItemId == p.Second.ItemId && p.First.Quantity == p.Second.Quantity);

    private static string RecipeLabel(Recipe r)
    {
        var name = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name;
        var mats = r.Materials.Count == 0 ? "素材なし" : string.Join(" + ", r.Materials.Select(m => $"{m.ItemId}×{m.Quantity}"));
        return $"{name}（×{r.OutputQuantity} ← {mats}）";
    }

    private static string RowLabel(int row, Item item) => row > 0 ? $"{row} 行目" : $"「{item.Name}」";

    /// <summary>確認済みの計画を適用し、新しい GameData を返す。元の target は変更しない。</summary>
    public static GameData Apply(GameData target, MergePreview preview)
    {
        if (!preview.CanApply) throw new InvalidOperationException("エラーがあるため取り込みを適用できません。");
        var result = target.Clone();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal); // 取り込み側 ID → 実際の ID

        // 1 回目: ID を確定する（新 ID 発行を含む）
        var planned = new List<(MergeEntry Entry, string FinalId)>();
        foreach (var e in preview.Entries)
        {
            if (e.Decision == MergeDecision.Skip) continue;
            if (e.Kind == MergeEntryKind.Identical) continue;
            string finalId;
            if (e.Kind == MergeEntryKind.EmptyId || e.Decision == MergeDecision.CreateNew)
            {
                finalId = IdGenerator.NewItemId(x => result.FindItem(x) != null || planned.Any(p => p.FinalId == x));
            }
            else
            {
                finalId = e.Incoming.Id;
            }
            if (!string.IsNullOrEmpty(e.Incoming.Id)) idMap[e.Incoming.Id] = finalId;
            planned.Add((e, finalId));
        }

        // 2 回目: 反映
        foreach (var (e, finalId) in planned)
        {
            var incoming = e.Incoming.Clone();
            incoming.Id = finalId;
            // 取り込みデータ内で付け替えた ID を素材参照にも反映
            foreach (var r in incoming.Recipes)
                foreach (var m in r.Materials)
                    if (idMap.TryGetValue(m.ItemId, out var mapped)) m.ItemId = mapped;

            var existing = result.FindItem(finalId);
            if (existing == null)
            {
                if (!e.IncludeRecipes) incoming.Recipes.Clear();
                foreach (var r in incoming.Recipes) EnsureUniqueRecipeId(result, r, incoming);
                result.Items.Add(incoming);
            }
            else
            {
                // 取り込みデータに存在する項目だけを更新する（CSV に無い列で既存値を消さない）
                if (e.Has("name")) existing.Name = incoming.Name;
                if (e.Has("summary")) existing.Summary = incoming.Summary;
                if (e.Has("description")) existing.Description = incoming.Description;
                if (e.Has("acquisition")) existing.Acquisition = incoming.Acquisition;
                if (e.Has("icon")) existing.IconPng = incoming.IconPng;
                if (e.IncludeRecipes)
                {
                    foreach (var r in incoming.Recipes)
                    {
                        var idx = existing.Recipes.FindIndex(x => x.Id == r.Id);
                        if (idx >= 0) existing.Recipes[idx] = r;
                        else
                        {
                            EnsureUniqueRecipeId(result, r, existing);
                            existing.Recipes.Add(r);
                        }
                    }
                }
            }
        }

        var v = Validators.ValidateGameData(result);
        if (!v.IsValid) throw new InvalidOperationException("取り込み結果の検証に失敗したため適用を中止しました: " + string.Join(" / ", v.Errors));
        return result;
    }

    /// <summary>別アイテムに同じレシピ ID が存在する場合は新しい ID を振る。</summary>
    private static void EnsureUniqueRecipeId(GameData data, Recipe recipe, Item owner)
    {
        var other = data.FindRecipeOwner(recipe.Id);
        if (string.IsNullOrEmpty(recipe.Id) || (other != null && other.Id != owner.Id))
            recipe.Id = IdGenerator.NewRecipeId(x => data.FindRecipe(x) != null);
    }
}
