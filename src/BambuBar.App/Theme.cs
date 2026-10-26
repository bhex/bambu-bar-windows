using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BambuBar.Core.Models;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace BambuBar.App;

/// <summary>
/// The palette shared by the macOS app and the widget, ported verbatim so both builds look the same.
/// </summary>
public static class Palette
{
    /// <summary>Bambu-ish green, a touch brighter so it reads on dark glass.</summary>
    public static readonly Color Accent = FromRgb(0.13, 0.80, 0.42);
    public static readonly Color AccentDeep = FromRgb(0.00, 0.60, 0.29);
    public static readonly Color Preparing = FromRgb(0.36, 0.66, 1.00);
    public static readonly Color Paused = FromRgb(1.00, 0.72, 0.24);
    public static readonly Color Failed = FromRgb(1.00, 0.36, 0.36);
    public static readonly Color Finished = FromRgb(0.42, 0.86, 0.62);
    public static readonly Color Idle = FromRgb(0.62, 0.66, 0.70);
    public static readonly Color Nozzle = FromRgb(1.00, 0.48, 0.30);
    public static readonly Color Bed = FromRgb(1.00, 0.72, 0.30);
    public static readonly Color Chamber = FromRgb(0.55, 0.70, 1.00);

    public static Color ForState(JobState state) => state switch
    {
        JobState.Idle => Idle,
        JobState.Preparing => Preparing,
        JobState.Printing => Accent,
        JobState.Paused => Paused,
        JobState.Finished => Finished,
        JobState.Failed => Failed,
        _ => Idle,
    };

    private static Color FromRgb(double r, double g, double b) =>
        Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

    /// <summary>
    /// Parses a filament <c>RRGGBB</c>/<c>RRGGBBAA</c> string. Fully transparent colours (unset slots)
    /// come back null; very low alpha is clamped up, as the macOS original does.
    /// </summary>
    public static Color? Filament(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var clean = new string(hex.Where(char.IsLetterOrDigit).ToArray());
        if (clean.Length != 6 && clean.Length != 8) return null;
        if (!ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
        double a = clean.Length == 8 ? (value & 0xFF) / 255.0 : 1.0;
        if (a == 0) return null;
        a = Math.Max(a, 0.35);
        return Color.FromArgb((byte)(a * 255), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }

    /// <summary>Perceived brightness of a <c>RRGGBB(AA)</c> colour, 0-1, for picking legible text on a swatch.</summary>
    public static double Luminance(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 6) return 0.5;
        var clean = new string(hex.Where(char.IsLetterOrDigit).ToArray()).Take(6).ToArray();
        if (clean.Length < 6 || !ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return 0.5;
        double r = ((value >> 16) & 0xFF) / 255.0, g = ((value >> 8) & 0xFF) / 255.0, b = (value & 0xFF) / 255.0;
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
}

/// <summary>Text formatting shared by the pages (port of the macOS <c>Format</c> enum).</summary>
public static class Format
{
    /// <summary>"2h 05m", "45m", "&lt;1m".</summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return "<1m";
        int h = minutes / 60, m = minutes % 60;
        if (h == 0) return $"{m}m";
        if (h >= 24) return $"{h / 24}d {h % 24}h";
        return $"{h}h {m:00}m";
    }

    public static string Duration(TimeSpan ts) => Duration((int)Math.Round(ts.TotalMinutes));

    /// <summary>"Done 14:32", "Done tomorrow 08:10", "Done Wed 14:32".</summary>
    public static string Finish(DateTime date, DateTime? now = null)
    {
        now ??= DateTime.Now;
        string time = date.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (date.Date == now.Value.Date) return time;
        if (date.Date == now.Value.Date.AddDays(1)) return $"tomorrow {time}";
        return date.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"3s ago", "2m ago", "1h 05m ago", "yesterday 14:32".</summary>
    public static string RelativeTime(DateTime date, DateTime? now = null)
    {
        now ??= DateTime.Now;
        var delta = now.Value - date;
        if (delta.TotalSeconds < 5) return "just now";
        if (delta.TotalMinutes < 1) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h {(int)delta.TotalMinutes % 60:00}m ago";
        if (date.Date == now.Value.Date.AddDays(-1)) return "yesterday " + date.ToString("HH:mm", CultureInfo.InvariantCulture);
        return date.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Temperature(double value) => $"{(int)Math.Round(value)}°";

    /// <summary>The macOS "WiFi.describe": "Good"/"Fair"/"Weak" by dBm.</summary>
    public static string Wifi(int dbm) => dbm switch
    {
        >= -55 => "Good",
        >= -70 => "Fair",
        _ => "Weak",
    };

    /// <summary>"HS01" → "Hardened steel", "HH01" → "Hardened steel, high flow"…</summary>
    public static string NozzleType(string code)
    {
        string upper = code.ToUpperInvariant();
        if (upper.Length < 4)
            return upper.Contains("HARDENED") ? "Hardened steel"
                 : upper.Contains("STAINLESS") ? "Stainless steel"
                 : code;
        string material = upper[2..4] switch
        {
            "00" => "Stainless steel",
            "01" => "Hardened steel",
            "05" => "Tungsten carbide",
            _ => code,
        };
        return upper[1] switch
        {
            'U' => material + ", TPU high flow",
            'H' or 'E' => material + ", high flow",
            _ => material,
        };
    }
}

/// <summary>
/// Small WPF building blocks shared by every page: the rounded "panel" card, section labels and
/// detail rows — the visual language of the macOS original.
/// </summary>
public static class Ui
{
    // Theme-aware: these resolve against the current light/dark palette at build time.
    private static Brush SecondaryText => ThemeEngine.Current.SecondaryBrush;
    private static Brush Hairline => ThemeEngine.Current.HairlineBrush;
    private static readonly System.Diagnostics.Stopwatch SharedClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>A rounded card: a faint fill and hairline, like Control Center's modules.</summary>
    public static Border Panel(UIElement content, double radius = 18, double padding = 14, Color? tint = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Background = new SolidColorBrush(tint is { } t
                ? Color.FromArgb(0x1F, t.R, t.G, t.B)
                : ThemeEngine.Current.Neutral(0x0D)),
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0.75),
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = ThemeEngine.Current.ShadowOpacity,
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
            },
        };
        return border;
    }

    /// <summary>A card with a colored accent stripe on the left edge.</summary>
    public static Border AccentPanel(UIElement content, Color accent, double radius = 18, double padding = 14)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stripe = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(radius, 0, 0, radius),
            Margin = new Thickness(0, 0, 0, 0),
        };
        Grid.SetColumn(stripe, 0);
        grid.Children.Add(stripe);

