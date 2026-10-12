using System.Text;
using System.Text.Json;
using CraftPlanner.Core.Models;
using CraftPlanner.Core.Storage;

namespace CraftPlanner.Core.ImportExport;

public sealed class PlanGameMismatchException : Exception
{
    public string PlanGameDataId { get; }
    public string ExpectedGameDataId { get; }

    public PlanGameMismatchException(string planGameId, string expectedGameId)
        : base($"この制作計画はゲームデータ「{planGameId}」を参照していますが、現在選択中のゲームデータは「{expectedGameId}」です。元のゲームデータを選択してから読み込んでください。")
    {
        PlanGameDataId = planGameId;
        ExpectedGameDataId = expectedGameId;
    }
}

/// <summary>
/// ユーザーが明示的に選んだファイルとの JSON のやり取り。
/// 読み込み元ファイルは一切変更しない。書き出し先はダイアログで選ばれたパスにだけ書く。
/// </summary>
public static class JsonTransfer
{
    public static void ExportGameData(GameData data, string path)
    {
        var v = Validators.ValidateGameData(data);
        if (!v.IsValid) throw new InvalidOperationException("ゲームデータに問題があるため書き出せません: " + string.Join(" / ", v.Errors));
        WriteAtomically(path, JsonSerializer.Serialize(data, JsonOptions.Default));
    }

    public static GameData ReadGameDataFile(string path)
    {
        var data = Read<GameData>(path);
        var v = Validators.ValidateGameData(data);
        if (!v.IsValid)
            throw new LoadException(new LoadFailure { FilePath = path, Reason = "ゲームデータの検証に失敗しました: " + string.Join(" / ", v.Errors), Recovery = "書き出し元のアプリで内容を確認してください。" });
        return data;
    }

    public static void ExportPlan(Plan plan, string path)
    {
        var v = Validators.ValidatePlan(plan, null);
        if (!v.IsValid) throw new InvalidOperationException("制作計画に問題があるため書き出せません: " + string.Join(" / ", v.Errors));
        WriteAtomically(path, JsonSerializer.Serialize(plan, JsonOptions.Default));
    }

    /// <summary>参照元ゲームデータが一致しない計画は読み込みを拒否する。</summary>
    public static Plan ReadPlanFile(string path, string expectedGameDataId)
    {
        var plan = Read<Plan>(path);
        var v = Validators.ValidatePlan(plan, null);
        if (!v.IsValid)
            throw new LoadException(new LoadFailure { FilePath = path, Reason = "制作計画の検証に失敗しました: " + string.Join(" / ", v.Errors), Recovery = "書き出し元のアプリで内容を確認してください。" });
        if (plan.GameDataId != expectedGameDataId)
            throw new PlanGameMismatchException(plan.GameDataId, expectedGameDataId);
        return plan;
    }

    private static T Read<T>(string path)
    {
        if (!File.Exists(path))
            throw new LoadException(new LoadFailure { FilePath = path, Reason = "ファイルが存在しません。", Recovery = "パスを確認してください。" });
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(text, JsonOptions.Default)
                ?? throw new LoadException(new LoadFailure { FilePath = path, Reason = "内容が空です。", Recovery = "正しい JSON ファイルを選んでください。" });
        }
        catch (JsonException ex)
        {
            throw new LoadException(new LoadFailure { FilePath = path, Reason = "JSON として解釈できません: " + ex.Message, Recovery = "このアプリで書き出した JSON ファイルを選んでください。" }, ex);
        }
    }

    /// <summary>書き出し先にも一時ファイル経由で書き、途中失敗で壊れたファイルを残さない。</summary>
    private static void WriteAtomically(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
        }
    }
}
