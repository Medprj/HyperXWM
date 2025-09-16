using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace HyperXWM;

public static class BatteryTrayIconRenderer
{
    /// <summary>
    /// Redraw tray icon.
    /// </summary>
    public static Icon CreateIcon(int percent)
    {
        percent = Math.Max(0, Math.Min(100, percent));

        var fill = percent switch
        {
            <= 20 => Color.Red,
            <= 30 => Color.Orange,
            <= 50 => Color.LightGreen,
            _ => Color.Chartreuse
        };

        using var bmp = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bmp);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        // battery geometry
        // body
        var body = new Rectangle(3, 8, 24, 16); // x,y,w,h
        const int radius = 4; // corner radius
        // terminal ("nose")
        var term = new Rectangle(body.Right + 1, body.Y + 4, 3, body.Height - 8);

        // outline brushes/pens
        using var outlinePen = new Pen(Color.White, 2f);
        using var emptyBrush = new SolidBrush(Color.FromArgb(28, 0, 0, 0)); // subtle empty bg
        using var fillBrush = new SolidBrush(fill);

        using (var bodyPath = CreateRoundedRect(body, radius))
        {
            graphics.FillPath(emptyBrush, bodyPath);
            graphics.DrawPath(outlinePen, bodyPath);
        }

        // draw terminal
        graphics.FillRectangle(emptyBrush, term);
        graphics.DrawRectangle(outlinePen, term);

        // inner fill area (with padding inside the body)
        const int pad = 3;
        var inner = new Rectangle(body.X + pad, body.Y + pad,
            body.Width - pad * 2, body.Height - pad * 2);

        // width proportional to percent
        var fillW = (int)Math.Round(inner.Width * (percent / 100f));
        if (fillW > 0)
        {
            var bar = new Rectangle(inner.X, inner.Y, fillW, inner.Height);
            graphics.FillRectangle(fillBrush, bar);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);

        // rounded rectangle path for body
        GraphicsPath CreateRoundedRect(Rectangle r, int cornerRadius)
        {
            var diameter = cornerRadius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, diameter, diameter, 180, 90); // top left
            path.AddArc(r.Right - diameter, r.Top, diameter, diameter, 270, 90); // top right
            path.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90); // bottom right
            path.AddArc(r.Left, r.Bottom - diameter, diameter, diameter, 90, 90); // bottom left
            path.CloseFigure();
            return path;
        }
    }
}