using CraftPlanner.Core.Ids;
using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Data;

/// <summary>アイテムがどこから参照されているか。削除前の確認表示に使う。</summary>
public sealed class ItemReference
{
    public enum Kind { RecipeMaterial, PlanNode }
    public Kind Type { get; init; }
    public string Description { get; init; } = "";
    public string? RecipeId { get; init; }
    public string? ProductItemId { get; init; }
    public string? PlanId { get; init; }
    public string? NodeId { get; init; }
}

/// <summary>
/// アイテム・レシピの登録・編集・削除。GameData を直接書き換える。
/// 保存はリポジトリが行う。参照されているアイテムの削除は拒否し、参照箇所を返す。
/// </summary>
public sealed class GameDataService
{
    public GameData Data { get; }

    public GameDataService(GameData data) => Data = data;

    // ---- アイテム ----

    public Item CreateItem(string name, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名前を入力してください。");
        string newId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (!IdGenerator.IsValidUserId(id)) throw new ArgumentException("ID に使える文字は英数字・ハイフン・アンダースコア・ドットです（64 文字以内）。");
            if (Data.FindItem(id) != null) throw new InvalidOperationException($"ID「{id}」は既に使われています。");
            newId = id;
        }
        else
        {
            newId = IdGenerator.NewItemId(x => Data.FindItem(x) != null);
        }
        var item = new Item { Id = newId, Name = name.Trim() };
        Data.Items.Add(item);
        return item;
    }

    public void UpdateItem(string itemId, string name, string summary, string description, string acquisition, string? iconPng)
    {
        var item = Data.FindItem(itemId) ?? throw new ArgumentException($"アイテム「{itemId}」が存在しません。");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名前を入力してください。");
        item.Name = name.Trim();
        item.Summary = summary;
        item.Description = description;
        item.Acquisition = acquisition;
        item.IconPng = iconPng;
    }

    /// <summary>参照を調べる。plans には同じゲームデータを参照する計画を渡す。</summary>
    public IReadOnlyList<ItemReference> FindReferences(string itemId, IEnumerable<Plan>? plans = null)
    {
        var refs = new List<ItemReference>();
        foreach (var usage in Data.FindUsages(itemId))
        {
            var recipeName = string.IsNullOrWhiteSpace(usage.Recipe.Name) ? "(名称なし)" : usage.Recipe.Name;
            refs.Add(new ItemReference
            {
                Type = ItemReference.Kind.RecipeMaterial,
                RecipeId = usage.Recipe.Id,
                ProductItemId = usage.Product.Id,
                Description = $"レシピ: 「{usage.Product.Name}」の {recipeName}（素材 ×{usage.Quantity}）",
            });
        }
        if (plans != null)
        {
            foreach (var plan in plans.Where(p => p.GameDataId == Data.Id))
            {
                foreach (var node in plan.ItemNodes.Where(n => n.ItemId == itemId))
                {
                    refs.Add(new ItemReference
                    {
                        Type = ItemReference.Kind.PlanNode,
                        PlanId = plan.Id,
                        NodeId = node.Id,
                        Description = $"制作計画: 「{plan.Name}」のノード",
                    });
                }
                if (plan.Inventory.ContainsKey(itemId))
                {
                    refs.Add(new ItemReference
                    {
                        Type = ItemReference.Kind.PlanNode,
                        PlanId = plan.Id,
                        Description = $"制作計画: 「{plan.Name}」の所持数",
                    });
                }
            }
        }
        return refs;
    }

    /// <summary>参照が残っている場合は削除せず例外で参照一覧を返す。</summary>
    public void DeleteItem(string itemId, IEnumerable<Plan>? plans = null)
    {
        var item = Data.FindItem(itemId) ?? throw new ArgumentException($"アイテム「{itemId}」が存在しません。");
        var refs = FindReferences(itemId, plans);
        if (refs.Count > 0) throw new ItemReferencedException(item, refs);
        Data.Items.Remove(item);
    }

    // ---- レシピ ----

    public Recipe AddRecipe(string itemId, string name = "", int outputQuantity = 1)
    {
        var item = Data.FindItem(itemId) ?? throw new ArgumentException($"アイテム「{itemId}」が存在しません。");
        if (outputQuantity < 1) throw new ArgumentException("完成数量は 1 以上にしてください。");
        var recipe = new Recipe
        {
            Id = IdGenerator.NewRecipeId(x => Data.FindRecipe(x) != null),
            Name = name,
            OutputQuantity = outputQuantity,
        };
        item.Recipes.Add(recipe);
        return recipe;
    }

    public void UpdateRecipe(string recipeId, string name, int outputQuantity, IEnumerable<RecipeMaterial> materials)
    {
        var recipe = Data.FindRecipe(recipeId) ?? throw new ArgumentException($"レシピ「{recipeId}」が存在しません。");
        var owner = Data.FindRecipeOwner(recipeId)!;
        if (outputQuantity < 1) throw new ArgumentException("完成数量は 1 以上にしてください。");
        var list = materials.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in list)
        {
            if (m.Quantity < 1) throw new ArgumentException("素材の数量は 1 以上にしてください。");
            if (Data.FindItem(m.ItemId) == null) throw new ArgumentException($"素材アイテム「{m.ItemId}」が存在しません。");
            if (!seen.Add(m.ItemId))
                throw new ArgumentException($"同じ素材「{Data.FindItem(m.ItemId)!.Name}」が重複しています。既存の行の数量を編集してください。");
        }
        recipe.Name = name;
        recipe.OutputQuantity = outputQuantity;
        recipe.Materials = list.Select(m => new RecipeMaterial { ItemId = m.ItemId, Quantity = m.Quantity }).ToList();
        _ = owner;
    }

    public void DeleteRecipe(string recipeId)
    {
        var owner = Data.FindRecipeOwner(recipeId) ?? throw new ArgumentException($"レシピ「{recipeId}」が存在しません。");
        owner.Recipes.RemoveAll(r => r.Id == recipeId);
    }
}

public sealed class ItemReferencedException : InvalidOperationException
{
    public Item Item { get; }
    public IReadOnlyList<ItemReference> References { get; }

    public ItemReferencedException(Item item, IReadOnlyList<ItemReference> references)
        : base($"「{item.Name}」は {references.Count} 箇所から参照されているため削除できません。先に参照を解消してください。")
    {
        Item = item;
        References = references;
    }
}
