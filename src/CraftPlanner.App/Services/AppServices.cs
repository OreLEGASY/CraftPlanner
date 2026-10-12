using System.IO;
using CraftPlanner.Core.Ids;
using CraftPlanner.Core.Logging;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Storage;

namespace CraftPlanner.App.Services;

/// <summary>
/// アプリ全体で共有するサービス。保存先・リポジトリ・ログ・設定・読み込み済みデータを束ねる。
/// ファイル操作はすべてここ（と Core の Storage）を経由する。
/// </summary>
public sealed class AppServices
{
    public AppPaths Paths { get; }
    public SafeFileStore Store { get; }
    public GameDataRepository Games { get; }
    public PlanRepository Plans { get; }
    public SettingsRepository SettingsRepo { get; }
    public FileLogger Log { get; }
    public AppSettings Settings { get; private set; } = new();

    public List<GameData> LoadedGames { get; } = new();
    public List<Plan> LoadedPlans { get; } = new();
    public List<LoadFailure> StartupFailures { get; } = new();

    public DateTime? LastSaveTime { get; private set; }
    public string? LastSaveError { get; private set; }

    /// <summary>保存結果の通知（メッセージ, エラーか）。ステータスバーが購読する。</summary>
    public event Action<string, bool>? SaveStatusChanged;

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Log = new FileLogger(paths.LogsDir);
        Store = new SafeFileStore(paths, () => Settings.BackupGenerations);
        Games = new GameDataRepository(Store);
        Plans = new PlanRepository(Store);
        SettingsRepo = new SettingsRepository(Store);
    }

    public void LoadAll()
    {
        Paths.EnsureDirectories();
        Log.Prune();
        Log.Info("起動: データフォルダー " + Paths.Root);

        Settings = SettingsRepo.LoadOrDefault(out var settingsFailure);
        if (settingsFailure != null) StartupFailures.Add(settingsFailure);

        var games = Games.LoadAll();
        LoadedGames.AddRange(games.Loaded);
        StartupFailures.AddRange(games.Failures);

        var plans = Plans.LoadAll();
        LoadedPlans.AddRange(plans.Loaded);
        StartupFailures.AddRange(plans.Failures);

        foreach (var f in StartupFailures) Log.Error($"読み込み失敗: {f.FilePath}: {f.Reason}");

        if (LoadedGames.Count == 0 && games.Failures.Count == 0)
        {
            // 初回起動。空のゲームデータを 1 つ用意する（既存ファイルは一切ない状態なので安全）。
            var g = CreateGame("新しいゲーム");
            Log.Info("初回起動のためゲームデータを作成: " + g.Id);
        }
    }

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

    public bool SaveGame(GameData game)
    {
        try
        {
            Games.Save(game);
            ReportSaved($"ゲームデータ「{game.Name}」を保存しました");
            return true;
        }
        catch (Exception ex)
        {
            ReportFailed($"ゲームデータ「{game.Name}」の保存に失敗: {ex.Message}", ex);
            return false;
        }
    }

    public bool SavePlan(Plan plan)
    {
        try
        {
            Plans.Save(plan);
            ReportSaved($"計画「{plan.Name}」を保存しました");
            return true;
        }
        catch (Exception ex)
        {
            ReportFailed($"計画「{plan.Name}」の保存に失敗: {ex.Message}", ex);
            return false;
        }
    }

    public bool SaveSettings()
    {
        try
        {
            SettingsRepo.Save(Settings);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("設定の保存に失敗", ex);
            SaveStatusChanged?.Invoke("設定の保存に失敗: " + ex.Message, true);
            return false;
        }
    }

    private void ReportSaved(string message)
    {
        LastSaveTime = DateTime.Now;
        LastSaveError = null;
        Log.Info(message);
        SaveStatusChanged?.Invoke(message, false);
    }

    private void ReportFailed(string message, Exception ex)
    {
        LastSaveError = message;
        Log.Error(message, ex);
        SaveStatusChanged?.Invoke(message, true);
    }

    // ------------------------------------------------------------------
    // ゲームデータ・計画の作成と削除
    // ------------------------------------------------------------------

    public GameData CreateGame(string name)
    {
        var game = new GameData
        {
            Id = IdGenerator.NewGameId(id => LoadedGames.Any(g => g.Id == id) || File.Exists(Paths.GameFile(id))),
            Name = string.IsNullOrWhiteSpace(name) ? "新しいゲーム" : name.Trim(),
        };
        Games.Save(game); // 失敗時は例外を呼び出し側へ
        LoadedGames.Add(game);
        return game;
    }

    /// <summary>取り込んだゲームデータを新規として登録する（ID は新規発行して衝突を避ける）。</summary>
    public GameData RegisterImportedGame(GameData imported, string name)
    {
        var game = imported.Clone();
        game.Id = IdGenerator.NewGameId(id => LoadedGames.Any(g => g.Id == id) || File.Exists(Paths.GameFile(id)));
        game.Name = string.IsNullOrWhiteSpace(name) ? imported.Name : name.Trim();
        game.FormatVersion = GameData.CurrentFormatVersion;
        Games.Save(game);
        LoadedGames.Add(game);
        return game;
    }

    /// <summary>ゲームデータの差し替え（取り込み統合の適用後など）。保存に成功した場合だけメモリ上も置き換える。</summary>
    public void ReplaceGame(GameData replacement)
    {
        Games.Save(replacement);
        var idx = LoadedGames.FindIndex(g => g.Id == replacement.Id);
        if (idx >= 0) LoadedGames[idx] = replacement; else LoadedGames.Add(replacement);
    }

    public void DeleteGame(GameData game)
    {
        var plans = PlansOf(game.Id).ToList();
        if (plans.Count > 0)
            throw new InvalidOperationException($"このゲームデータを参照する制作計画が {plans.Count} 件あります（{string.Join("、", plans.Select(p => p.Name))}）。先に計画を削除してください。");
        Games.Delete(game.Id);
        LoadedGames.Remove(game);
        Log.Info($"ゲームデータを削除: {game.Id}");
    }

    public IEnumerable<Plan> PlansOf(string gameId) => LoadedPlans.Where(p => p.GameDataId == gameId);

    public Plan CreatePlan(GameData game, string name)
    {
        var plan = new Plan
        {
            Id = IdGenerator.NewPlanId(id => LoadedPlans.Any(p => p.Id == id) || File.Exists(Paths.PlanFile(id))),
            Name = string.IsNullOrWhiteSpace(name) ? "新しい計画" : name.Trim(),
            GameDataId = game.Id,
        };
        Plans.Save(plan);
        LoadedPlans.Add(plan);
        return plan;
    }

    public Plan RegisterImportedPlan(Plan imported, string? name = null)
    {
        var plan = imported.Clone();
        plan.Id = IdGenerator.NewPlanId(id => LoadedPlans.Any(p => p.Id == id) || File.Exists(Paths.PlanFile(id)));
        if (!string.IsNullOrWhiteSpace(name)) plan.Name = name.Trim();
        plan.FormatVersion = Plan.CurrentFormatVersion;
        Plans.Save(plan);
        LoadedPlans.Add(plan);
        return plan;
    }

    public void DeletePlan(Plan plan)
    {
        Plans.Delete(plan.Id);
        LoadedPlans.RemoveAll(p => p.Id == plan.Id);
        Settings.OpenPlanIds.Remove(plan.Id);
        if (Settings.ActivePlanId == plan.Id) Settings.ActivePlanId = null;
        SaveSettings();
        Log.Info($"計画を削除: {plan.Id}");
    }

    /// <summary>メモリ上の計画を差し替える（Undo などで Plan インスタンスが入れ替わるため）。</summary>
    public void ReplacePlanInstance(Plan plan)
    {
        var idx = LoadedPlans.FindIndex(p => p.Id == plan.Id);
        if (idx >= 0) LoadedPlans[idx] = plan; else LoadedPlans.Add(plan);
    }
}
