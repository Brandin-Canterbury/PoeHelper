using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PoeHelper.App;

/// <summary>Draws the tray icon at runtime (a gold "P" on a dark disc), so there's no binary asset to keep.</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(255, 30, 26, 22));
            using var ring = new Pen(Color.FromArgb(255, 181, 136, 74), 2);
            g.FillEllipse(bg, 1, 1, 29, 29);
            g.DrawEllipse(ring, 1, 1, 29, 29);
            using var font = new Font("Georgia", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var gold = new SolidBrush(Color.FromArgb(255, 242, 200, 106));
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("P", font, gold, new RectangleF(0, 1, 32, 32), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
