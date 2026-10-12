using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CraftPlanner.App.Services;
using CraftPlanner.App.ViewModels;
using CraftPlanner.App.Views;
using CraftPlanner.Core.Data;
using CraftPlanner.Core.ImportExport;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Storage;
using Microsoft.Win32;

namespace CraftPlanner.App;

public partial class MainWindow : Window
{
    public const string ItemDragFormat = "CraftPlanner.ItemId";
    public static readonly RoutedCommand DuplicateCommand = new("Duplicate", typeof(MainWindow));

    private readonly MainViewModel _vm;
    private AppServices Services => _vm.Services;
    private Point _dragStartPoint;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(App.Services);
        DataContext = _vm;
        var s = Services.Settings;
        if (s.WindowWidth is > 300 && s.WindowHeight is > 200) { Width = s.WindowWidth.Value; Height = s.WindowHeight.Value; }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.RestoreTabs();
        if (Services.StartupFailures.Count > 0)
        {
            var lines = Services.StartupFailures.Select(f => $"{f.FilePath}\n  原因: {f.Reason}\n  対処: {f.Recovery}");
            ListMessageDialog.Show(this, "読み込めなかったファイルがあります",
                "次のファイルは読み込めませんでした。ファイルは変更していません。「バックアップと復元」から直前の世代に戻すか、ファイルを確認してください。",
                lines, "バックアップと復元を開く", () => BackupDialog.Show(this, _vm));
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = Services.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; }
        _vm.PersistTabs();

