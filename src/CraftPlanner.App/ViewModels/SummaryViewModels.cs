using CommunityToolkit.Mvvm.ComponentModel;
using CraftPlanner.Core.Calculation;

namespace CraftPlanner.App.ViewModels;

/// <summary>目標 1 つ分の参考値（所持品を配分しない展開結果）。</summary>
public sealed class TargetSummaryViewModel
{
    public TargetSummaryViewModel(string title, int requiredQuantity, IEnumerable<TargetLineViewModel> lines)
    {
        Title = title;
        RequiredQuantity = requiredQuantity;
        Lines = lines.ToList();
    }

    public string Title { get; }
    public int RequiredQuantity { get; }
    public string Header => $"{Title} ×{RequiredQuantity}";
    public List<TargetLineViewModel> Lines { get; }
}

public sealed record TargetLineViewModel(string Name, int Required, int OnHand, int ReferenceShortage, bool IsIntermediate, bool IsUncertain)
{
    public string Kind => IsIntermediate ? "制作" : "調達";
}

/// <summary>計画全体のアイテム別集計（確定値）。所持数はここからも編集できる。</summary>
public sealed partial class ItemTotalViewModel : ObservableObject
{
    private readonly PlanViewModel _owner;

    public ItemTotalViewModel(PlanViewModel owner, ItemTotal total, string name)
    {
        _owner = owner;
        ItemId = total.ItemId;
        Name = name;
        Gross = total.Gross;
        _onHand = total.OnHand;
        Net = total.Net;
        Crafts = total.Crafts;
        Produced = total.Produced;
        Surplus = total.Surplus;
        Shortage = total.Shortage;
        IsUncertain = total.IsUncertain;
        IsCyclic = total.IsCyclic;
    }

    public string ItemId { get; }
    public string Name { get; }
    public int Gross { get; }
    public int Net { get; }
    public int Crafts { get; }
    public int Produced { get; }
    public int Surplus { get; }
    public int Shortage { get; }
    public bool IsUncertain { get; }
    public bool IsCyclic { get; }
    public bool HasShortage => Shortage > 0;
    public string ShortageText => IsUncertain ? $"{Shortage}?" : Shortage.ToString();

    private int _onHand;
    public int OnHand
    {
        get => _onHand;
        set
        {
            if (value == _onHand) return;
            _owner.SetInventory(ItemId, value);
        }
    }
}
