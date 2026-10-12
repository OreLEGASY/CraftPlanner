using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CraftPlanner.App.Services;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.ViewModels;

/// <summary>アイテム一覧（カード表示）。検索と並び替えを行う。</summary>
public sealed partial class ItemListViewModel : ObservableObject
{
    public static readonly string[] SortOptions = { "名前順", "ID 順", "レシピ数順", "使用先の多い順" };

    public ObservableCollection<ItemCardViewModel> Cards { get; } = new();

    [ObservableProperty] private GameData? _game;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private ItemCardViewModel? _selectedCard;
    [ObservableProperty] private string _countText = "";

    partial void OnGameChanged(GameData? value) => Refresh();
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSortIndexChanged(int value) => Refresh();

    public void Refresh()
    {
        var selectedId = SelectedCard?.Id;
        Cards.Clear();
        if (Game == null) { CountText = ""; return; }

        var q = SearchText.Trim();
        IEnumerable<Item> items = Game.Items;
        if (q.Length > 0)
        {
            // 検索対象: 名前・概要・詳細説明・入手方法
            items = items.Where(i =>
                Contains(i.Name, q) || Contains(i.Summary, q) || Contains(i.Description, q) || Contains(i.Acquisition, q));
        }
        var cards = items.Select(i => new ItemCardViewModel(i, Game.FindUsages(i.Id).Count));
        cards = SortIndex switch
        {
            1 => cards.OrderBy(c => c.Id, StringComparer.Ordinal),
            2 => cards.OrderByDescending(c => c.RecipeCount).ThenBy(c => c.Name, StringComparer.CurrentCulture),
            3 => cards.OrderByDescending(c => c.UsageCount).ThenBy(c => c.Name, StringComparer.CurrentCulture),
            _ => cards.OrderBy(c => c.Name, StringComparer.CurrentCulture),
        };
        foreach (var c in cards) Cards.Add(c);
        CountText = q.Length > 0 ? $"{Cards.Count} / {Game.Items.Count} 件" : $"{Cards.Count} 件";
        if (selectedId != null) SelectedCard = Cards.FirstOrDefault(c => c.Id == selectedId);
    }

    private static bool Contains(string? text, string q)
        => !string.IsNullOrEmpty(text) && text.Contains(q, StringComparison.CurrentCultureIgnoreCase);
}

public sealed class ItemCardViewModel
{
    public ItemCardViewModel(Item item, int usageCount)
    {
        Item = item;
        UsageCount = usageCount;
        Icon = IconService.Decode(item.IconPng);
    }

    public Item Item { get; }
    public string Id => Item.Id;
    public string Name => Item.Name;
    public string Summary => Item.Summary;
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;
    public int RecipeCount => Item.Recipes.Count;
    public int UsageCount { get; }
    public string MetaText => $"レシピ {RecipeCount} / 使用先 {UsageCount}";
    public string ToolTipText
    {
        get
        {
            var parts = new List<string> { Item.Name, "ID: " + Item.Id };
            if (!string.IsNullOrWhiteSpace(Item.Summary)) parts.Add(Item.Summary);
            if (!string.IsNullOrWhiteSpace(Item.Acquisition)) parts.Add("入手: " + Item.Acquisition);
            return string.Join("\n", parts);
        }
    }
}
