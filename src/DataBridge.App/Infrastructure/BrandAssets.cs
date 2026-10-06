using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DataBridge.App.Infrastructure;

/// <summary>
/// Official DataBridge logo, compiled into the executable as a WPF resource (pack URI): nothing is read from the
/// source folder at run time, so DataBridge.exe works alone on any PC. The original file is used as is; only its
/// transparent margins are trimmed in memory so the logo can be sized by its visible height.
/// </summary>
public static class BrandAssets
{
    private const string LogoUri = "pack://application:,,,/Assets/Branding/databrige-logo.png";
    private const string IconUri = "pack://application:,,,/Assets/Branding/databridge-icon.ico";

    private static readonly Lazy<ImageSource?> _logo = new(LoadLogo);
    private static readonly Lazy<ImageSource?> _symbol = new(LoadSymbol);

    /// <summary>Horizontal DataBridge logo (mark + wordmark), transparent background, ratio preserved.</summary>
    public static ImageSource? Logo => _logo.Value;

    /// <summary>DataBridge symbol alone (largest frame of the official databridge-icon.ico).</summary>
    public static ImageSource? Symbol => _symbol.Value;

    private static ImageSource? LoadSymbol()
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(IconUri, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadLogo()
    {
        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(LogoUri, UriKind.Absolute);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();

            var bounds = VisibleBounds(source);
            if (bounds.IsEmpty) return source;
            var cropped = new CroppedBitmap(source, bounds);
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return null; // the header falls back to the product name
        }
    }

    /// <summary>Bounding box of the non-transparent pixels, with a small safety margin.</summary>
    private static Int32Rect VisibleBounds(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
            {
                if (pixels[row + x * 4 + 3] <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return Int32Rect.Empty;

        const int margin = 4;
        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(w - 1, maxX + margin);
        maxY = Math.Min(h - 1, maxY + margin);
        return new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
