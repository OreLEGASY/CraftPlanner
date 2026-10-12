using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CraftPlanner.App.Services;

/// <summary>
/// アイコン画像の読み込みと変換。ユーザーが選んだ画像を最大 128×128 の PNG に縮小して Base64 にする。
/// 元の画像ファイルは読み取るだけで変更しない。
/// </summary>
public static class IconService
{
    public const int MaxSize = 128;
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    public static string EncodeFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        var scale = Math.Min(1.0, (double)MaxSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1.0)
        {
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static ImageSource? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(base64, out var cached)) return cached;
        }
        ImageSource? result = null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            result = image;
        }
        catch
        {
            // 壊れた画像データは「アイコンなし」として扱う（データ自体は保持する）
        }
        lock (Cache)
        {
            if (Cache.Count > 2000) Cache.Clear();
            Cache[base64] = result;
        }
        return result;
    }
}
