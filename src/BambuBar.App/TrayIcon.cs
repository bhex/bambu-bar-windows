using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Drawing.Imaging;
using BambuBar.Core.Models;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>
/// Renders the tray icon (port of the macOS <c>MenuBarIcon</c>): a small printer glyph whose chamber
/// fills with the print, plus pause/alert marks and a dashed outline while offline.
///
/// The macOS original is a *template* image that the menu bar tints; Windows has no equivalent, so the
/// Windows icon carries a rounded background chip in the current state colour and a white glyph. That
/// reads on both light and dark taskbars and tells the state at a glance.
/// </summary>
public static class TrayIcon
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Icon> Cache = new();

    /// <summary>
    /// Builds (or reuses) the icon for a state.
    /// <paramref name="state"/> is null when no printer is set up; <paramref name="offline"/> draws the dashed outline.
    /// </summary>
    public static Icon Get(JobState? state, double progress, bool alert, bool offline)
    {
        var step = (int)Math.Round(progress * 20); // 5 % steps are plenty at this size
        string key = $"{state?.ToString() ?? "none"}|{step}|{alert}|{offline}";
        if (Cache.TryGetValue(key, out var hit)) return hit;

        using var bitmap = Render(state, step / 20.0, alert, offline);
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            var icon = (Icon)temp.Clone(); // detach from the HICON we're about to destroy
            Cache[key] = icon;
            return icon;
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>Drops cached icons (after a state flood, the cache is bounded anyway).</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>The app icon (welcome screens, about page) — loaded from Resources when available.</summary>
    public static System.Windows.Media.ImageSource? AppImage()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
            if (File.Exists(path))
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                return image;
            }
        }
        catch
        {
            // Fall through: the app runs without a decorative icon.
        }
        return null;
    }

    private static Bitmap Render(JobState? state, double fill, bool alert, bool offline)
    {
        const int size = 32;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float u = size / 24f; // design grid: 24 units, glyph centred in a 20-unit chip

        // MARK: background chip
        Color chip;
        if (offline || state is null) chip = Color.FromArgb(0x8A, 0x91, 0x99);
        else chip = state switch
        {
            JobState.Idle => Color.FromArgb(0x6E, 0x74, 0x7B),
            JobState.Preparing => ToD(Palette.Preparing),
            JobState.Printing => ToD(Palette.Accent),
            JobState.Paused => ToD(Palette.Paused),
            JobState.Finished => ToD(Palette.Finished),
            JobState.Failed => ToD(Palette.Failed),
            _ => Color.FromArgb(0x6E, 0x74, 0x7B),
        };
        using (var bg = new SolidBrush(chip))
        using (var path = Rounded(2 * u, 2 * u, 20 * u, 20 * u, 4.5f * u))
        {
            g.FillPath(bg, path);
        }

        // MARK: glyph (white), scaled to the 24-unit grid — the macOS design is 18 units, so 4/3 scale
        var glyph = Color.White;
        float s = 4f / 3f; // macOS 18 pt design → 24 unit chip grid
        var frame = new RectangleF(2.25f * s * u, 1.75f * s * u, 13.5f * s * u, 14.5f * s * u);
        using (var outline = Rounded(frame.X, frame.Y, frame.Width, frame.Height, 2.6f * s * u))
        using (var pen = new Pen(glyph, 1.5f * u))
        {
            if (offline || state is null) pen.DashStyle = DashStyle.Dash;
            g.DrawPath(pen, outline);
        }
        if (offline || state is null) return bitmap;

        using var fillBrush = new SolidBrush(glyph);

        // Gantry with the toolhead.
        {
            var gantry = new RectangleF(frame.Left + 1 * s * u, 5.2f * s * u - 0.4f * s * u, frame.Width - 2 * s * u, 0.8f * s * u);
            g.FillRectangle(fillBrush, gantry);
        }
        // Build plate.
        float plateY = 13.4f * s * u;
        using (var plate = Rounded(frame.Left + 2.2f * s * u, plateY, frame.Width - 4.4f * s * u, 1.3f * s * u, 0.6f * s * u))
        {
            g.FillPath(fillBrush, plate);
        }

        switch (state)
        {
            case JobState.Paused:
                foreach (var x in new[] { 6.6f, 9.9f })
                {
                    using var bar = Rounded(x * s * u, 7.2f * s * u, 1.6f * s * u, 4.8f * s * u, 0.8f * s * u);
                    g.FillPath(fillBrush, bar);
                }
                break;

            case JobState.Failed:
            {
                using var exBar = Rounded(8.2f * s * u, 6.6f * s * u, 1.6f * s * u, 4.4f * s * u, 0.8f * s * u);
                g.FillPath(fillBrush, exBar);
                var exDot = new RectangleF(8.1f * s * u, 11.8f * s * u, 1.8f * s * u, 1.8f * s * u);
                g.FillEllipse(fillBrush, exDot);
                break;
            }

            case JobState.Printing or JobState.Preparing or JobState.Finished:
            {
                float maxH = plateY - 5.2f * s * u - 2.6f * s * u;
                float h = state == JobState.Finished ? maxH : Math.Max(0.9f * s * u, maxH * (float)fill);
                using var part = Rounded(6.2f * s * u, plateY - h, 5.6f * s * u, h, 0.9f * s * u);
                g.FillPath(fillBrush, part);
                if (state != JobState.Finished)
                {
                    float headY = Math.Max(5.2f * s * u + 0.6f * s * u, plateY - h - 2.3f * s * u);
                    using var head = Rounded(7.3f * s * u, headY, 3.4f * s * u, 1.8f * s * u, 0.5f * s * u);
                    g.FillPath(fillBrush, head);
                }
                break;
            }

            default: // Idle: just the toolhead parked at the top.
            {
                using var head = Rounded(frame.Left + 1.6f * s * u, 5.2f * s * u + 0.6f * s * u, 3.2f * s * u, 1.8f * s * u, 0.5f * s * u);
                g.FillPath(fillBrush, head);
                break;
            }
        }

        if (alert && state != JobState.Failed)
        {
            // Small badge in the corner: white disc, red mark.
            var badge = new RectangleF(11.2f * s * u - 2 * u, 0.2f * s * u - 2 * u, 6.6f * s * u, 6.6f * s * u);
            g.FillEllipse(Brushes.White, badge);
            using var red = new SolidBrush(Color.FromArgb(0xE5, 0x4B, 0x4B));
            var bx = badge.Left + badge.Width * 0.5f;
            using var markBar = Rounded(bx - 0.6f * u, badge.Top + 1.6f * u, 1.2f * u, 2.6f * u, 0.6f * u);
            g.FillPath(red, markBar);
            g.FillEllipse(red, bx - 0.65f * u, badge.Top + 4.4f * u, 1.3f * u, 1.3f * u);
        }

        // MARK: progress ring + percentage (when actively printing)
        if (state is JobState.Printing or JobState.Preparing && fill > 0.02f)
        {
            float cx = size / 2f;
            float cy = size / 2f;
            float ringRadius = size / 2f - 1.5f;
            float ringWidth = 2.5f;

            // Background ring (subtle)
            using (var bgRing = new Pen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), ringWidth))
            {
                g.DrawEllipse(bgRing, cx - ringRadius, cy - ringRadius, ringRadius * 2, ringRadius * 2);
            }

            // Progress arc (bright)
            float sweepAngle = (float)(fill * 360.0);
            var ringPen = new Pen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), ringWidth + 0.5f);
            ringPen.StartCap = LineCap.Round;
            ringPen.EndCap = LineCap.Round;
            g.DrawArc(ringPen, cx - ringRadius, cy - ringRadius, ringRadius * 2, ringRadius * 2, -90, sweepAngle);
            ringPen.Dispose();

            // Percentage text (small, centred)
            if (fill > 0.04f)
            {
                string pctText = $"{(int)Math.Round(fill * 100)}%";
                using var font = new Font("Segoe UI", 5.5f, FontStyle.Bold);
                using var textBrush = Brushes.White;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var textRect = new RectangleF(0, size / 2f - 8f, size, 16f);
                g.DrawString(pctText, font, textBrush, textRect, sf);
            }
        }

        return bitmap;
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius, Math.Min(w, h) / 2);
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ToD(System.Windows.Media.Color c) => Color.FromArgb(c.R, c.G, c.B);
}
