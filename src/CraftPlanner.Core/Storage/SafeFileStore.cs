using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CraftPlanner.Core.Models;

namespace CraftPlanner.Core.Storage;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま出力して読みやすくする
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>読み込み失敗の詳細。原因と復旧手段をユーザーに示すために使う。</summary>
public sealed class LoadFailure
{
    public string FilePath { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Recovery { get; init; } = "";
}

public sealed class LoadException : Exception
{
    public LoadFailure Failure { get; }
    public LoadException(LoadFailure failure, Exception? inner = null) : base(failure.Reason, inner) => Failure = failure;
}

public sealed record BackupEntry(string FilePath, DateTime CreatedAtLocal, long SizeBytes);

/// <summary>
/// アプリ専用領域への安全なファイル書き込み。
///  1. 一時ファイルに書く → 2. 読み戻して検証 → 3. 既存ファイルをバックアップ → 4. 置き換え → 5. 古い世代を削除。
/// 途中で失敗しても既存ファイルはそのまま残る。
/// </summary>
public sealed class SafeFileStore
{
    private readonly AppPaths _paths;
    private readonly Func<int> _backupGenerations;

    public SafeFileStore(AppPaths paths, Func<int>? backupGenerations = null)
    {
        _paths = paths;
        _backupGenerations = backupGenerations ?? (() => 10);
    }

    public AppPaths Paths => _paths;

    /// <summary>
    /// JSON を安全に保存する。validate は読み戻した値を検証し、エラーがあれば保存を中止する。
    /// </summary>
    public void WriteJson<T>(string targetFile, T value, Func<T, ValidationResult>? validate = null)
    {
        _paths.EnsureInsideRoot(targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var tmp = targetFile + ".tmp";

        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(true);
            }

            // 読み戻し検証: 書いた内容が確実にパースできることを確認してから本体を置き換える
            var readBack = JsonSerializer.Deserialize<T>(File.ReadAllText(tmp, Encoding.UTF8), JsonOptions.Default)
                ?? throw new InvalidDataException("書き込んだファイルを読み戻せませんでした。");
            if (validate != null)
            {
                var v = validate(readBack);
                if (!v.IsValid)
                    throw new InvalidDataException("保存内容の検証に失敗しました: " + string.Join(" / ", v.Errors));
            }

            if (File.Exists(targetFile)) CreateBackup(targetFile);
            File.Move(tmp, targetFile, overwrite: true);
            PruneBackups(targetFile);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* 一時ファイルの掃除失敗は致命的ではない */ }
            }
        }
    }

    /// <summary>JSON を読み込んで検証する。失敗時は原因と復旧方法を含む LoadException を投げ、ファイルは変更しない。</summary>
    public T ReadJson<T>(string file, Func<T, ValidationResult>? validate = null)
    {
        if (!File.Exists(file))
            throw new LoadException(new LoadFailure { FilePath = file, Reason = "ファイルが存在しません。", Recovery = "バックアップがあれば復元してください。" });
        string text;
        try
        {
            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new LoadException(new LoadFailure { FilePath = file, Reason = "ファイルを読み取れません: " + ex.Message, Recovery = "ほかのアプリで開いていないか確認し、必要ならバックアップから復元してください。" }, ex);
        }
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new LoadException(new LoadFailure
            {
                FilePath = file,
                Reason = $"JSON として解釈できません（{ex.Message}）。ファイルが破損している可能性があります。",
                Recovery = "「バックアップから復元」で直前の世代に戻せます。元ファイルは上書きされていません。",
            }, ex);
        }
        if (value == null)
            throw new LoadException(new LoadFailure { FilePath = file, Reason = "内容が空です。", Recovery = "バックアップから復元してください。" });
        if (validate != null)
        {
            var v = validate(value);
            if (!v.IsValid)
                throw new LoadException(new LoadFailure
                {
                    FilePath = file,
                    Reason = "データの検証に失敗しました: " + string.Join(" / ", v.Errors),
                    Recovery = "バックアップから復元するか、ファイルを手で修正してください。元ファイルは上書きされていません。",
                });
        }
        return value;
    }

    // ------------------------------------------------------------------
    // バックアップ
    // ------------------------------------------------------------------

    public string CreateBackup(string targetFile)
    {
        _paths.EnsureInsideRoot(targetFile);
        var dir = _paths.BackupDirFor(targetFile);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dest = Path.Combine(dir, stamp + Path.GetExtension(targetFile));
        var n = 1;
        while (File.Exists(dest)) dest = Path.Combine(dir, $"{stamp}-{n++}{Path.GetExtension(targetFile)}");
        File.Copy(targetFile, dest, overwrite: false);
        return dest;
    }

    public IReadOnlyList<BackupEntry> ListBackups(string targetFile)
    {
        var dir = _paths.BackupDirFor(targetFile);
        if (!Directory.Exists(dir)) return Array.Empty<BackupEntry>();
        return Directory.EnumerateFiles(dir, "*" + Path.GetExtension(targetFile))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupEntry(f.FullName, f.LastWriteTime, f.Length))
            .ToList();
    }

    /// <summary>世代数を超えた古いバックアップを削除する。対象はバックアップフォルダー内の自アプリ命名規則のファイルだけ。</summary>
    public void PruneBackups(string targetFile)
    {
        var keep = Math.Max(1, _backupGenerations());
        var list = ListBackups(targetFile);
        foreach (var old in list.Skip(keep))
        {
            _paths.EnsureInsideRoot(old.FilePath);
            if (!Path.GetFullPath(old.FilePath).StartsWith(Path.GetFullPath(_paths.BackupsDir), StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(old.FilePath);
        }
    }

    /// <summary>バックアップから復元する。復元前に現在のファイルもバックアップする。復元ファイルは事前に検証する。</summary>
    public void RestoreBackup<T>(string backupFile, string targetFile, Func<T, ValidationResult>? validate = null)
    {
        _paths.EnsureInsideRoot(backupFile);
        _paths.EnsureInsideRoot(targetFile);
        // 復元対象が読めることを先に確認する（壊れたバックアップで上書きしない）
        var value = ReadJson<T>(backupFile, validate);
        WriteJson(targetFile, value, validate);
    }

    public void DeleteFile(string targetFile)
    {
        _paths.EnsureInsideRoot(targetFile);
        if (File.Exists(targetFile))
        {
            CreateBackup(targetFile);   // 削除前にも世代を残す
            File.Delete(targetFile);
        }
    }
}
