using System.Windows;
using System.Windows.Threading;
using CraftPlanner.App.Services;
using CraftPlanner.App.Views;
using CraftPlanner.Core.Storage;

namespace CraftPlanner.App;

public partial class App : Application
{
    public static AppServices Services { get; internal set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Services?.Log.Error("未処理の例外（AppDomain）", args.ExceptionObject as Exception);

        Services = new AppServices(AppPaths.Default());
        try
        {
            Services.LoadAll();
        }
        catch (Exception ex)
        {
            Services.Log.Error("起動時の読み込みに失敗しました", ex);
            MessageBox.Show(
                "データの読み込みに失敗しました。\n\n" + ex.Message + "\n\nデータフォルダー: " + Services.Paths.Root +
                "\n\n既存のファイルは変更していません。ログ（logs フォルダー）を確認してください。",
                "CraftPlanner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 例外を握りつぶさず、記録してユーザーに伝える。アプリは可能な限り継続する。
        Services?.Log.Error("未処理の例外", e.Exception);
        MessageBox.Show(
            "予期しないエラーが発生しました。\n\n" + e.Exception.Message +
            "\n\n詳細はログ（" + (Services?.Paths.LogsDir ?? "logs") + "）に記録しました。未保存の変更がある場合は Ctrl+S で保存を試してください。",
            "CraftPlanner", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
