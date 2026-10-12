using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.ViewModels;

/// <summary>有効な接続線。親（製品）の右端中央から子（素材）の左端中央へベジェ曲線を引く。</summary>
public sealed partial class EdgeViewModel : ObservableObject
{
    private readonly PlanViewModel _owner;

    public EdgeViewModel(PlanViewModel owner, PlanEdge edge, ItemNodeViewModel parent, ItemNodeViewModel child)
    {
        _owner = owner;
        Id = edge.Id;
        Parent = parent;
        Child = child;
        MaterialItemId = edge.MaterialItemId;
        parent.PropertyChanged += OnEndpointChanged;
        child.PropertyChanged += OnEndpointChanged;
        Recalculate();
    }

    public string Id { get; }
    public ItemNodeViewModel Parent { get; }
    public ItemNodeViewModel Child { get; }
    public string MaterialItemId { get; }

    [ObservableProperty] private string _pathData = "";
    [ObservableProperty] private double _midX;
    [ObservableProperty] private double _midY;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isEmphasized;
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private string _label = "";

    public void Detach()
    {
        Parent.PropertyChanged -= OnEndpointChanged;
        Child.PropertyChanged -= OnEndpointChanged;
    }

    private void OnEndpointChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(ItemNodeViewModel.X) or nameof(ItemNodeViewModel.Y)
                or nameof(ItemNodeViewModel.ActualWidth) or nameof(ItemNodeViewModel.ActualHeight)
                or nameof(ItemNodeViewModel.IsVisible) or nameof(ItemNodeViewModel.IsSelected)
                or nameof(ItemNodeViewModel.IsDimmed) or nameof(ItemNodeViewModel.IsHighlighted))
        {
            Recalculate();
        }
    }

    public void Recalculate()
    {
        var x1 = Parent.X + Parent.ActualWidth;
        var y1 = Parent.Y + Parent.ActualHeight / 2;
        var x2 = Child.X;
        var y2 = Child.Y + Child.ActualHeight / 2;
        PathData = OrthogonalPath(x1, y1, x2, y2);
        // 必要数のラベルは最後の横線の右端（素材ノードの左端に接する位置）に置く
        MidX = x2 - 14;
        MidY = y2;
        IsVisible = Parent.IsVisible && Child.IsVisible;
        IsEmphasized = Parent.IsSelected || Child.IsSelected || Parent.IsHighlighted || Child.IsHighlighted;
        IsDimmed = Parent.IsDimmed && Child.IsDimmed;
        Label = Child.Demand > 0 ? Child.Demand.ToString() : "";
    }

    /// <summary>
    /// 直角の折れ線。通常は中間の x で縦に折れる 3 本線。
    /// 子が親より左にある場合は、親の右へ少し出てから縦に移動し、子の左へ回り込む 5 本線にする。
    /// </summary>
    public static string OrthogonalPath(double x1, double y1, double x2, double y2)
    {
        const double stub = 24;
        if (x2 >= x1 + stub * 2)
        {
            var mx = Math.Round((x1 + x2) / 2);
            return string.Create(CultureInfo.InvariantCulture, $"M {x1},{y1} L {mx},{y1} L {mx},{y2} L {x2},{y2}");
        }
        var my = Math.Round((y1 + y2) / 2);
        return string.Create(CultureInfo.InvariantCulture,
            $"M {x1},{y1} L {x1 + stub},{y1} L {x1 + stub},{my} L {x2 - stub},{my} L {x2 - stub},{y2} L {x2},{y2}");
    }

    [RelayCommand] private void Sever() => _owner.Sever(Id);
    [RelayCommand] private void DeleteEdge() => _owner.DeleteEdge(Id);
}

/// <summary>切断された接続。親の右側に短い破線と「再接続」マーカーを出す。クリックで再接続する。</summary>
public sealed partial class SeveredEdgeViewModel : ObservableObject
{
    private readonly PlanViewModel _owner;
    private readonly int _index;

    public SeveredEdgeViewModel(PlanViewModel owner, SeveredEdge severed, ItemNodeViewModel parent, int index, string materialName)
    {
        _owner = owner;
        _index = index;
        Parent = parent;
        ParentNodeId = severed.ParentNodeId;
        MaterialItemId = severed.MaterialItemId;
        Label = $"✂ {materialName} を再接続";
        parent.PropertyChanged += OnParentChanged;
        Recalculate();
    }

    public ItemNodeViewModel Parent { get; }
    public string ParentNodeId { get; }
    public string MaterialItemId { get; }
    public string Label { get; }

    [ObservableProperty] private string _pathData = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private bool _isVisible = true;

    public void Detach() => Parent.PropertyChanged -= OnParentChanged;

    private void OnParentChanged(object? sender, PropertyChangedEventArgs e) => Recalculate();

    private void Recalculate()
    {
        // 有効な接続線（中央）と重ならないよう、ポートの少し下から順に並べる
        var x1 = Parent.X + Parent.ActualWidth;
        var y1 = Parent.Y + Parent.ActualHeight / 2 + 28 + _index * 22;
        var x2 = x1 + 36;
        PathData = string.Create(CultureInfo.InvariantCulture, $"M {x1},{y1} L {x2},{y1}");
        X = x2 + 2;
        Y = y1 - 10;
        IsVisible = Parent.IsVisible;
    }

    [RelayCommand] private void Reconnect() => _owner.Reconnect(ParentNodeId, MaterialItemId);
}
