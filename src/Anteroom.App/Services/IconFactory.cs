using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Anteroom.App.Services;

/// <summary>
/// Draws the tray icons at runtime, so Anteroom ships with no binary assets and stays sharp at any
/// tray DPI. The mark is a doorway - an anteroom - with a star above it when a session is waiting.
/// </summary>
public static class IconFactory
{
    public static readonly Color Accent = Color.FromArgb(0xF2, 0xA0, 0x4B);
    public static readonly Color Calm = Color.FromArgb(0xB6, 0xBB, 0xC4);

    private static readonly Dictionary<(bool, int), Icon> Cache = new();

    public static Icon Get(bool needsAttention, int size = 32)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((needsAttention, size), out var cached)) return cached;
            var icon = Render(needsAttention, size);
            Cache[(needsAttention, size)] = icon;
            return icon;
        }
    }

    public static void DisposeAll()
    {
        lock (Cache)
        {
            foreach (var icon in Cache.Values)
            {
                DestroyIcon(icon.Handle);
                icon.Dispose();
            }
            Cache.Clear();
        }
    }

    private static Icon Render(bool needsAttention, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float unit = size / 32f;
            var body = needsAttention ? Accent : Calm;

            // Doorway: a rounded arch, open at the bottom.
            using var path = new GraphicsPath();
            float left = 8 * unit, right = 24 * unit, top = 10 * unit, bottom = 28 * unit;
            float radius = (right - left) / 2f;
            path.AddArc(left, top, right - left, radius * 2, 180, 180);
            path.AddLine(right, top + radius, right, bottom);
            path.AddLine(left, bottom, left, top + radius);

            if (needsAttention)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, body));
                g.FillPath(fill, path);
            }

            using var pen = new Pen(body, 2.4f * unit) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, path);

            if (needsAttention) DrawStar(g, new PointF(24.5f * unit, 8f * unit), 7.5f * unit);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void DrawStar(Graphics g, PointF centre, float outerRadius)
    {
        const int points = 5;
        float innerRadius = outerRadius * 0.45f;
        var vertices = new PointF[points * 2];

        for (int i = 0; i < vertices.Length; i++)
        {
            float radius = i % 2 == 0 ? outerRadius : innerRadius;
            double angle = -Math.PI / 2 + i * Math.PI / points;
            vertices[i] = new PointF(
                centre.X + (float)(Math.Cos(angle) * radius),
                centre.Y + (float)(Math.Sin(angle) * radius));
        }

        using var glow = new SolidBrush(Color.FromArgb(230, 26, 27, 30));
        g.FillEllipse(glow, centre.X - outerRadius, centre.Y - outerRadius, outerRadius * 2, outerRadius * 2);

        using var brush = new SolidBrush(Accent);
        g.FillPolygon(brush, vertices);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
