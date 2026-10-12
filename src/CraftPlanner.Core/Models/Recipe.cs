namespace CraftPlanner.Core.Models;

/// <summary>1 回の制作で <see cref="OutputQuantity"/> 個の完成品を作るレシピ。素材 0 種類も許可する（採取・購入など）。</summary>
public sealed class Recipe
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int OutputQuantity { get; set; } = 1;
    public List<RecipeMaterial> Materials { get; set; } = new();

    public Recipe Clone()
    {
        return new Recipe
        {
            Id = Id,
            Name = Name,
            OutputQuantity = OutputQuantity,
            Materials = Materials.Select(m => new RecipeMaterial { ItemId = m.ItemId, Quantity = m.Quantity }).ToList(),
        };
    }
}

public sealed class RecipeMaterial
{
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; } = 1;
}
