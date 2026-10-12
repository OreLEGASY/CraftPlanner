using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.ImportExport;

public sealed class CsvReadResult
{
    public List<(int Row, Item Item)> Items { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>
    /// CSV に存在した項目（name, summary, description, acquisition）。
    /// 存在しない列は「変更なし」として扱い、既存の値を空文字で上書きしない。
    /// </summary>
    public HashSet<string> PresentFields { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// アイテムの CSV。列: id,name,summary,description,acquisition。アイコンは扱わない（JSON を使う）。
/// 読み込みは「取り込み候補」を作るだけで、既存データへの反映は MergePlanner が行う。
/// </summary>
public static class ItemCsv
{
    public static readonly string[] Header = { "id", "name", "summary", "description", "acquisition" };

    public static string Export(IEnumerable<Item> items)
    {
        var rows = new List<string[]> { Header };
        rows.AddRange(items.Select(i => new[] { i.Id, i.Name, i.Summary, i.Description, i.Acquisition }));
        return Csv.Write(rows);
    }

    public static CsvReadResult Read(string text)
    {
        var result = new CsvReadResult();
        var rows = Csv.Parse(text);
        if (rows.Count == 0) { result.Errors.Add("CSV が空です。"); return result; }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++) index.TryAdd(header[i], i);
        if (!index.ContainsKey("name"))
        {
            result.Errors.Add("1 行目に name 列が見つかりません。列名は id,name,summary,description,acquisition です。");
            return result;
        }
        foreach (var col in new[] { "name", "summary", "description", "acquisition" })
            if (index.ContainsKey(col)) result.PresentFields.Add(col);
        string Get(string[] row, string col) => index.TryGetValue(col, out var i) && i < row.Length ? row[i].Trim() : "";

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var item = new Item
            {
                Id = Get(row, "id"),
                Name = Get(row, "name"),
                Summary = Get(row, "summary"),
                Description = Get(row, "description"),
                Acquisition = Get(row, "acquisition"),
            };
            if (string.IsNullOrWhiteSpace(item.Name) && string.IsNullOrWhiteSpace(item.Id)) continue; // 完全な空行
            result.Items.Add((r + 1, item)); // 1 始まりの行番号（ヘッダーが 1 行目）
        }
        return result;
    }
}

/// <summary>
/// レシピの CSV エクスポート（提案形式。インポートは列構成の確認後に実装する）。
/// 1 素材 1 行: recipe_id,output_item_id,output_item_name,recipe_name,output_quantity,material_item_id,material_item_name,material_quantity
/// 素材 0 種類のレシピは素材列を空にした 1 行で表す。
/// </summary>
public static class RecipeCsv
{
    public static readonly string[] Header =
    {
        "recipe_id", "output_item_id", "output_item_name", "recipe_name", "output_quantity",
        "material_item_id", "material_item_name", "material_quantity",
    };

    public static string Export(GameData data)
    {
        var rows = new List<string[]> { Header };
        foreach (var item in data.Items)
        {
            foreach (var recipe in item.Recipes)
            {
                if (recipe.Materials.Count == 0)
                {
                    rows.Add(new[] { recipe.Id, item.Id, item.Name, recipe.Name, recipe.OutputQuantity.ToString(), "", "", "" });
                    continue;
                }
                foreach (var m in recipe.Materials)
                {
                    var matName = data.FindItem(m.ItemId)?.Name ?? "";
                    rows.Add(new[] { recipe.Id, item.Id, item.Name, recipe.Name, recipe.OutputQuantity.ToString(), m.ItemId, matName, m.Quantity.ToString() });
                }
            }
        }
        return Csv.Write(rows);
    }
}
