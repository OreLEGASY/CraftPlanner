using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftPlanner.App.Services;
using CraftPlanner.Core.Calculation;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.ViewModels;

/// <summary>キャンバス上のアイテムノード 1 つ分の表示状態。モデル（ItemNode）と計算結果（NodeResult）を束ねる。</summary>
public sealed partial class ItemNodeViewModel : ObservableObject
{
    private readonly PlanViewModel _owner;

    public ItemNodeViewModel(PlanViewModel owner, ItemNode model)
    {
        _owner = owner;
        Model = model;
        Id = model.Id;
        ActualWidth = 220;
        ActualHeight = owner.IsDetailed ? 150 : 64;
    }

    public string Id { get; }
    public ItemNode Model { get; set; }
    public Item? Item { get; private set; }

    // ---- 位置（ドラッグ中はここを直接更新し、確定時に履歴へ記録する） ----
    public double X
    {
        get => Model.X;
        set { if (Model.X != value) { Model.X = value; OnPropertyChanged(); } }
    }
    public double Y
    {
        get => Model.Y;
        set { if (Model.Y != value) { Model.Y = value; OnPropertyChanged(); } }
    }

    [ObservableProperty] private double _actualWidth;
    [ObservableProperty] private double _actualHeight;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isVisible = true;

