using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.ViewModels;

/// <summary>メモノードの表示状態。テキストはフォーカス喪失時に 1 回の編集として履歴に記録する。</summary>
public sealed partial class MemoNodeViewModel : ObservableObject
{
    private readonly PlanViewModel _owner;

    public MemoNodeViewModel(PlanViewModel owner, MemoNode model)
    {
        _owner = owner;
        Model = model;
        Id = model.Id;
    }

    public string Id { get; }
    public MemoNode Model { get; set; }

    [ObservableProperty] private bool _isSelected;

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
    public double Width
    {
        get => Model.Width;
        set { var v = Math.Max(80, value); if (Model.Width != v) { Model.Width = v; OnPropertyChanged(); } }
    }
    public double Height
    {
        get => Model.Height;
        set { var v = Math.Max(40, value); if (Model.Height != v) { Model.Height = v; OnPropertyChanged(); } }
    }

    public string Text
    {
        get => Model.Text;
        set { if (Model.Text != value) _owner.SetMemoText(Id, value); }
    }

    public string ColorKey => Model.ColorKey;

    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand] private void SetColor(string key) => _owner.SetMemoColor(Id, key);
    [RelayCommand] private void Delete() => _owner.DeleteNodes(new[] { Id });
    [RelayCommand] private void Duplicate() => _owner.DuplicateNodes(new[] { Id });
}
