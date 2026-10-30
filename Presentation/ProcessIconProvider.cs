using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static CompactLoader.Interop.NativeMethods;
using static CompactLoader.Interop.ProcessIconNativeMethods;

namespace CompactLoader;

internal static class ProcessIconProvider
{
    const int CacheLimit = 512;
    static readonly ConcurrentDictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ImageSource fallback = CreateFallback();

    // Called on the refresh worker. Frozen images can safely cross to the UI thread.
    internal static ImageSource Get(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) return fallback;
        var filename = new StringBuilder(32768);
        uint size = (uint)filename.Capacity;
        if (!QueryFullProcessImageName(process, 0, filename, ref size)) return fallback;
        var path = filename.ToString();
        if (cache.TryGetValue(path, out var existing)) return existing;
        var icon = Extract(path);
        if (cache.Count < CacheLimit) cache.TryAdd(path, icon);
        return icon;
    }

    static ImageSource Extract(string path)
    {
        nint handle = 0;
        try
        {
            if (ExtractIconEx(path, 0, out handle, 0, 1) == 0 || handle == 0) return fallback;
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (ArgumentException) { return fallback; }
        catch (System.Runtime.InteropServices.ExternalException) { return fallback; }
        finally { if (handle != 0) DestroyIcon(handle); }
    }

    static ImageSource CreateFallback()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(36, 36, 36)),
                new Pen(new SolidColorBrush(Color.FromRgb(128, 128, 128)), 1), new Rect(1, 2, 22, 20), 2, 2);
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(168, 168, 168)), 1), new Point(2, 8), new Point(22, 8));
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(159, 159, 159)), null, new Rect(6, 12, 5, 5), 1, 1);
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
