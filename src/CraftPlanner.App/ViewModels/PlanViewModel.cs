using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftPlanner.App.Services;
using CraftPlanner.Core.Calculation;
using CraftPlanner.Core.History;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Planning;
using CraftPlanner.Core.Storage;

namespace CraftPlanner.App.ViewModels;

/// <summary>
/// 制作計画 1 件（タブ 1 つ）の状態。Plan モデルを PlanEditor で編集し、編集ごとに
/// 履歴を記録 → 再計算 → ノード／接続のビューモデルを同期 → 自動保存を予約する。
/// </summary>
public sealed partial class PlanViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly HistoryManager<Plan> _history = new(p => p.Clone(), 200);
    private readonly DispatcherTimer _autosave;
    private readonly Dictionary<string, ItemNodeViewModel> _nodeVms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoNodeViewModel> _memoVms = new(StringComparer.Ordinal);
    private Plan? _dragSnapshot;

    /// <summary>アプリ内クリップボード（ゲームデータ ID と一緒に保持し、別ゲームへの貼り付けを防ぐ）。</summary>
    private static (string GameId, PlanClipboardData Data)? s_clipboard;

    public PlanViewModel(AppServices services, Plan plan, GameData game)
    {
        _services = services;
        Plan = plan;
        Game = game;
        Editor = new PlanEditor(plan, game, new AutoLayout(SizeOf));
        _name = plan.Name;
        _zoom = plan.View.Zoom <= 0 ? 1 : plan.View.Zoom;
        _offsetX = plan.View.OffsetX;
        _offsetY = plan.View.OffsetY;
        _isDetailed = plan.DisplayMode == DisplayMode.Detailed;
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveNow(); };
        Result = PlanCalculator.Calculate(plan, game);
        Sync();
    }

    public Plan Plan { get; private set; }
    public GameData Game { get; }
    public PlanEditor Editor { get; private set; }
    public CalculationResult Result { get; private set; }

    public ObservableCollection<ItemNodeViewModel> ItemNodes { get; } = new();
    public ObservableCollection<MemoNodeViewModel> MemoNodes { get; } = new();
    public ObservableCollection<EdgeViewModel> Edges { get; } = new();
    public ObservableCollection<SeveredEdgeViewModel> SeveredEdges { get; } = new();
    public ObservableCollection<TargetSummaryViewModel> Targets { get; } = new();
    public ObservableCollection<ItemTotalViewModel> Totals { get; } = new();
    public HashSet<string> SelectedIds { get; } = new(StringComparer.Ordinal);

    [ObservableProperty] private string _name;
    [ObservableProperty] private double _zoom;
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _saveFailed;
    [ObservableProperty] private string? _hoveredItemId;
    [ObservableProperty] private bool _isDetailed;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private int _totalShortageCount;

    public string TabTitle => IsDirty ? Name + " *" : Name;

    /// <summary>レシピ変更ダイアログを開くためのフック（ビューが設定する）。戻り値は採用するレシピ ID。</summary>
    public Func<ItemNodeViewModel, string?>? RecipePicker { get; set; }

    /// <summary>ノードの実サイズ（自動配置用）。未計測なら既定値。</summary>
    private (double, double) SizeOf(Plan plan, string nodeId)
    {
        if (_nodeVms.TryGetValue(nodeId, out var vm) && vm.ActualWidth > 0 && vm.ActualHeight > 0)
            return (vm.ActualWidth, vm.ActualHeight);
        {
            // 未計測のノード（新規に生成したものなど）は、既存ノードの実測値に合わせる
            var sample = _nodeVms.Values.FirstOrDefault(v => v.ActualWidth > 0);
            if (sample != null) return (sample.ActualWidth, sample.ActualHeight);
        }
        return AutoLayout.DefaultSize(plan, nodeId);
    }

    // ------------------------------------------------------------------
    // 編集の共通経路
    // ------------------------------------------------------------------

    /// <summary>編集を 1 件として実行する。操作前の状態を履歴に記録し、再計算・同期・自動保存を行う。</summary>
    public EditOutcome? Edit(Func<PlanEditor, EditOutcome?> action)
    {
        var before = Plan.Clone();
        EditOutcome? outcome;
        try
        {
            outcome = action(Editor);
        }
        catch (Exception ex)
        {
            // 失敗した編集はロールバックする（モデルが途中状態で残らないように）
            ReplacePlan(before);
            Refresh();
            ShowNotice(ex.Message);
            _services.Log.Warn("編集に失敗: " + ex.Message, ex);
            return null;
        }
        if (IsSame(before, Plan)) return outcome;
        _history.RecordPrepared(before);
        AfterEdit();
        if (outcome != null && outcome.Notices.Count > 0) ShowNotice(string.Join("\n", outcome.Notices.Distinct()));
        return outcome;
    }

    public void Edit(Action<PlanEditor> action) => Edit(ed => { action(ed); return null; });

    private static bool IsSame(Plan a, Plan b)
        => JsonSerializer.Serialize(a, JsonOptions.Default) == JsonSerializer.Serialize(b, JsonOptions.Default);

    private void AfterEdit()
    {
        _services.ReplacePlanInstance(Plan);
        Refresh();
        MarkDirty();
    }

    private void ReplacePlan(Plan plan)
    {
        Plan = plan;
        Editor = new PlanEditor(plan, Game, new AutoLayout(SizeOf));
        _services.ReplacePlanInstance(plan);
    }

    /// <summary>再計算してビューモデルを同期する。ゲームデータの変更後にも呼ぶ。</summary>
    public void Refresh()
    {
        try
        {
            Result = PlanCalculator.Calculate(Plan, Game);
        }
        catch (Exception ex)
        {
            _services.Log.Error("計算に失敗", ex);
            ShowNotice("計算に失敗しました: " + ex.Message);
            Result = new CalculationResult();
        }
        Sync();
        CanUndo = _history.CanUndo;
        CanRedo = _history.CanRedo;
    }

    private void Sync()
    {
        // ---- アイテムノード ----
        var liveIds = Plan.ItemNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _nodeVms.Keys.Where(k => !liveIds.Contains(k)).ToList())
        {
            ItemNodes.Remove(_nodeVms[stale]);
            _nodeVms.Remove(stale);
            SelectedIds.Remove(stale);
        }
        var hidden = HiddenNodeIds();
        foreach (var node in Plan.ItemNodes)
        {
            if (!_nodeVms.TryGetValue(node.Id, out var vm))
            {
                vm = new ItemNodeViewModel(this, node);
                _nodeVms[node.Id] = vm;
                ItemNodes.Add(vm);
            }
            vm.Model = node;
            vm.Update(Result.Node(node.Id), Result.Item(node.ItemId));
            vm.IsVisible = !hidden.Contains(node.Id);
            vm.IsSelected = SelectedIds.Contains(node.Id);
            vm.RefreshHighlight(HoveredItemId);
        }

        // ---- メモ ----
        var liveMemo = Plan.MemoNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _memoVms.Keys.Where(k => !liveMemo.Contains(k)).ToList())
        {
            MemoNodes.Remove(_memoVms[stale]);
            _memoVms.Remove(stale);
            SelectedIds.Remove(stale);
        }
        foreach (var memo in Plan.MemoNodes)
        {
            if (!_memoVms.TryGetValue(memo.Id, out var vm))
            {
                vm = new MemoNodeViewModel(this, memo);
                _memoVms[memo.Id] = vm;
                MemoNodes.Add(vm);
            }
            vm.Model = memo;
            vm.IsSelected = SelectedIds.Contains(memo.Id);
            vm.Refresh();
        }

        // ---- 接続（毎回作り直す。数が少なく単純さを優先） ----
        foreach (var e in Edges) e.Detach();
        Edges.Clear();
        foreach (var edge in Plan.Edges)
        {
            if (_nodeVms.TryGetValue(edge.ParentNodeId, out var p) && _nodeVms.TryGetValue(edge.ChildNodeId, out var c))
                Edges.Add(new EdgeViewModel(this, edge, p, c));
        }
        foreach (var s in SeveredEdges) s.Detach();
        SeveredEdges.Clear();
        foreach (var group in Plan.SeveredEdges.GroupBy(s => s.ParentNodeId))
        {
            if (!_nodeVms.TryGetValue(group.Key, out var p)) continue;
            var i = 0;
            foreach (var s in group)
            {
                var name = Game.FindItem(s.MaterialItemId)?.Name ?? s.MaterialItemId;
                SeveredEdges.Add(new SeveredEdgeViewModel(this, s, p, i++, name));
            }
        }

        // ---- 集計 ----
        Targets.Clear();
        foreach (var t in Result.Targets)
        {
            var title = Game.FindItem(t.ItemId)?.Name ?? t.ItemId;
            Targets.Add(new TargetSummaryViewModel(title, t.RequiredQuantity,
                t.Lines.Select(l => new TargetLineViewModel(Game.FindItem(l.ItemId)?.Name ?? l.ItemId, l.Required, l.OnHand, l.ReferenceShortage, l.IsIntermediate, l.IsUncertain))));
        }
        Totals.Clear();
        foreach (var total in Result.Items.Values.OrderBy(i => Game.FindItem(i.ItemId)?.Name ?? i.ItemId, StringComparer.CurrentCulture))
            Totals.Add(new ItemTotalViewModel(this, total, Game.FindItem(total.ItemId)?.Name ?? total.ItemId));
        TotalShortageCount = Result.Items.Values.Count(i => i.Shortage > 0);
    }

    /// <summary>折りたたまれたノードの子孫（非表示にするノード）。</summary>
    private HashSet<string> HiddenNodeIds()
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in Plan.ItemNodes.Where(n => n.IsCollapsed))
            foreach (var id in Plan.DescendantIds(n.Id).Skip(1)) hidden.Add(id);
        return hidden;
    }

    partial void OnHoveredItemIdChanged(string? value)
    {
        foreach (var vm in _nodeVms.Values) vm.RefreshHighlight(value);
    }

    partial void OnIsDetailedChanged(bool value)
    {
        var mode = value ? DisplayMode.Detailed : DisplayMode.Simple;
        if (Plan.DisplayMode == mode) return;
        Edit(ed => ed.SetDisplayMode(mode));
        foreach (var vm in _nodeVms.Values) vm.Update(Result.Node(vm.Id), Result.Item(vm.ItemId));
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(TabTitle));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(TabTitle));

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

    private void MarkDirty()
    {
        IsDirty = true;
        _autosave.Stop();
        _autosave.Start();
    }

    /// <summary>表示状態（ズーム・位置）は履歴に入れず、保存だけ予約する。</summary>
    public void ViewChanged()
    {
        Plan.View.Zoom = Zoom;
        Plan.View.OffsetX = OffsetX;
        Plan.View.OffsetY = OffsetY;
        _autosave.Stop();
        _autosave.Start();
    }

    public bool SaveNow()
    {
        _autosave.Stop();
        Plan.View.Zoom = Zoom;
        Plan.View.OffsetX = OffsetX;
        Plan.View.OffsetY = OffsetY;
        Plan.Name = Name;
        var ok = _services.SavePlan(Plan);
        SaveFailed = !ok;
        if (ok) IsDirty = false;
        return ok;
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        Name = newName.Trim();
        Plan.Name = Name;
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // Undo / Redo
    // ------------------------------------------------------------------

    [RelayCommand]
    public void Undo()
    {
        if (!_history.CanUndo) return;
        ReplacePlan(_history.Undo(Plan));
        Refresh();
        MarkDirty();
    }

    [RelayCommand]
    public void Redo()
    {
        if (!_history.CanRedo) return;
        ReplacePlan(_history.Redo(Plan));
        Refresh();
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // ノード操作（ビューから呼ぶ）
    // ------------------------------------------------------------------

    public ItemNodeViewModel? AddItemNode(string itemId, double? x, double? y, bool expand, int quantity = 1)
    {
        string? newId = null;
        Edit(ed =>
        {
            var outcome = new EditOutcome();
            var node = ed.AddItemNode(itemId, quantity, x, y, outcome);
            newId = node.Id;
            if (expand) outcome.Merge(ed.Expand(node.Id));
            return outcome;
        });
        if (expand && newId != null)
        {
            var id = newId;
            RunAfterRender(() => Editor.ArrangeSubtree(id));
        }
        return newId != null && _nodeVms.TryGetValue(newId, out var vm) ? vm : null;
    }

    public void AddMemo(double x, double y)
    {
        string? id = null;
        Edit(ed => { id = ed.AddMemoNode(x, y).Id; });
        if (id != null) SelectOnly(id);
    }

    public void SetRequiredQuantity(string nodeId, int qty) => Edit(ed => ed.SetRequiredQuantity(nodeId, qty));
    public void SetInventory(string itemId, int qty) => Edit(ed => ed.SetInventory(itemId, qty));
    public void Expand(string nodeId) => Edit(ed => ed.Expand(nodeId));
    public void ToggleCollapse(string nodeId) => Edit(ed => ed.SetCollapsed(nodeId, !Plan.FindItemNode(nodeId)!.IsCollapsed));
    public void Sever(string edgeId) => Edit(ed => ed.Sever(edgeId));
    public void DeleteEdge(string edgeId) => Edit(ed => ed.DeleteEdge(edgeId, DemandOf));
    public void Reconnect(string parentNodeId, string materialItemId) => Edit(ed => ed.Reconnect(parentNodeId, materialItemId));
    public void Connect(string parentNodeId, string childNodeId) => Edit(ed => ed.Connect(parentNodeId, childNodeId));
    public void Arrange(string nodeId) => Edit(ed => ed.ArrangeSubtree(nodeId));

    /// <summary>すべての目標ツリーを整列する。描画後に実測サイズでもう一度並べ直す（履歴には追加しない）。</summary>
    public void ArrangeAll()
    {
        Edit(ed => ed.ArrangeAll());
        RunAfterRender(() => Editor.ArrangeAll());
    }

    /// <summary>ノードの実測サイズが確定した後に配置を微調整する。Undo の単位は直前の編集に含める。</summary>
    private void RunAfterRender(Action relayout)
    {
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try { relayout(); } catch (Exception ex) { _services.Log.Warn("再配置に失敗: " + ex.Message, ex); return; }
            Refresh();
            MarkDirty();
        }));
    }
    public void SetMemoText(string id, string text) => Edit(ed => ed.SetMemoText(id, text));
    public void SetMemoColor(string id, string key) => Edit(ed => ed.SetMemoColor(id, key));

    public void SeverParentOf(string nodeId)
    {
        var edge = Plan.FindParentEdge(nodeId);
        if (edge != null) Sever(edge.Id);
    }

    public void SetRecipe(string nodeId, string? recipeId) => Edit(ed => ed.SetSelectedRecipe(nodeId, recipeId));

    public void RequestRecipeChange(ItemNodeViewModel node)
    {
        var picked = RecipePicker?.Invoke(node);
        if (picked != null && picked != node.Model.SelectedRecipeId) SetRecipe(node.Id, picked);
    }

    private int DemandOf(string nodeId) => Result.Node(nodeId)?.Demand ?? 0;

    public void DeleteNodes(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        Edit(ed => ed.DeleteNodes(list, DemandOf));
        foreach (var id in list) SelectedIds.Remove(id);
    }

    [RelayCommand]
    public void DeleteSelected() => DeleteNodes(SelectedIds.ToList());

    [RelayCommand]
    public void CopySelected()
    {
        if (SelectedIds.Count == 0) return;
        s_clipboard = (Game.Id, Editor.Copy(SelectedIds, DemandOf));
        ShowNotice($"{SelectedIds.Count} 個のノードをコピーしました");
    }

    [RelayCommand]
    public void Paste()
    {
        if (s_clipboard == null || s_clipboard.Value.Data.IsEmpty) return;
        if (s_clipboard.Value.GameId != Game.Id) { ShowNotice("別のゲームデータの計画からは貼り付けできません。"); return; }
        List<string>? created = null;
        Edit(ed => { created = ed.Paste(s_clipboard.Value.Data, 30, 30); });
        if (created != null) SelectMany(created);
    }

    [RelayCommand]
    public void DuplicateSelected() => DuplicateNodes(SelectedIds.ToList());

    public void DuplicateNodes(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        var data = Editor.Copy(list, DemandOf);
        List<string>? created = null;
        Edit(ed => { created = ed.Paste(data, 30, 30); });
        if (created != null) SelectMany(created);
    }

    // ------------------------------------------------------------------
    // ドラッグ（移動・サイズ変更）: 開始時のスナップショットを終了時に履歴へ記録する
    // ------------------------------------------------------------------

    public void BeginDrag() => _dragSnapshot ??= Plan.Clone();

    public void EndDrag()
    {
        if (_dragSnapshot == null) return;
        var snap = _dragSnapshot;
        _dragSnapshot = null;
        if (IsSame(snap, Plan)) return;
        _history.RecordPrepared(snap);
        AfterEdit();
    }

    // ------------------------------------------------------------------
    // 選択
    // ------------------------------------------------------------------

    public void SelectOnly(string id)
    {
        SelectedIds.Clear();
        SelectedIds.Add(id);
        ApplySelection();
    }

    public void ToggleSelect(string id)
    {
        if (!SelectedIds.Remove(id)) SelectedIds.Add(id);
        ApplySelection();
    }

    public void SelectMany(IEnumerable<string> ids)
    {
        SelectedIds.Clear();
        foreach (var id in ids) SelectedIds.Add(id);
        ApplySelection();
    }

    public void ClearSelection()
    {
        if (SelectedIds.Count == 0) return;
        SelectedIds.Clear();
        ApplySelection();
    }

    private void ApplySelection()
    {
        foreach (var vm in _nodeVms.Values) vm.IsSelected = SelectedIds.Contains(vm.Id);
        foreach (var vm in _memoVms.Values) vm.IsSelected = SelectedIds.Contains(vm.Id);
    }

    public IEnumerable<ItemNodeViewModel> SelectedItemNodes => _nodeVms.Values.Where(v => v.IsSelected);
    public IEnumerable<MemoNodeViewModel> SelectedMemoNodes => _memoVms.Values.Where(v => v.IsSelected);

    // ------------------------------------------------------------------
    // 表示
    // ------------------------------------------------------------------

    /// <summary>全ノードが収まるようにズームと位置を決める。履歴には入れない。</summary>
    public void FitAll(double viewportWidth, double viewportHeight)
    {
        var layout = new AutoLayout(SizeOf);
        var bounds = layout.AllBounds(Plan).Select(b => b.Bounds).ToList();
        if (bounds.Count == 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            Zoom = 1; OffsetX = 0; OffsetY = 0;
            ViewChanged();
            return;
        }
        var minX = bounds.Min(b => b.X) - 40;
        var minY = bounds.Min(b => b.Y) - 40;
        var maxX = bounds.Max(b => b.Right) + 40;
        var maxY = bounds.Max(b => b.Bottom) + 40;
        var zoom = Math.Min(viewportWidth / (maxX - minX), viewportHeight / (maxY - minY));
        Zoom = Math.Clamp(zoom, 0.1, 2.0);
        OffsetX = (viewportWidth - (maxX - minX) * Zoom) / 2 - minX * Zoom;
        OffsetY = (viewportHeight - (maxY - minY) * Zoom) / 2 - minY * Zoom;
        ViewChanged();
    }

    public void ShowNotice(string text)
    {
        Notice = text;
        NoticeRaised?.Invoke(text);
    }

    public event Action<string>? NoticeRaised;

    public void Close()
    {
        _autosave.Stop();
        foreach (var e in Edges) e.Detach();
        foreach (var s in SeveredEdges) s.Detach();
    }
}
