namespace CraftPlanner.Core.Storage;

/// <summary>
/// アプリが読み書きするすべてのパスをここで決める。
/// この領域（既定: %LOCALAPPDATA%\CraftPlanner）の外には、ユーザーが明示的に選んだファイル以外触れない。
/// テストでは一時フォルダーを root に渡す。
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string GamesDir => Path.Combine(Root, "games");
    public string PlansDir => Path.Combine(Root, "plans");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string LogsDir => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>環境変数 CRAFTPLANNER_DATA_DIR の名前。設定されていれば、その場所をデータフォルダーとして使う（テスト・検証用）。</summary>
    public const string DataDirEnvironmentVariable = "CRAFTPLANNER_DATA_DIR";

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return new AppPaths(overridden);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "CraftPlanner"));
    }

    public string GameFile(string gameId) => Path.Combine(GamesDir, SafeFileName(gameId) + ".json");
    public string PlanFile(string planId) => Path.Combine(PlansDir, SafeFileName(planId) + ".json");

    /// <summary>対象ファイルのバックアップ置き場。games\x.json → backups\games\x\</summary>
    public string BackupDirFor(string targetFile)
    {
        var full = Path.GetFullPath(targetFile);
        EnsureInsideRoot(full);
        var relative = Path.GetRelativePath(Root, full);
        var withoutExt = Path.ChangeExtension(relative, null);
        return Path.Combine(BackupsDir, withoutExt);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(GamesDir);
        Directory.CreateDirectory(PlansDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>アプリ領域の外を指すパスを拒否する。ファイル操作の範囲を限定するための最後の砦。</summary>
    public void EnsureInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"アプリのデータ領域の外へのアクセスは許可されていません: {path}");
    }

    public static string SafeFileName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("ID が空です。");
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);
        if (name is "." or "..") throw new ArgumentException("不正な ID です。");
        return name;
    }
}
