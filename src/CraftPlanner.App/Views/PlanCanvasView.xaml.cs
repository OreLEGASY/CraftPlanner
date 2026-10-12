using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CraftPlanner.App.ViewModels;

namespace CraftPlanner.App.Views;

/// <summary>
/// キャンバスのマウス操作（ズーム・パン・ノード移動・接続ドラッグ・ドロップ）を担当する。
/// 状態の変更はすべて PlanViewModel を通して行い、ここでは座標計算とイベント処理だけを行う。
/// </summary>
public partial class PlanCanvasView : UserControl
{
    private PlanViewModel? Vm => DataContext as PlanViewModel;

    // パン
    private bool _panning;
    private Point _panStart;
    private double _panOffsetX, _panOffsetY;

    // ノード移動
    private bool _draggingNodes;
    private Point _dragStartWorld;
    private readonly Dictionary<object, (double X, double Y)> _dragOrigins = new();

    // 接続ドラッグ
    private ItemNodeViewModel? _linkSource;

    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private PlanViewModel? _subscribed;

    public PlanCanvasView()
    {
        InitializeComponent();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); NoticeBar.Visibility = Visibility.Collapsed; };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed != null) _subscribed.NoticeRaised -= ShowNotice;
        _subscribed = Vm;
        if (Vm == null) return;
        Vm.NoticeRaised += ShowNotice;
        Vm.RecipePicker = node => RecipePickerDialog.Pick(Window.GetWindow(this), Vm, node);
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ------------------------------------------------------------------
    // 座標変換
    // ------------------------------------------------------------------

    private Point ToWorld(Point viewportPoint)
    {
        var vm = Vm!;
        return new Point((viewportPoint.X - vm.OffsetX) / vm.Zoom, (viewportPoint.Y - vm.OffsetY) / vm.Zoom);
    }

    private Point ViewportCenterWorld() => ToWorld(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    // ------------------------------------------------------------------
    // ズーム・パン
    // ------------------------------------------------------------------

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null) return;
        var p = e.GetPosition(Viewport);
        var oldZoom = Vm.Zoom;
        var newZoom = Math.Clamp(oldZoom * Math.Pow(1.1, e.Delta / 120.0), 0.1, 3.0);
        // カーソル位置のワールド座標が動かないようにオフセットを調整する
        Vm.OffsetX = p.X - (p.X - Vm.OffsetX) * (newZoom / oldZoom);
        Vm.OffsetY = p.Y - (p.Y - Vm.OffsetY) * (newZoom / oldZoom);
        Vm.Zoom = newZoom;
        Vm.ViewChanged();
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        Viewport.Focus();
        // ノード側で処理済みならここには来ない（e.Handled）。空白部分のクリック＝選択解除＋パン開始
        Vm.ClearSelection();
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panOffsetX = Vm.OffsetX;
        _panOffsetY = Vm.OffsetY;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _lastRightClickWorld = ToWorld(e.GetPosition(Viewport));
    }

    private Point _lastRightClickWorld;

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (Vm == null) return;
        var p = e.GetPosition(Viewport);
        if (_panning)
        {
            Vm.OffsetX = _panOffsetX + (p.X - _panStart.X);
            Vm.OffsetY = _panOffsetY + (p.Y - _panStart.Y);
        }
        else if (_draggingNodes)
        {
            var w = ToWorld(p);
            var dx = w.X - _dragStartWorld.X;
            var dy = w.Y - _dragStartWorld.Y;
            foreach (var kv in _dragOrigins)
            {
                switch (kv.Key)
                {
                    case ItemNodeViewModel n: n.X = Math.Round(kv.Value.X + dx); n.Y = Math.Round(kv.Value.Y + dy); break;
                    case MemoNodeViewModel m: m.X = Math.Round(kv.Value.X + dx); m.Y = Math.Round(kv.Value.Y + dy); break;
                }
            }
        }
        else if (_linkSource != null)
        {
            var w = ToWorld(p);
            var x1 = _linkSource.X + _linkSource.ActualWidth;
            var y1 = _linkSource.Y + _linkSource.ActualHeight / 2;
            TempLink.Data = Geometry.Parse(EdgeViewModel.OrthogonalPath(x1, y1, w.X, w.Y));
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        if (_panning)
        {
            _panning = false;
            Viewport.ReleaseMouseCapture();
            Vm.ViewChanged();
        }
        if (_draggingNodes)
        {
            _draggingNodes = false;
            _dragOrigins.Clear();
            Viewport.ReleaseMouseCapture();
            Vm.EndDrag();
        }
        if (_linkSource != null)
        {
            var source = _linkSource;
            _linkSource = null;
            TempLink.Visibility = Visibility.Collapsed;
            Viewport.ReleaseMouseCapture();
            var target = HitTestItemNode(e.GetPosition(World));
            if (target != null && target != source) Vm.Connect(source.Id, target.Id);
        }
    }

    private ItemNodeViewModel? HitTestItemNode(Point worldPoint)
    {
        ItemNodeViewModel? found = null;
        VisualTreeHelper.HitTest(World, null, r =>
        {
            var d = r.VisualHit;
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext is ItemNodeViewModel vm) { found = vm; return HitTestResultBehavior.Stop; }
                d = VisualTreeHelper.GetParent(d);
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(worldPoint));
        return found;
    }

    // ------------------------------------------------------------------
    // ノードの選択とドラッグ
    // ------------------------------------------------------------------

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null || sender is not FrameworkElement fe) return;
        if (IsInteractive(e.OriginalSource as DependencyObject, fe)) return; // テキストボックスやボタンの操作は邪魔しない

        var id = fe.DataContext switch { ItemNodeViewModel n => n.Id, MemoNodeViewModel m => m.Id, _ => null };
        if (id == null) return;
        Viewport.Focus();

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl) Vm.ToggleSelect(id);
        else if (!Vm.SelectedIds.Contains(id)) Vm.SelectOnly(id);

        if (!Vm.SelectedIds.Contains(id)) { e.Handled = true; return; }

        _draggingNodes = true;
        _dragStartWorld = ToWorld(e.GetPosition(Viewport));
        _dragOrigins.Clear();
        foreach (var n in Vm.SelectedItemNodes) _dragOrigins[n] = (n.X, n.Y);
        foreach (var m in Vm.SelectedMemoNodes) _dragOrigins[m] = (m.X, m.Y);
        Vm.BeginDrag();
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>クリック元が入力コントロール（TextBox / Button / Thumb など）か。</summary>
    private static bool IsInteractive(DependencyObject? source, DependencyObject stopAt)
    {
        while (source != null && source != stopAt)
        {
            if (source is TextBoxBase || source is ButtonBase || source is Thumb || source is ComboBox || source is ScrollBar) return true;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Node_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Vm != null && sender is FrameworkElement { DataContext: ItemNodeViewModel vm }) Vm.HoveredItemId = vm.ItemId;
    }

    private void Node_MouseLeave(object sender, MouseEventArgs e)
    {
        if (Vm != null) Vm.HoveredItemId = null;
    }

    private void Node_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemNodeViewModel vm })
        {
            vm.ActualWidth = e.NewSize.Width;
            vm.ActualHeight = e.NewSize.Height;
        }
    }

    private void Port_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null || sender is not FrameworkElement { DataContext: ItemNodeViewModel vm }) return;
        _linkSource = vm;
        TempLink.Visibility = Visibility.Visible;
        TempLink.Data = Geometry.Empty;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // メモのサイズ変更
    // ------------------------------------------------------------------

    private void MemoResize_DragStarted(object sender, DragStartedEventArgs e) => Vm?.BeginDrag();

    private void MemoResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemoNodeViewModel memo })
        {
            memo.Width += e.HorizontalChange;
            memo.Height += e.VerticalChange;
        }
    }

    private void MemoResize_DragCompleted(object sender, DragCompletedEventArgs e) => Vm?.EndDrag();

    // ------------------------------------------------------------------
    // ドロップ・キー
    // ------------------------------------------------------------------

    private void Viewport_Drop(object sender, DragEventArgs e)
    {
        if (Vm == null) return;
        if (e.Data.GetData(MainWindow.ItemDragFormat) is string itemId)
        {
            var w = ToWorld(e.GetPosition(Viewport));
            var node = Vm.AddItemNode(itemId, w.X, w.Y, expand: false);
            if (node != null) Vm.SelectOnly(node.Id);
        }
    }

    private void Viewport_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.Key == Key.Delete) { Vm.DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm.ClearSelection(); e.Handled = true; }
    }

    // ------------------------------------------------------------------
    // ツールバー・コンテキストメニュー
    // ------------------------------------------------------------------

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void AddItemAt(Point? world)
    {
        if (Vm == null || Main == null) return;
        var picked = ItemPickerDialog.PickWithMode(Window.GetWindow(this), Main, Vm.Game, "キャンバスに追加するアイテムを選択");
        if (picked == null) return;
        var node = Vm.AddItemNode(picked.Value.Item.Id, world?.X, world?.Y, picked.Value.Expand);
        if (node != null) Vm.SelectOnly(node.Id);
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItemAt(null);
    private void AddItemHere_Click(object sender, RoutedEventArgs e) => AddItemAt(_lastRightClickWorld);

    private void AddMemo_Click(object sender, RoutedEventArgs e)
    {
        var c = ViewportCenterWorld();
        Vm?.AddMemo(Math.Round(c.X - 100), Math.Round(c.Y - 60));
    }

    private void AddMemoHere_Click(object sender, RoutedEventArgs e) => Vm?.AddMemo(Math.Round(_lastRightClickWorld.X), Math.Round(_lastRightClickWorld.Y));

    private void ArrangeAll_Click(object sender, RoutedEventArgs e) => Vm?.ArrangeAll();

    private void FitAll_Click(object sender, RoutedEventArgs e) => Vm?.FitAll(Viewport.ActualWidth, Viewport.ActualHeight);

    public void FitAll() => Vm?.FitAll(Viewport.ActualWidth, Viewport.ActualHeight);
}
