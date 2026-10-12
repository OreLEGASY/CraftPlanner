using System.Security.Cryptography;

namespace CraftPlanner.Core.Ids;

/// <summary>
/// 接頭辞付きの短い ID を発行する。読みやすさのため 8 桁の英数字（小文字）を使い、
/// 呼び出し側から渡された「既存 ID かどうか」の判定で衝突を避ける。
/// </summary>
public static class IdGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string NewItemId(Func<string, bool> exists) => New("item-", exists);
    public static string NewRecipeId(Func<string, bool> exists) => New("recipe-", exists);
    public static string NewNodeId(Func<string, bool> exists) => New("node-", exists);
    public static string NewEdgeId(Func<string, bool> exists) => New("edge-", exists);
    public static string NewPlanId(Func<string, bool> exists) => New("plan-", exists);
    public static string NewGameId(Func<string, bool> exists) => New("game-", exists);

    public static string New(string prefix, Func<string, bool> exists)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = prefix + RandomSuffix(8);
            if (!exists(id)) return id;
        }
        // 8 桁で 100 回連続衝突することは現実的にないが、無限ループは避ける。
        throw new InvalidOperationException("一意な ID を生成できませんでした。");
    }

    private static string RandomSuffix(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }

    /// <summary>ユーザー入力の ID として許可する文字か（英数字・ハイフン・アンダースコア・ドット）。</summary>
    public static bool IsValidUserId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
        }
        return true;
    }
}
