namespace CraftPlanner.Core.Models;

/// <summary>
/// ゲーム内のあらゆる対象（採取素材・加工素材・完成品）を表す。
/// 種類ごとに型を分けず、レシピの有無だけで性質が決まる。
/// </summary>
public sealed class Item
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Acquisition { get; set; } = "";

    /// <summary>PNG 画像を Base64 で埋め込む。null なら未設定。</summary>
    public string? IconPng { get; set; }

    /// <summary>このアイテムを出力するレシピ。使用先のレシピはここに保存せず、GameData から導出する。</summary>
    public List<Recipe> Recipes { get; set; } = new();

    public Item Clone()
    {
        return new Item
        {
            Id = Id,
            Name = Name,
            Summary = Summary,
            Description = Description,
            Acquisition = Acquisition,
            IconPng = IconPng,
            Recipes = Recipes.Select(r => r.Clone()).ToList(),
        };
    }
}
