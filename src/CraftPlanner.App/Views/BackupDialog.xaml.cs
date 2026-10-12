using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CraftPlanner.App.ViewModels;
using CraftPlanner.Core.Storage;

namespace CraftPlanner.App.Views;

public sealed class BackupTarget
{
    public string Label { get; init; } = "";
    public string TargetFile { get; init; } = "";
    public string? GameId { get; init; }
    public string? PlanId { get; init; }
}

public sealed record BackupRow(BackupEntry Entry)
{
    public string When => Entry.CreatedAtLocal.ToString("yyyy-MM-dd HH:mm:ss");
    public string Size => $"{Entry.SizeBytes / 1024.0:0.0} KB";
    public string FileName => Path.GetFileName(Entry.FilePath);
}

/// <summary>バックアップ世代の一覧表示と復元。</summary>
public partial class BackupDialog : Window
{
    private readonly MainViewModel _main;

    private BackupDialog(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        var s = main.Services;
        var targets = new List<BackupTarget>();
        foreach (var g in s.LoadedGames.OrderBy(g => g.Name, StringComparer.CurrentCulture))
        {
            targets.Add(new BackupTarget { Label = $"ゲームデータ: {g.Name}", TargetFile = s.Games.FileOf(g.Id), GameId = g.Id });
            foreach (var p in s.PlansOf(g.Id).OrderBy(p => p.Name, StringComparer.CurrentCulture))
                targets.Add(new BackupTarget { Label = $"　計画: {p.Name}（{g.Name}）", TargetFile = s.Plans.FileOf(p.Id), PlanId = p.Id });
        }
        // 読み込めなかったファイルも復元対象にする
        foreach (var f in s.StartupFailures.Where(f => f.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (targets.Any(t => string.Equals(t.TargetFile, f.FilePath, StringComparison.OrdinalIgnoreCase))) continue;
            var isGame = f.FilePath.StartsWith(s.Paths.GamesDir, StringComparison.OrdinalIgnoreCase);
            var isPlan = f.FilePath.StartsWith(s.Paths.PlansDir, StringComparison.OrdinalIgnoreCase);
            if (!isGame && !isPlan) continue;
            var id = Path.GetFileNameWithoutExtension(f.FilePath);
            targets.Add(new BackupTarget
            {
                Label = $"（読み込み失敗）{(isGame ? "ゲームデータ" : "計画")}: {id}",
                TargetFile = f.FilePath,
                GameId = isGame ? id : null,
                PlanId = isPlan ? id : null,
            });
        }
        TargetBox.ItemsSource = targets;
        var preferred = main.ActivePlan != null ? targets.FirstOrDefault(t => t.PlanId == main.ActivePlan.Plan.Id) : null;
        TargetBox.SelectedItem = preferred ?? targets.FirstOrDefault(t => t.GameId == main.SelectedGame?.Id) ?? targets.FirstOrDefault();
    }

    public static void Show(Window owner, MainViewModel main)
    {
        new BackupDialog(main) { Owner = owner }.ShowDialog();
    }

    private BackupTarget? Target => TargetBox.SelectedItem as BackupTarget;

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        if (Target == null) { List.ItemsSource = null; return; }
        var backups = _main.Services.Store.ListBackups(Target.TargetFile);
        List.ItemsSource = backups.Select(b => new BackupRow(b)).ToList();
        PathText.Text = $"現在のファイル: {Target.TargetFile}\nバックアップ: {_main.Services.Paths.BackupDirFor(Target.TargetFile)}（{backups.Count} 世代）";
        RestoreButton.IsEnabled = backups.Count > 0;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Target == null) return;
        var dir = _main.Services.Paths.BackupDirFor(Target.TargetFile);
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }

    private void CreateNow_Click(object sender, RoutedEventArgs e)
    {
        if (Target == null) return;
        try
        {
            if (!File.Exists(Target.TargetFile)) { MessageBox.Show(this, "現在のファイルが存在しないためバックアップできません。"); return; }
            // 開いている計画は先に保存してから世代を作る
            var open = _main.OpenPlans.FirstOrDefault(p => p.Plan.Id == Target.PlanId);
            if (open != null && !open.SaveNow()) { MessageBox.Show(this, "保存に失敗したためバックアップを作成しませんでした。"); return; }
            var path = _main.Services.Store.CreateBackup(Target.TargetFile);
            _main.Services.Log.Info("手動バックアップ: " + path);
            RefreshList();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "バックアップに失敗", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Target == null || List.SelectedItem is not BackupRow row) { MessageBox.Show(this, "復元する世代を選択してください。"); return; }
        var r = MessageBox.Show(this,
            $"{row.When} の世代に復元しますか？\n\n対象: {Target.Label}\n現在の内容は復元前にバックアップとして保存されます。開いているタブは復元後の内容で開き直されます。",
            "復元の確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        var s = _main.Services;
        try
        {
            if (Target.GameId != null)
            {
                s.Games.Restore(Target.GameId, row.Entry.FilePath);
                var game = s.Games.Load(Target.GameId);
                var idx = s.LoadedGames.FindIndex(g => g.Id == game.Id);
                if (idx >= 0) s.LoadedGames[idx] = game; else s.LoadedGames.Add(game);
                _main.RefreshGames();
                _main.GameReplaced(game);
            }
            else if (Target.PlanId != null)
            {
                var open = _main.OpenPlans.FirstOrDefault(p => p.Plan.Id == Target.PlanId);
                s.Plans.Restore(Target.PlanId, row.Entry.FilePath);
                var plan = s.Plans.Load(Target.PlanId);
                s.ReplacePlanInstance(plan);
                if (open != null) _main.PlanReplaced(plan);
            }
            s.StartupFailures.RemoveAll(f => string.Equals(f.FilePath, Target.TargetFile, StringComparison.OrdinalIgnoreCase));
            s.Log.Info($"復元: {Target.TargetFile} ← {row.Entry.FilePath}");
            MessageBox.Show(this, "復元しました。", Title);
            RefreshList();
        }
        catch (LoadException ex)
        {
            MessageBox.Show(this, "復元を中止しました。\n" + ex.Failure.Reason + "\n" + ex.Failure.Recovery, "復元できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            s.Log.Error("復元に失敗", ex);
            MessageBox.Show(this, "復元に失敗しました: " + ex.Message, "復元できません", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
