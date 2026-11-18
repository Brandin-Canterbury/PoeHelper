using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PoeHelper.App.Overlay;

public static class OverlaySnapshot
{
    /// <summary>Renders a window's content to PNG (for QA and bug reports).</summary>
    public static void Save(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(60, 60, 60)), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(size));
        }
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
