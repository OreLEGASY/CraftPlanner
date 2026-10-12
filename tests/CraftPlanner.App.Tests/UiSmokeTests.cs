using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CraftPlanner.App;
using CraftPlanner.App.Services;
using CraftPlanner.App.ViewModels;
using CraftPlanner.App.Views;
using CraftPlanner.Core.Data;
using CraftPlanner.Core.ImportExport;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Storage;

namespace CraftPlanner.App.Tests;

/// <summary>
/// WPF の画面を実際に生成して、XAML の読み込み・バインディング・主要操作が例外なく動くことを確認する。
/// スクリーンショットを CRAFTPLANNER_SHOT_DIR（未設定なら %TEMP%\CraftPlannerUiTest\shots）へ保存する。
/// </summary>
public class UiSmokeTests
{
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) Messages.Add(message); }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Xunit.Sdk.XunitException("UI スレッドで例外: " + error);
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.Now + duration;
        while (DateTime.Now < until) { DoEvents(); Thread.Sleep(20); }
    }

    private static string ShotDir()
    {
        var dir = Environment.GetEnvironmentVariable("CRAFTPLANNER_SHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Path.GetTempPath(), "CraftPlannerUiTest", "shots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Screenshot(Window window, string name)
    {
        window.UpdateLayout();
        DoEvents();
        var w = (int)Math.Ceiling(window.ActualWidth);
        var h = (int)Math.Ceiling(window.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render((Visual)window.Content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(ShotDir(), name));
        enc.Save(fs);
    }

    private static (AppServices Services, GameData Game, Plan Plan) Seed()
    {
        var root = Path.Combine(Path.GetTempPath(), "CraftPlannerUiTest", Guid.NewGuid().ToString("N"));
        var services = new AppServices(new AppPaths(root));
        services.LoadAll();
        var game = services.LoadedGames.Single();
        game.Name = "サンプルRPG";
        var svc = new GameDataService(game);
        var sword = svc.CreateItem("鉄の剣", "sword");
        svc.UpdateItem(sword.Id, "鉄の剣", "序盤の定番武器", "攻撃力 12。鍛冶屋で作れる。", "鍛冶屋で制作", null);
        var ingot = svc.CreateItem("鉄インゴット", "ingot");
        svc.UpdateItem(ingot.Id, "鉄インゴット", "鉄の原石を精錬したもの", "", "炉で精錬", null);
        var ore = svc.CreateItem("鉄の原石", "ore");
        svc.UpdateItem(ore.Id, "鉄の原石", "鉱山で採れる", "", "北の鉱山", null);
        var coal = svc.CreateItem("石炭", "coal");
        var hilt = svc.CreateItem("柄", "hilt");
        var wood = svc.CreateItem("木材", "wood");
        var r1 = svc.AddRecipe(sword.Id, "鍛冶", 1);
        svc.UpdateRecipe(r1.Id, "鍛冶", 1, new[] { new RecipeMaterial { ItemId = ingot.Id, Quantity = 3 }, new RecipeMaterial { ItemId = hilt.Id, Quantity = 1 } });
        var r2 = svc.AddRecipe(ingot.Id, "精錬", 3);
        svc.UpdateRecipe(r2.Id, "精錬", 3, new[] { new RecipeMaterial { ItemId = ore.Id, Quantity = 2 }, new RecipeMaterial { ItemId = coal.Id, Quantity = 1 } });
        var r3 = svc.AddRecipe(ingot.Id, "簡易精錬", 1);
        svc.UpdateRecipe(r3.Id, "簡易精錬", 1, new[] { new RecipeMaterial { ItemId = ore.Id, Quantity = 1 } });
        var r4 = svc.AddRecipe(hilt.Id, "", 1);
        svc.UpdateRecipe(r4.Id, "", 1, new[] { new RecipeMaterial { ItemId = wood.Id, Quantity = 2 } });
        svc.AddRecipe(wood.Id, "採取", 1);
        Assert.True(services.SaveGame(game));
        var plan = services.CreatePlan(game, "武器制作");
        return (services, game, plan);
    }

    private static Application EnsureApp()
    {
        if (Application.Current != null) return Application.Current;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return app;
    }

    [Fact]
    public void メインウィンドウと計画キャンバスの表示_操作_保存()
    {
        RunSta(() =>
        {
            var (services, game, plan) = Seed();
            App.Services = services;
            EnsureApp();

            var listener = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;

            var window = new MainWindow { Width = 1400, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Show();
            DoEvents();
            var main = (MainViewModel)window.DataContext;
            Assert.Equal(game.Id, main.SelectedGame!.Id);
            Assert.Equal(6, main.ItemList.Cards.Count);
            Screenshot(window, "01-empty.png");

            // 計画を開いて素材ツリーを展開
            var pvm = main.OpenPlan(plan)!;
            DoEvents();
            var node = pvm.AddItemNode("sword", 60, 80, expand: true, quantity: 2);
            Assert.NotNull(node);
            DoEvents();
            Assert.Equal(6, pvm.ItemNodes.Count);
            Assert.Equal(5, pvm.Edges.Count);
            pvm.SetInventory("ingot", 2);
            pvm.AddItemNode("ingot", 60, 520, expand: false, quantity: 5);
            pvm.AddMemo(700, 520);
            DoEvents();
            Pump(TimeSpan.FromMilliseconds(300));
            Screenshot(window, "02-canvas-detailed.png");

            // 企画書 7.3 の例: 剣 2 本分 6 個 + 独立目標 5 個 = 11、所持 2 → 不足 9（独立目標側は末端）
            var ingotTotal = pvm.Result.Item("ingot")!;
            Assert.Equal(11, ingotTotal.Gross);
            Assert.Equal(2, ingotTotal.OnHand);

            // シンプル表示
            pvm.IsDetailed = false;
            DoEvents();
            Screenshot(window, "03-canvas-simple.png");
            pvm.IsDetailed = true;
            DoEvents();

            // Undo / Redo
            var countBefore = pvm.ItemNodes.Count;
            pvm.SelectOnly(pvm.ItemNodes.Last().Id);
            pvm.DeleteSelected();
            DoEvents();
            Assert.Equal(countBefore - 1, pvm.ItemNodes.Count);
            pvm.Undo();
            DoEvents();
            Assert.Equal(countBefore, pvm.ItemNodes.Count);
            pvm.Redo();
            DoEvents();
            Assert.Equal(countBefore - 1, pvm.ItemNodes.Count);
            pvm.Undo();
            DoEvents();

            // 切断と再接続
            var edge = pvm.Edges.First(e => e.MaterialItemId == "hilt");
            pvm.Sever(edge.Id);
            DoEvents();
            Assert.Single(pvm.SeveredEdges);
            Screenshot(window, "04-severed.png");
            pvm.Reconnect(pvm.SeveredEdges[0].ParentNodeId, "hilt");
            DoEvents();
            Assert.Empty(pvm.SeveredEdges);

            // 折りたたみ
            pvm.ToggleCollapse(node!.Id);
            DoEvents();
            Assert.True(pvm.ItemNodes.Count(n => !n.IsVisible) >= 4);
            pvm.ToggleCollapse(node.Id);
            DoEvents();

            // 全体表示・自動保存
            pvm.ArrangeAll();
            Pump(TimeSpan.FromMilliseconds(400));
            Screenshot(window, "05a-arranged.png");
            pvm.FitAll(900, 600);
            Pump(TimeSpan.FromSeconds(2.2));
            Assert.False(pvm.IsDirty);
            var saved = services.Plans.Load(plan.Id);
            Assert.Equal(pvm.Plan.ItemNodes.Count, saved.ItemNodes.Count);
            Assert.Equal(2, saved.Inventory["ingot"]);
            Screenshot(window, "05-fit.png");

            // 各ダイアログの XAML が読み込めること（表示はせず生成のみ／一部はスクリーンショット）
            var editor = Construct<ItemEditorDialog>(main, game, game.FindItem("sword"), null);
            editor.Show(); DoEvents(); Screenshot(editor, "06-item-editor.png"); editor.Close();
            var picker = Construct<ItemPickerDialog>(main, game, null);
            picker.ModePanel.Visibility = Visibility.Visible;
            picker.Show(); DoEvents(); Screenshot(picker, "07-item-picker.png"); picker.Close();
            var rp = Construct<RecipePickerDialog>(pvm, pvm.ItemNodes.First(n => n.ItemId == "ingot"));
            rp.Show(); DoEvents(); Screenshot(rp, "08-recipe-picker.png"); rp.Close();
            var preview = MergePlanner.Preview(game, ItemCsv.Read("id,name,summary\nsword,鉄の剣,変更後の概要\n,名無し\nnew1,新規\n").Items, false);
            var mp = Construct<MergePreviewDialog>(preview, "テスト");
            mp.Show(); DoEvents(); Screenshot(mp, "09-merge-preview.png"); mp.Close();
            var bd = Construct<BackupDialog>(main);
            bd.Show(); DoEvents(); Screenshot(bd, "10-backup.png"); bd.Close();
            Construct<PlanListDialog>(main).Close();
            Construct<GameDataDialog>(main).Close();
            new InputDialog().Close();
            new ListMessageDialog().Close();

            window.Close();
            DoEvents();

            var errors = listener.Messages.Where(m => m.Contains("Error") || m.Contains("エラー")).ToList();
            Assert.True(errors.Count == 0, "バインディングエラー:\n" + string.Join("\n", errors));
        });
    }

    private static T Construct<T>(params object?[] args) where T : Window
    {
        var ctor = typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(c => c.GetParameters().Length == args.Length);
        return (T)ctor.Invoke(args);
    }
}