        ((FrameworkElement)content).Margin = new Thickness(padding - 8, padding, padding, padding);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x0A)),
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0.75),
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = ThemeEngine.Current.ShadowOpacity,
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
            },
        };
        return outer;
    }

    /// <summary>Small uppercase heading above a group.</summary>
    public static TextBlock SectionLabel(string title) => new()
    {
        Text = title.ToUpperInvariant(),
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = SecondaryText,
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>"Speed — Silent (100%)" style row used in the printer details.</summary>
    public static Grid DetailRow(string title, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 4.5, 0, 4.5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = title, FontSize = 12, Foreground = SecondaryText, VerticalAlignment = VerticalAlignment.Center };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(label);
        grid.Children.Add(valueText);
        return grid;
    }

    public static TextBlock Body(string text, double size = 12.5) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = SecondaryText,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A thin horizontal gauge (capsule), like the temperature and fan bars.</summary>
    public static Border Gauge(double fraction, Brush fill, double height = 4, byte trackAlpha = 0x22, Brush? trackOverride = null)
    {
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = trackOverride ?? new SolidColorBrush(ThemeEngine.Current.Neutral(trackAlpha)),
            ClipToBounds = true,
            Child = new Border
            {
                Width = 0,
                Height = height,
                CornerRadius = new CornerRadius(height / 2),
                Background = fill,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        var inner = (Border)track.Child;
        // Fraction is applied once the track has a real width (via a size-changed handler).
        track.SizeChanged += (_, _) => inner.Width = Math.Max(0, track.ActualWidth * Math.Clamp(fraction, 0, 1));
        inner.Width = Math.Max(0, track.ActualWidth * Math.Clamp(fraction, 0, 1));
        return track;
    }

    /// <summary>
    /// An indeterminate capsule bar: a rounded runner gliding across the track. Used while the
    /// printer loads the 3MF and slices it — a phase where no numeric progress is reported.
    /// The phase comes from a shared clock, so a rebuilt bar picks up where the previous one left
    /// off instead of visibly restarting.
    /// </summary>
    public static Border Indeterminate(Brush fill, double height = 4)
    {
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x22)),
            ClipToBounds = true,
        };
        var runner = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var move = new TranslateTransform();
        runner.RenderTransform = move;
        track.Child = runner;

        const double secondsPerLoop = 1.4;
        bool started = false;
        void Start()
        {
            if (started || track.ActualWidth <= 0) return;
            started = true;
            double width = Math.Max(48, track.ActualWidth * 0.32);
            runner.Width = width;
            var anim = new DoubleAnimation
            {
                From = -width,
                To = track.ActualWidth,
                Duration = new Duration(TimeSpan.FromSeconds(secondsPerLoop)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            // Negative begin time: the loop is already mid-flight, continuous across rebuilds.
            anim.BeginTime = TimeSpan.FromSeconds(-(SharedClock.Elapsed.TotalSeconds % secondsPerLoop));
            move.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        track.Loaded += (_, _) => Start();
        track.SizeChanged += (_, _) => Start();
        return track;
    }

    public static Brush Brush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
