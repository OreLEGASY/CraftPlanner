using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Storage;

/// <summary>読み込み結果。成功したものと、失敗したファイルの両方を返す（失敗を握りつぶさない）。</summary>
public sealed class LoadReport<T>
{
    public List<T> Loaded { get; } = new();
    public List<LoadFailure> Failures { get; } = new();
}

public sealed class GameDataRepository
{
    private readonly SafeFileStore _store;
    public GameDataRepository(SafeFileStore store) => _store = store;

    public string FileOf(string gameId) => _store.Paths.GameFile(gameId);

    public void Save(GameData data) => _store.WriteJson(FileOf(data.Id), data, Validators.ValidateGameData);

    public GameData Load(string gameId) => _store.ReadJson<GameData>(FileOf(gameId), Validators.ValidateGameData);

    public LoadReport<GameData> LoadAll()
    {
        var report = new LoadReport<GameData>();
        var dir = _store.Paths.GamesDir;
        if (!Directory.Exists(dir)) return report;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                report.Loaded.Add(_store.ReadJson<GameData>(file, Validators.ValidateGameData));
            }
            catch (LoadException ex)
            {
                report.Failures.Add(ex.Failure);
            }
        }
        return report;
    }

    public void Delete(string gameId) => _store.DeleteFile(FileOf(gameId));

    public IReadOnlyList<BackupEntry> Backups(string gameId) => _store.ListBackups(FileOf(gameId));

    public void Restore(string gameId, string backupFile) => _store.RestoreBackup<GameData>(backupFile, FileOf(gameId), Validators.ValidateGameData);
}

public sealed class PlanRepository
{
    private readonly SafeFileStore _store;
    public PlanRepository(SafeFileStore store) => _store = store;

    public string FileOf(string planId) => _store.Paths.PlanFile(planId);

    public void Save(Plan plan) => _store.WriteJson(FileOf(plan.Id), plan, p => Validators.ValidatePlan(p, null));

    public Plan Load(string planId) => _store.ReadJson<Plan>(FileOf(planId), p => Validators.ValidatePlan(p, null));

    public LoadReport<Plan> LoadAll()
    {
        var report = new LoadReport<Plan>();
        var dir = _store.Paths.PlansDir;
        if (!Directory.Exists(dir)) return report;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                report.Loaded.Add(_store.ReadJson<Plan>(file, p => Validators.ValidatePlan(p, null)));
            }
            catch (LoadException ex)
            {
                report.Failures.Add(ex.Failure);
            }
        }
        return report;
    }

    public void Delete(string planId) => _store.DeleteFile(FileOf(planId));

    public IReadOnlyList<BackupEntry> Backups(string planId) => _store.ListBackups(FileOf(planId));

    public void Restore(string planId, string backupFile) => _store.RestoreBackup<Plan>(backupFile, FileOf(planId), p => Validators.ValidatePlan(p, null));
}

public sealed class SettingsRepository
{
    private readonly SafeFileStore _store;
    public SettingsRepository(SafeFileStore store) => _store = store;

    public void Save(AppSettings settings) => _store.WriteJson(_store.Paths.SettingsFile, settings);

    /// <summary>設定は失っても致命的ではないので、読めない場合は既定値を返し、失敗内容を out で知らせる。元ファイルは残す。</summary>
    public AppSettings LoadOrDefault(out LoadFailure? failure)
    {
        failure = null;
        if (!File.Exists(_store.Paths.SettingsFile)) return new AppSettings();
        try
        {
            return _store.ReadJson<AppSettings>(_store.Paths.SettingsFile);
        }
        catch (LoadException ex)
        {
            failure = ex.Failure;
            return new AppSettings();
        }
    }
}