        var failed = _vm.OpenPlans.Where(p => p.IsDirty && !p.SaveNow()).ToList();
        if (failed.Count > 0)
        {
            var r = MessageBox.Show(this,
                "次の計画を保存できませんでした:\n" + string.Join("\n", failed.Select(p => "・" + p.Name)) +
                "\n\n保存されていない変更が失われます。それでも終了しますか？\n（「いいえ」で戻り、「ファイル > データフォルダーを開く」で状態を確認できます）",
                "保存に失敗", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
    }

    // ------------------------------------------------------------------
    // コマンド（ショートカット）
    // ------------------------------------------------------------------

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_vm.SaveAll()) _vm.StatusText = $"すべて保存しました（{DateTime.Now:HH:mm:ss}）";
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e) => _vm.ActivePlan?.Undo();
    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e) => _vm.ActivePlan?.Redo();
    private void Copy_Executed(object sender, ExecutedRoutedEventArgs e) => _vm.ActivePlan?.CopySelected();
    private void Paste_Executed(object sender, ExecutedRoutedEventArgs e) => _vm.ActivePlan?.Paste();
    private void Duplicate_Executed(object sender, ExecutedRoutedEventArgs e) => _vm.ActivePlan?.DuplicateSelected();
    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => _vm.ActivePlan?.DeleteSelected();

    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (ItemsColumn.Width.Value < 10) ToggleItems_Click(sender, e);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void FitAll_Click(object sender, RoutedEventArgs e) => FindCanvasView()?.FitAll();

    private PlanCanvasView? FindCanvasView()
    {
        var presenter = PlanTabs.Template.FindName("PART_SelectedContentHost", PlanTabs) as ContentPresenter;
        if (presenter == null) return null;
        presenter.ApplyTemplate();
        return presenter.ContentTemplate?.FindName("Canvas", presenter) as PlanCanvasView;
    }

    private void ToggleItems_Click(object sender, RoutedEventArgs e)
    {
        var show = ItemsColumn.Width.Value < 10;
        ItemsColumn.Width = new GridLength(show ? 320 : 0);
        ShowItemsMenu.IsChecked = show;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Services.Paths.Root) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "フォルダーを開けませんでした: " + ex.Message); }
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "docs", "USER_GUIDE.md"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "USER_GUIDE.md")),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
        {
            MessageBox.Show(this, "USER_GUIDE.md が見つかりません。配布フォルダーの docs フォルダーを確認してください。\n\nデータ保存先: " + Services.Paths.Root, "操作方法");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "ファイルを開けませんでした: " + ex.Message); }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "CraftPlanner 0.1\nゲーム素材・レシピ管理アプリ\n\n" +
            $"データ保存先: {Services.Paths.Root}\nバックアップ世代数: {Services.Settings.BackupGenerations}\n\n" +
            "外部通信は行いません。アプリ専用フォルダー以外のファイルは、明示的に選択したものだけを読み書きします。",
            "バージョン情報");
    }

    private void Backups_Click(object sender, RoutedEventArgs e) => BackupDialog.Show(this, _vm);

    // ------------------------------------------------------------------
    // ゲームデータ
    // ------------------------------------------------------------------

    private void ManageGames_Click(object sender, RoutedEventArgs e) => GameDataDialog.Show(this, _vm);

    private GameData? RequireGame()
    {
        if (_vm.SelectedGame != null) return _vm.SelectedGame;
        MessageBox.Show(this, "ゲームデータが選択されていません。", "CraftPlanner");
        return null;
    }

    private void ExportGameJson_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var dlg = new SaveFileDialog { Filter = "JSON ファイル (*.json)|*.json", FileName = SafeName(game.Name) + ".json", Title = "ゲームデータを JSON に書き出し" };
        if (dlg.ShowDialog(this) != true) return;
        Run(() => { JsonTransfer.ExportGameData(game, dlg.FileName); _vm.StatusText = "書き出しました: " + dlg.FileName; });
    }

    private void ImportGameJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON ファイル (*.json)|*.json", Title = "ゲームデータ JSON を選択" };
        if (dlg.ShowDialog(this) != true) return;
        GameData incoming;
        try { incoming = JsonTransfer.ReadGameDataFile(dlg.FileName); }
        catch (LoadException ex) { MessageBox.Show(this, ex.Failure.Reason + "\n\n" + ex.Failure.Recovery, "取り込みできません", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var choice = MessageBox.Show(this,
            $"「{incoming.Name}」（アイテム {incoming.Items.Count} 件）を読み込みました。\n\n" +
            "[はい] 新しいゲームデータとして作成する\n[いいえ] 現在選択中のゲームデータへ統合する（ID 競合を確認します）",
            "取り込み先の選択", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.Yes)
        {
            var name = InputDialog.Show(this, "新しいゲームデータ", "ゲームデータの名前", incoming.Name);
            if (name == null) return;
            Run(() =>
            {
                var g = Services.RegisterImportedGame(incoming, name);
                _vm.RefreshGames();
                _vm.SelectedGame = _vm.Games.First(x => x.Id == g.Id);
                _vm.StatusText = $"ゲームデータ「{g.Name}」を作成しました";
            });
            return;
        }
        var target = RequireGame();
        if (target == null) return;
        MergeInto(target, incoming.Items.Select((i, idx) => (idx + 1, i)).ToList(), incomingHasRecipes: true, "JSON の統合");
    }

    private void MergeInto(GameData target, List<(int Row, Item Item)> incoming, bool incomingHasRecipes, string title, IReadOnlySet<string>? presentFields = null)
    {
        var preview = MergePlanner.Preview(target, incoming, incomingHasRecipes, presentFields);
        if (!MergePreviewDialog.Confirm(this, preview, title)) return;
        Run(() =>
        {
            var merged = MergePlanner.Apply(target, preview);
            Services.ReplaceGame(merged);     // 保存に成功した場合だけ差し替わる
            _vm.GameReplaced(merged);
            var applied = preview.Entries.Count(x => x.Decision != MergeDecision.Skip && x.Kind != MergeEntryKind.Identical);
            _vm.StatusText = $"{applied} 件を取り込みました（{DateTime.Now:HH:mm:ss}）";
        });
    }

    private void ExportItemCsv_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var dlg = new SaveFileDialog { Filter = "CSV ファイル (*.csv)|*.csv", FileName = SafeName(game.Name) + "_items.csv", Title = "アイテム CSV に書き出し（アイコンは含まれません）" };
        if (dlg.ShowDialog(this) != true) return;
        Run(() =>
        {
            File.WriteAllText(dlg.FileName, ItemCsv.Export(game.Items), Csv.FileEncoding);
            _vm.StatusText = "書き出しました: " + dlg.FileName + "（アイコンを含む完全な移行には JSON を使ってください）";
        });
    }

    private void ExportRecipeCsv_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var dlg = new SaveFileDialog { Filter = "CSV ファイル (*.csv)|*.csv", FileName = SafeName(game.Name) + "_recipes.csv", Title = "レシピ CSV に書き出し" };
        if (dlg.ShowDialog(this) != true) return;
        Run(() => { File.WriteAllText(dlg.FileName, RecipeCsv.Export(game), Csv.FileEncoding); _vm.StatusText = "書き出しました: " + dlg.FileName; });
    }

    private void ImportItemCsv_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var dlg = new OpenFileDialog { Filter = "CSV ファイル (*.csv)|*.csv", Title = "アイテム CSV を選択" };
        if (dlg.ShowDialog(this) != true) return;
        CsvReadResult read;
        try { read = ItemCsv.Read(File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8)); }
        catch (Exception ex) { MessageBox.Show(this, "CSV を読み込めませんでした: " + ex.Message, "取り込みできません", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (read.Errors.Count > 0)
        {
            ListMessageDialog.Show(this, "CSV の形式に問題があります", "取り込みを中止しました。CSV を修正してください。", read.Errors);
            return;
        }
        MergeInto(game, read.Items, incomingHasRecipes: false, "アイテム CSV の取り込み（レシピは変更されません）", read.PresentFields);
    }

    // ------------------------------------------------------------------
    // 計画
    // ------------------------------------------------------------------

    private void NewPlan_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var name = InputDialog.Show(this, "新しい制作計画", "計画の名前（例: 武器制作、ボス攻略準備）", "新しい計画");
        if (name == null) return;
        Run(() => { var plan = Services.CreatePlan(game, name); _vm.OpenPlan(plan); });
    }

    private void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        if (RequireGame() == null) return;
        var plan = PlanListDialog.Show(this, _vm);
        if (plan != null) _vm.OpenPlan(plan);
    }

    private void RenamePlan_Click(object sender, RoutedEventArgs e)
    {
        var active = _vm.ActivePlan;
        if (active == null) return;
        var name = InputDialog.Show(this, "計画の名前を変更", "新しい名前", active.Name);
        if (name != null) active.Rename(name);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActivePlan != null) CloseTabWithConfirm(_vm.ActivePlan);
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanViewModel vm }) CloseTabWithConfirm(vm);
    }

    private void CloseTabWithConfirm(PlanViewModel vm)
    {
        var r = MessageBox.Show(this, $"タブ「{vm.Name}」を閉じますか？\n（計画は保存され、削除はされません。「計画を開く」からいつでも開けます）",
            "タブを閉じる", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        if (!_vm.ClosePlan(vm))
            MessageBox.Show(this, "保存に失敗したためタブを閉じませんでした。ステータスバーのエラーを確認してください。", "保存に失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        var active = _vm.ActivePlan;
        if (active == null) return;
        var r = MessageBox.Show(this, $"計画「{active.Name}」を削除しますか？\nこの操作はタブを閉じるのとは異なり、計画ファイルを削除します（削除前にバックアップを 1 世代残します）。",
            "計画の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        Run(() =>
        {
            active.Close();
            _vm.OpenPlans.Remove(active);
            Services.DeletePlan(active.Plan);
            _vm.ActivePlan = _vm.OpenPlans.FirstOrDefault();
            _vm.PersistTabs();
            _vm.StatusText = $"計画「{active.Name}」を削除しました";
        });
    }

    private void ExportPlanJson_Click(object sender, RoutedEventArgs e)
    {
        var active = _vm.ActivePlan;
        if (active == null) { MessageBox.Show(this, "書き出す計画のタブを選択してください。"); return; }
        var dlg = new SaveFileDialog { Filter = "JSON ファイル (*.json)|*.json", FileName = SafeName(active.Name) + ".json", Title = "制作計画を JSON に書き出し" };
        if (dlg.ShowDialog(this) != true) return;
        Run(() => { active.SaveNow(); JsonTransfer.ExportPlan(active.Plan, dlg.FileName); _vm.StatusText = "書き出しました: " + dlg.FileName; });
    }

    private void ImportPlanJson_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var dlg = new OpenFileDialog { Filter = "JSON ファイル (*.json)|*.json", Title = "制作計画 JSON を選択" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var plan = JsonTransfer.ReadPlanFile(dlg.FileName, game.Id);
            var registered = Services.RegisterImportedPlan(plan);
            _vm.OpenPlan(registered);
            _vm.StatusText = $"計画「{registered.Name}」を取り込みました";
        }
        catch (PlanGameMismatchException ex)
        {
            var other = Services.LoadedGames.FirstOrDefault(g => g.Id == ex.PlanGameDataId);
            var hint = other != null ? $"\n\n参照元のゲームデータ「{other.Name}」を選択してからもう一度取り込んでください。" : "\n\n参照元のゲームデータがこのアプリに存在しません。先にそのゲームデータの JSON を取り込んでください。";
            MessageBox.Show(this, ex.Message + hint, "取り込みできません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (LoadException ex)
        {
            MessageBox.Show(this, ex.Failure.Reason + "\n\n" + ex.Failure.Recovery, "取り込みできません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    // ------------------------------------------------------------------
    // アイテム一覧
    // ------------------------------------------------------------------

    private Item? SelectedItem => _vm.ItemList.SelectedCard?.Item;

    private void NewItem_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        var created = ItemEditorDialog.CreateNew(this, _vm, game, _vm.ItemList.SearchText);
        if (created != null) _vm.ItemList.SelectedCard = _vm.ItemList.Cards.FirstOrDefault(c => c.Id == created.Id);
    }

    private void EditItem_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null) return;
        if (SelectedItem == null) { MessageBox.Show(this, "編集するアイテムを選択してください。"); return; }
        ItemEditorDialog.EditExisting(this, _vm, game, SelectedItem);
    }

    private void ItemListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedItem != null) EditItem_Click(sender, e);
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var game = RequireGame();
        if (game == null || SelectedItem == null) { if (game != null) MessageBox.Show(this, "削除するアイテムを選択してください。"); return; }
        var item = SelectedItem;
        var svc = new GameDataService(game);
        var refs = svc.FindReferences(item.Id, Services.LoadedPlans);
        if (refs.Count > 0)
        {
            ListMessageDialog.Show(this, $"「{item.Name}」は削除できません",
                $"次の {refs.Count} 箇所から参照されています。レシピの素材から外す、または計画のノードを削除してから、もう一度お試しください。",
                refs.Select(r => r.Description));
            return;
        }
        var r = MessageBox.Show(this, $"アイテム「{item.Name}」（ID: {item.Id}）を削除しますか？\nこのアイテムのレシピ {item.Recipes.Count} 件も削除されます。",
            "アイテムの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        Run(() => { svc.DeleteItem(item.Id, Services.LoadedPlans); _vm.GameDataChanged(game); });
    }

    private void AddToCanvas_Click(object sender, RoutedEventArgs e) => AddSelectedToCanvas(expand: false);
    private void AddTreeToCanvas_Click(object sender, RoutedEventArgs e) => AddSelectedToCanvas(expand: true);

    private void AddSelectedToCanvas(bool expand)
    {
        var item = SelectedItem;
        if (item == null) { MessageBox.Show(this, "アイテムを選択してください。"); return; }
        var plan = _vm.ActivePlan;
        if (plan == null) { MessageBox.Show(this, "追加先の制作計画のタブを開いてください（「計画」メニュー）。"); return; }
        if (plan.Game.Id != _vm.SelectedGame?.Id) { MessageBox.Show(this, "アクティブな計画は別のゲームデータを参照しています。"); return; }
        var node = plan.AddItemNode(item.Id, null, null, expand);
        if (node != null) plan.SelectOnly(node.Id);
    }

    private void ItemListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStartPoint = e.GetPosition(null);

    private void ItemListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedItem == null) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) == null) return;
        var data = new DataObject(ItemDragFormat, SelectedItem.Id);
        DragDrop.DoDragDrop(ItemListBox, data, DragDropEffects.Copy);
    }

    // ------------------------------------------------------------------
    // タブの並び替え（ドラッグ）・中クリックで閉じる
    // ------------------------------------------------------------------

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is TabItem { DataContext: PlanViewModel vm }) CloseTabWithConfirm(vm);
    }

    private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not TabItem tab || tab.DataContext is not PlanViewModel vm) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        if (!tab.IsMouseOver) return;
        DragDrop.DoDragDrop(tab, new DataObject("CraftPlanner.Tab", vm), DragDropEffects.Move);
    }

    private void TabItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not TabItem { DataContext: PlanViewModel target } || e.Data.GetData("CraftPlanner.Tab") is not PlanViewModel source || source == target) return;
        var from = _vm.OpenPlans.IndexOf(source);
        var to = _vm.OpenPlans.IndexOf(target);
        if (from < 0 || to < 0) return;
        _vm.OpenPlans.Move(from, to);
        _vm.ActivePlan = source;
        _vm.PersistTabs();
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Services.Log.Error("操作に失敗", ex);
        MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "export" : s;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