    // ---- 表示用（Update で一括更新） ----
    public string ItemId => Model.ItemId;
    public string Name { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string Acquisition { get; private set; } = "";
    public ImageSource? Icon { get; private set; }
    public bool HasIcon => Icon != null;
    public bool IsTarget { get; private set; }
    public int Demand { get; private set; }
    public int NetDemand { get; private set; }
    public int Crafts { get; private set; }
    public int OutputQuantity { get; private set; }
    public int Produced { get; private set; }
    public int ItemShortage { get; private set; }
    public int NodeShortage { get; private set; }
    public int Surplus { get; private set; }
    public bool IsCrafted { get; private set; }
    public bool IsExpanded => Model.IsExpanded;
    public bool IsCollapsed => Model.IsCollapsed;
    public bool HasChildren { get; private set; }
    public bool HasRecipes { get; private set; }
    public bool HasMultipleRecipes { get; private set; }
    public bool CanExpand => HasRecipes && !Model.IsExpanded;
    public bool IsCycle => Model.IsCycleTerminal;
    public bool IsUncertain { get; private set; }
    public Craftability Craftability { get; private set; }
    public string RecipeName { get; private set; } = "";
    public string WarningsText { get; private set; } = "";
    public bool HasWarnings => WarningsText.Length > 0;
    public bool IsDetailed => _owner.IsDetailed;
    public string ToolTipText { get; private set; } = "";
    public string CraftsText { get; private set; } = "";
    public ObservableCollection<MaterialLineViewModel> MaterialLines { get; } = new();

    /// <summary>目標の必要数量。子ノードでは読み取り専用。</summary>
    public int RequiredQuantity
    {
        get => IsTarget ? Model.RequiredQuantity : Demand;
        set
        {
            if (!IsTarget || value == Model.RequiredQuantity) return;
            _owner.SetRequiredQuantity(Id, value);
        }
    }

    /// <summary>所持数（計画内・同アイテムで共有）。</summary>
    public int OnHand
    {
        get => _onHand;
        set
        {
            if (value == _onHand) return;
            _owner.SetInventory(ItemId, value);
        }
    }
    private int _onHand;

    public void Update(NodeResult? result, ItemTotal? total)
    {
        var game = _owner.Game;
        Item = game.FindItem(Model.ItemId);
        Name = Item?.Name ?? $"(不明: {Model.ItemId})";
        Summary = Item?.Summary ?? "";
        Acquisition = Item?.Acquisition ?? "";
        Icon = IconService.Decode(Item?.IconPng);
        IsTarget = _owner.Plan.IsTarget(Id);
        HasRecipes = Item?.Recipes.Count > 0;
        HasMultipleRecipes = Item?.Recipes.Count > 1;
        HasChildren = _owner.Plan.ChildEdgesOf(Id).Any();

        Demand = result?.Demand ?? 0;
        NetDemand = result?.NetDemand ?? 0;
        Crafts = result?.Crafts ?? 0;
        OutputQuantity = result?.OutputQuantity ?? 0;
        Produced = result?.Produced ?? 0;
        NodeShortage = result?.Shortage ?? 0;
        IsCrafted = result?.IsCrafted ?? false;
        IsUncertain = result?.IsUncertain ?? false;
        Craftability = result?.Craftability ?? Craftability.None;
        ItemShortage = total?.Shortage ?? 0;
        Surplus = total?.Surplus ?? 0;
        _onHand = total?.OnHand ?? _owner.Plan.Inventory.GetValueOrDefault(Model.ItemId);

        var recipe = Model.SelectedRecipeId == null ? null : Item?.Recipes.FirstOrDefault(r => r.Id == Model.SelectedRecipeId);
        RecipeName = recipe == null ? (HasRecipes ? "(レシピ未選択)" : "レシピなし")
            : string.IsNullOrWhiteSpace(recipe.Name) ? $"レシピ（×{recipe.OutputQuantity}）" : $"{recipe.Name}（×{recipe.OutputQuantity}）";
        CraftsText = IsCrafted ? $"制作 {Crafts} 回 → {Produced} 個" : "";

        var warnings = new List<string>();
        if (result != null) warnings.AddRange(result.Warnings);
        if (IsCycle) warnings.Add("循環レシピの地点です。数量は不確定です。");
        else if (IsUncertain) warnings.Add("循環の影響を受けるため数量は不確定です。");
        WarningsText = string.Join("\n", warnings);

        var tip = new StringBuilder(Name);
        if (!string.IsNullOrWhiteSpace(Summary)) tip.Append('\n').Append(Summary);
        if (!string.IsNullOrWhiteSpace(Acquisition)) tip.Append("\n入手: ").Append(Acquisition);
        ToolTipText = tip.ToString();

        RebuildMaterialLines(recipe, result);
        OnPropertyChanged(string.Empty);
    }

    private void RebuildMaterialLines(Recipe? recipe, NodeResult? result)
    {
        MaterialLines.Clear();
        if (recipe == null || !Model.IsExpanded) return;
        var plan = _owner.Plan;
        foreach (var m in recipe.Materials)
        {
            var edge = plan.ChildEdgesOf(Id).FirstOrDefault(e => e.MaterialItemId == m.ItemId);
            var severed = plan.SeveredEdges.Any(s => s.ParentNodeId == Id && s.MaterialItemId == m.ItemId);
            var name = _owner.Game.FindItem(m.ItemId)?.Name ?? m.ItemId;
            MaterialLines.Add(new MaterialLineViewModel(_owner, Id, m.ItemId, name, m.Quantity,
                required: (result?.Crafts ?? 0) * m.Quantity,
                onHand: plan.Inventory.GetValueOrDefault(m.ItemId),
                edgeId: edge?.Id, isSevered: severed));
        }
    }

    public void RefreshHighlight(string? hoveredItemId)
    {
        IsHighlighted = hoveredItemId != null && hoveredItemId == Model.ItemId;
        IsDimmed = hoveredItemId != null && hoveredItemId != Model.ItemId;
    }

    [RelayCommand] private void Expand() => _owner.Expand(Id);
    [RelayCommand] private void ToggleCollapse() => _owner.ToggleCollapse(Id);
    [RelayCommand] private void ChangeRecipe() => _owner.RequestRecipeChange(this);
    [RelayCommand] private void Delete() => _owner.DeleteNodes(new[] { Id });
    [RelayCommand] private void Arrange() => _owner.Arrange(Id);
    [RelayCommand] private void Duplicate() => _owner.DuplicateNodes(new[] { Id });
    [RelayCommand] private void SeverFromParent() => _owner.SeverParentOf(Id);
}

/// <summary>詳細表示のノード内に出す素材 1 行。切断・再接続の操作を持つ。</summary>
public sealed partial class MaterialLineViewModel : ObservableObject
{
    private readonly PlanViewModel _owner;
    private readonly string _parentNodeId;

    public MaterialLineViewModel(PlanViewModel owner, string parentNodeId, string itemId, string name, int perCraft, int required, int onHand, string? edgeId, bool isSevered)
    {
        _owner = owner;
        _parentNodeId = parentNodeId;
        ItemId = itemId;
        Name = name;
        PerCraft = perCraft;
        Required = required;
        OnHand = onHand;
        EdgeId = edgeId;
        IsSevered = isSevered;
    }

    public string ItemId { get; }
    public string Name { get; }
    public int PerCraft { get; }
    public int Required { get; }
    public int OnHand { get; }
    public string? EdgeId { get; }
    public bool IsSevered { get; }
    public bool IsConnected => EdgeId != null;
    public bool IsMissing => EdgeId == null && !IsSevered;
    public string StateText => IsSevered ? "切断中" : IsMissing ? "ノードなし" : "";

    [RelayCommand] private void Sever() { if (EdgeId != null) _owner.Sever(EdgeId); }
    [RelayCommand] private void Reconnect() => _owner.Reconnect(_parentNodeId, ItemId);
}
