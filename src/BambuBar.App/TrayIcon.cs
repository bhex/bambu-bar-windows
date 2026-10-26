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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Icon> SpinCache = new();

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
    public static void ClearCache()
    {
        Cache.Clear();
        SpinCache.Clear();
    }

    /// <summary>
    /// One frame of the "loading 3MF / slicing" spinner: a lime arc rotating around a printer
    /// glyph. The printer reports no numeric progress in this phase, so the arc is indeterminate.
    /// </summary>
    public static Icon PrepSpin(int frame)
    {
        int slot = ((frame % 24) + 24) % 24;
        if (SpinCache.TryGetValue(slot, out var hit)) return hit;
        using var bitmap = RenderPrepSpin(slot);
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            var icon = (Icon)temp.Clone();
            SpinCache[slot] = icon;
            return icon;
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static Bitmap RenderPrepSpin(int slot)
    {
        const int size = 48;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = size / 2f, cy = size / 2f;
        float arcR = size / 2f - 5f;

        using var bgPen = new Pen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 5f);
        bgPen.StartCap = LineCap.Round;
        bgPen.EndCap = LineCap.Round;
        g.DrawEllipse(bgPen, cx - arcR, cy - arcR, arcR * 2, arcR * 2);

        using var arcPen = new Pen(ToD(Palette.Accent), 5f);
        arcPen.StartCap = LineCap.Round;
        arcPen.EndCap = LineCap.Round;
        g.DrawArc(arcPen, cx - arcR, cy - arcR, arcR * 2, arcR * 2, slot * 15, 100);

        DrawPrinterGlyph(g, cx, cy, size * 0.55f, Color.White, DashStyle.Solid);
        return bitmap;
    }

    /// <summary>The little printer frame, centred on (cx, cy) in a <paramref name="box"/>-sized square.</summary>
    private static void DrawPrinterGlyph(Graphics g, float cx, float cy, float box, Color color, DashStyle dash)
    {
        float half = box / 2f;
        var frame = new RectangleF(cx - half, cy - half, box, box);
        using var outline = new Pen(color, Math.Max(1.5f, box * 0.06f));
        outline.DashStyle = dash;
        g.DrawRectangle(outline, frame);
        using var fill = new SolidBrush(color);
        float headY = frame.Top + frame.Height * 0.28f;
        float headW = frame.Width * 0.35f;
        g.FillRectangle(fill, cx - headW / 2, headY, headW, box * 0.08f);
        float plateY = frame.Bottom - frame.Height * 0.22f;
        g.FillRectangle(fill, frame.Left + frame.Width * 0.15f, plateY, frame.Width * 0.7f, box * 0.06f);
    }

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
        const int size = 48;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float u = size / 24f; // design grid: 24 units, glyph centred in a 20-unit chip

        // MARK: background chip (skip for active printing — transparent for contrast on dark taskbar)
        bool isPrinting = state is JobState.Printing or JobState.Preparing;
        if (!isPrinting)
        {
            Color chip;
            if (offline || state is null) chip = Color.FromArgb(0x8A, 0x91, 0x99);
            else chip = state switch
            {
                JobState.Idle => Color.FromArgb(0x6E, 0x74, 0x7B),
                JobState.Paused => ToD(Palette.Paused),
                JobState.Finished => ToD(Palette.Finished),
                JobState.Failed => ToD(Palette.Failed),
                _ => Color.FromArgb(0x6E, 0x74, 0x7B),
            };
            using var bg = new SolidBrush(chip);
            using var path = Rounded(2 * u, 2 * u, 20 * u, 20 * u, 4.5f * u);
            g.FillPath(bg, path);
        }

        var glyph = Color.White;
        float cx = size / 2f;
        float cy = size / 2f;

        switch (state)
        {
            case JobState.Printing or JobState.Preparing when fill > 0.02f:
            {
                // Thick progress arc (readable at 16px)
                float arcR = size / 2f - 5f;
                var bgPen = new Pen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 5f);
                bgPen.StartCap = LineCap.Round;
                bgPen.EndCap = LineCap.Round;
                g.DrawEllipse(bgPen, cx - arcR, cy - arcR, arcR * 2, arcR * 2);
                bgPen.Dispose();

                float sweep = (float)(fill * 350.0);
                var arcPen = new Pen(ToD(Palette.Accent), 5f);
                arcPen.StartCap = LineCap.Round;
                arcPen.EndCap = LineCap.Round;
                g.DrawArc(arcPen, cx - arcR, cy - arcR, arcR * 2, arcR * 2, -90, sweep);
                arcPen.Dispose();

                // Large bold percentage number
                int pct = (int)Math.Round(fill * 100);
                string num = pct >= 100 ? "100" : $"{pct}";
                float fontSize = pct >= 100 ? 16f : 20f;
                using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using var whiteBrush = new SolidBrush(Color.White);
                g.DrawString(num, font, whiteBrush, cx, cy, sf);
                sf.Dispose();
                break;
            }

            case JobState.Finished:
            {
                var chkPen = new Pen(glyph, 3f * u);
                chkPen.StartCap = LineCap.Round;
                chkPen.EndCap = LineCap.Round;
                g.DrawLines(chkPen, new[]
                {
                    new PointF(size * 0.25f, size * 0.52f),
                    new PointF(size * 0.42f, size * 0.70f),
                    new PointF(size * 0.75f, size * 0.34f),
                });
                chkPen.Dispose();
                break;
            }

            case JobState.Paused:
            {
                float barW = size * 0.14f;
                float barH = size * 0.42f;
                float gap = size * 0.08f;
                using var wb = new SolidBrush(glyph);
                g.FillRectangle(wb, cx - gap / 2 - barW, cy - barH / 2, barW, barH);
                g.FillRectangle(wb, cx + gap / 2, cy - barH / 2, barW, barH);
                break;
            }

            case JobState.Failed:
            {
                float barW = size * 0.13f;
                float barH = size * 0.38f;
                using var wb = new SolidBrush(glyph);
                g.FillRectangle(wb, cx - barW / 2, size * 0.22f, barW, barH);
                g.FillEllipse(wb, cx - barW / 2, size * 0.66f, barW, barW);
                break;
            }

            default: // Idle, null, offline: simple printer frame
            {
                DrawPrinterGlyph(g, cx, cy, size * 0.64f, glyph, offline || state is null ? DashStyle.Dash : DashStyle.Solid);
                break;
            }
        }

        // Alert badge (top-right corner)
        if (alert && state != JobState.Failed)
        {
            float badgeSize = size * 0.35f;
            float bx = size - badgeSize * 0.7f;
            float by = badgeSize * 0.2f;
            g.FillEllipse(Brushes.White, bx, by, badgeSize, badgeSize);
            using var red = new SolidBrush(Color.FromArgb(0xE5, 0x4B, 0x4B));
            float markW = badgeSize * 0.2f;
            g.FillRectangle(red, bx + badgeSize * 0.4f, by + badgeSize * 0.22f, markW, badgeSize * 0.42f);
            g.FillEllipse(red, bx + badgeSize * 0.4f, by + badgeSize * 0.7f, markW, markW);
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
