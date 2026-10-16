using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Artemis.Core.Mods;

namespace Artemis.App.Services;

/// <summary>Mod thumbnails, decoded small and off the UI thread, cached by path.</summary>
public static class Thumbnails
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? PathFor(ModInfo mod)
    {
        if (mod.ContentDir is null) return null;
        foreach (var name in new[] { mod.Picture, "thumbnail.png", "thumbnail.jpg" })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var path = Path.Combine(mod.ContentDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static Task<ImageSource?> LoadAsync(string path) =>
        Cache.GetOrAdd(path, p => Task.Run(() => Decode(p)));

    private static ImageSource? Decode(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 64;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // a corrupt or unreadable picture just means the placeholder stays
        }
    }
}

/// <summary>A stable, muted gradient and initials for mods that have no picture.</summary>
public static class Placeholder
{
    private static readonly ConcurrentDictionary<int, Brush> Brushes = new();

    public static Brush For(string name)
    {
        var hue = Hue(name);
        return Brushes.GetOrAdd(hue, h =>
        {
            var brush = new LinearGradientBrush(FromHsl(h, .28, .36), FromHsl((h + 40) % 360, .30, .20), 45);
            brush.Freeze();
            return brush;
        });
    }

    public static string Initials(string name)
    {
        var letters = name.Split([' ', '-', '_', ':', '.'], StringSplitOptions.RemoveEmptyEntries)
                          .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
                          .Where(c => c != default).Take(2).ToArray();
        return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
    }

    private static int Hue(string name)
    {
        var h = 7u;
        foreach (var c in name) h = unchecked(h * 31 + c);
        return (int)(h % 360);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
