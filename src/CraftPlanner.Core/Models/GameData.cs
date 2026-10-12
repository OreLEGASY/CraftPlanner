namespace CraftPlanner.Core.Models;

/// <summary>1 つのゲームに属するアイテムとレシピの集合。1 ファイルに保存する。</summary>
public sealed class GameData
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<Item> Items { get; set; } = new();

    public Item? FindItem(string itemId) => Items.FirstOrDefault(i => i.Id == itemId);

    public Recipe? FindRecipe(string recipeId)
        => Items.SelectMany(i => i.Recipes).FirstOrDefault(r => r.Id == recipeId);

    /// <summary>レシピ ID からそのレシピを出力するアイテムを返す。</summary>
    public Item? FindRecipeOwner(string recipeId)
        => Items.FirstOrDefault(i => i.Recipes.Any(r => r.Id == recipeId));

    /// <summary>
    /// 指定アイテムを素材として使うレシピの一覧（使用先）。
    /// 使用先は保存せず、常にここで導出する（二重管理を避ける）。
    /// </summary>
    public IReadOnlyList<RecipeUsage> FindUsages(string itemId)
    {
        var list = new List<RecipeUsage>();
        foreach (var owner in Items)
        {
            foreach (var recipe in owner.Recipes)
            {
                var mat = recipe.Materials.FirstOrDefault(m => m.ItemId == itemId);
                if (mat != null)
                {
                    list.Add(new RecipeUsage(owner, recipe, mat.Quantity));
                }
            }
        }
        return list;
    }

    public GameData Clone()
    {
        return new GameData
        {
            FormatVersion = FormatVersion,
            Id = Id,
            Name = Name,
            Items = Items.Select(i => i.Clone()).ToList(),
        };
    }
}

/// <summary>あるアイテムを素材として使っているレシピの参照情報。</summary>
public sealed record RecipeUsage(Item Product, Recipe Recipe, int Quantity);
