using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MicTap.App;

/// <summary>The tray icon: the app icon (red while recording) with the current language as a badge.</summary>
public static class TrayIcons
{
    private const int Size = 32;
    private const double BadgeHeight = 15;
    private static readonly Brush BadgeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1D, 0x1B, 0x20)));
    private static readonly Typeface BadgeFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static System.Drawing.Icon Render(bool recording, string language)
    {
        var decoder = BitmapDecoder.Create(
            new Uri($"pack://application:,,,/Assets/{(recording ? "mictap-recording" : "mictap")}.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - Size)).First();

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(frame, new Rect(0, 0, Size, Size));
            var text = new FormattedText(
                language.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, BadgeFont, 12, Brushes.White, 1.0);
            var badge = new Rect(Size - text.Width - 4, Size - BadgeHeight, text.Width + 4, BadgeHeight);
            context.DrawRoundedRectangle(BadgeBrush, null, badge, 3, 3);
            context.DrawText(text, new Point(badge.X + 2, badge.Y + ((BadgeHeight - text.Height) / 2)));
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;

        using var drawing = new System.Drawing.Bitmap(png);
        IntPtr handle = drawing.GetHicon();
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
