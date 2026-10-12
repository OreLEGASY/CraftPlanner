using System.Text;

namespace CraftPlanner.Core.Logging;

/// <summary>
/// アプリ専用領域の logs フォルダーに日付ごとのテキストログを書く。
/// 問題の原因調査に必要な情報（時刻・レベル・メッセージ・例外）を残す。
/// ログ書き込み自体の失敗でアプリを止めない。
/// </summary>
public sealed class FileLogger
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FileLogger(string logsDir)
    {
        _dir = logsDir;
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").Append(message);
            if (ex != null) sb.AppendLine().Append(ex);
            sb.AppendLine();
            lock (_gate)
            {
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // ログに書けないことでアプリを落とさない
        }
    }

    /// <summary>古いログを削除する（既定 30 日）。削除対象は自アプリ命名規則のファイルだけ。</summary>
    public void Prune(int keepDays = 30)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var limit = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(_dir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < limit) File.Delete(f);
            }
        }
        catch
        {
        }
    }
}
