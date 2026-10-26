using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BambuBar.Core.App;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>
/// Every loaded spool, large, with material, colour, what's left and the AMS climate
/// (port of the macOS <c>FilamentPage</c>).
/// </summary>
public sealed class FilamentPage
{
    private readonly PageContext _ctx;
    public FilamentPage(PageContext ctx) => _ctx = ctx;

    public FrameworkElement Build()
    {
        var store = _ctx.Store;
        var session = store.Current;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 24),
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        root.Children.Add(PageKit.Header("Filament", Subtitle(online ? status : null)));

        if (online && status is not null)
        {
            bool anySpool = status.Ams.Count > 0 || status.ExternalSpool is { IsLoaded: true };
            if (!anySpool)
            {
                root.Children.Add(Empty("No AMS connected",
                    "Spools loaded in an AMS or on the external holder appear here."));
            }

            var active = status.ActiveSpool;
            foreach (var unit in status.Ams)
            {
                var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock
                {
                    Text = unit.Title,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeEngine.Current.PrimaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(title, 0);
                head.Children.Add(title);
                var climate = Climate(unit);
                Grid.SetColumn(climate, 1);
                head.Children.Add(climate);
                block.Children.Add(head);

                if (unit.Spools.Count > 0)
                {
                    var grid = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                    foreach (var spool in unit.Spools)
                        grid.Children.Add(SpoolCard(spool, active is { Id: int id } && id == spool.Id && status.State.IsActive()));
                    block.Children.Add(grid);
                }
                root.Children.Add(block);
            }

            if (status.ExternalSpool is { IsLoaded: true } ext)
            {
                var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
                block.Children.Add(new TextBlock { Text = "External spool", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush });
                var grid = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                grid.Children.Add(SpoolCard(ext, active is { Id: int id } && id == ext.Id && status.State.IsActive()));
                block.Children.Add(grid);
                root.Children.Add(block);
            }
        }
        else
        {
            root.Children.Add(Empty(
                store.Sessions.Count > 0 ? "Printer offline" : "No printer set up",
                store.Sessions.Count > 0
                    ? "Filament appears once Bambu Bar is connected."
                    : "Set up your printer on the Printer page."));
        }

        return root;
    }

    private static string Subtitle(PrinterStatus? status)
    {
        if (status is null) return "Spools in your AMS and on the external holder";
        int loaded = status.AllSpools.Count(s => s.IsLoaded);
        if (status.ActiveSpool is { } active && status.State.IsActive())
            return $"{loaded} loaded · printing with {active.Label} {active.DisplayName}";
        return $"{loaded} spool{(loaded == 1 ? "" : "s")} loaded";
    }

    /// <summary>Drying, humidity and temperature of an AMS unit, the way the macOS page shows them.</summary>
    private static UIElement Climate(AmsUnit unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool first = true;
        void Add(string text, Color? tint)
        {
            if (!first)
            {
                var dot = new Border
                {
                    Width = 3,
                    Height = 3,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x44)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                };
                row.Children.Add(dot);
            }
            first = false;
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = Ui.Brush(tint ?? Palette.Idle),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        if (unit.DryingMinutesLeft > 0)
            Add($"Drying · {Format.Duration(unit.DryingMinutesLeft)} left", Palette.Bed);
        if (unit.Humidity is { } h)
            Add($"{h}% humidity", h > 40 ? Palette.Paused : (Color?)null);
        else if (unit.HumidityLevel is { } level)
            Add($"Humidity {level} of 5", level >= 4 ? Palette.Paused : (Color?)null);
        if (unit.Temperature is { } t)
            Add(Format.Temperature(t), null);
        return row;
    }

    private UIElement SpoolCard(Spool spool, bool active)
    {
        var content = new StackPanel();

        // Swatch + slot label, with the "in use" mark.
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var swatch = Swatch(spool, active);
        Grid.SetColumn(swatch, 0);
        head.Children.Add(swatch);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x14)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock { Text = spool.Label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = ThemeEngine.Current.PrimaryBrush },
        });
        if (active)
            right.Children.Add(new TextBlock
            {
                Text = "In use",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Brush(Palette.AccentDeep),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 3, 0, 0),
            });
        Grid.SetColumn(right, 1);
        head.Children.Add(right);
        content.Children.Add(head);

        content.Children.Add(new TextBlock
        {
            Text = spool.IsLoaded ? spool.DisplayName : "Empty slot",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = spool.IsLoaded ? ThemeEngine.Current.PrimaryBrush : Ui.Brush(Palette.Idle),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new TextBlock
        {
            Text = spool.IsLoaded ? Detail(spool) : "Load a spool to see it here",
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (spool.IsLoaded)
        {
            var remain = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pct = spool.Remain;
            var label = new TextBlock { Text = "Remaining", FontSize = 10.5, Foreground = Ui.Brush(Palette.Idle) };
            var value = new TextBlock
            {
                Text = pct is { } r ? $"{r}%" : "Unknown",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeEngine.Current.PrimaryBrush,
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            row.Children.Add(label);
            row.Children.Add(value);
            remain.Children.Add(row);

            Color? tint = Palette.Filament(spool.Color);
            var bar = Ui.Gauge(pct is { } p ? p / 100.0 : 0,
                Ui.Brush(tint is { } c ? c : Palette.Idle), height: 5);
            if (spool.Remain is null) bar.Opacity = 0.4;
            bar.Margin = new Thickness(0, 5, 0, 0);
            remain.Children.Add(bar);
            content.Children.Add(remain);
        }

        var panel = Ui.Panel(content, radius: 18, padding: 14, tint: active ? Palette.Accent : null);
        if (active)
        {
            panel.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B));
            panel.BorderThickness = new Thickness(1.2);
        }
        panel.Width = 186;
        panel.Margin = new Thickness(0, 0, 12, 12);
        return panel;
    }

    /// <summary>"PLA · #E11D2E · Bambu RFID" / third-party.</summary>
    private static string Detail(Spool spool)
    {
        var parts = new List<string>();
        if (spool.Material.Length > 0) parts.Add(spool.Material);
        var clean = new string(spool.Color.Where(char.IsLetterOrDigit).ToArray()).Take(6).ToArray();
        if (clean.Length == 6) parts.Add("#" + string.Join("", clean));
        parts.Add(spool.IsBambu ? "Bambu RFID" : "Third-party");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A filament spool seen from the side (port of the macOS <c>SpoolSwatch</c>): the colour, a hub,
    /// and a ring showing how much is left.
    /// </summary>
    private static UIElement Swatch(Spool spool, bool active)
    {
        const double size = 58;
        double ringWidth = Math.Max(2, size * 0.075);
        double gap = Math.Max(1.5, size * 0.06);

        var root = new Grid { Width = size, Height = size };
        double center = size / 2;
        double outerR = center - ringWidth / 2 - 0.5;

        // Base ring.
        root.Children.Add(new Ellipse
        {
            Width = outerR * 2,
            Height = outerR * 2,
            Stroke = new SolidColorBrush(ThemeEngine.Current.Neutral(0x1A)),
            StrokeThickness = ringWidth,
        });

        Color? color = spool.IsLoaded ? Palette.Filament(spool.Color) : null;

        // Remaining ring.
        if (spool.Remain is { } remain && remain > 0 && color is not null)
        {
            root.Children.Add(RingArc(center, outerR, remain / 100.0, ringWidth,
                active ? Ui.Brush(Palette.Accent) : new SolidColorBrush(ThemeEngine.Current.Neutral(0x8C))));
        }
        else if (active)
        {
            root.Children.Add(new Ellipse
            {
                Width = outerR * 2,
                Height = outerR * 2,
                Stroke = Ui.Brush(Palette.Accent),
                StrokeThickness = ringWidth,
            });
        }

        // Fill.
        double innerR = center - ringWidth - gap;
        if (color is { } c)
        {
            var colors = spool.Colors
                .Select(Palette.Filament)
                .Where(x => x is not null)
                .Select(x => x.Value)
                .Distinct()
                .ToList();
            Brush fill;
            if (colors.Count > 1)
            {
                var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                for (int i = 0; i < colors.Count; i++)
                    gradient.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Count - 1)));
                gradient.Freeze();
                fill = gradient;
            }
            else
            {
                fill = Ui.Brush(c);
            }
            var inner = new Ellipse { Width = innerR * 2, Height = innerR * 2, Fill = fill };
            inner.Stroke = new SolidColorBrush(Color.FromArgb(
                Palette.Luminance(spool.Color) > 0.85 ? (byte)0x2E : (byte)0x0F, 0, 0, 0));
            inner.StrokeThickness = 0.75;
            root.Children.Add(inner);

            // Hub.
            double hub = size * 0.2;
            root.Children.Add(new Ellipse
            {
                Width = hub,
                Height = hub,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(ThemeEngine.Current.Neutral(0x8C)),
            });
        }
        else
        {
            // Empty slot: a dashed disc, like the macOS original.
            var dashes = new Ellipse
            {
                Width = innerR * 2,
                Height = innerR * 2,
                Stroke = new SolidColorBrush(ThemeEngine.Current.Neutral(0x38)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2.5, 2.5 },
            };
            root.Children.Add(dashes);
        }
        return root;
    }

    /// <summary>A clockwise arc starting at 12 o'clock, <paramref name="fraction"/> of the circle.</summary>
    private static Path RingArc(double center, double radius, double fraction, double thickness, Brush stroke)
    {
        fraction = Math.Clamp(fraction, 0, 0.999);
        double angle = fraction * 2 * Math.PI;
        var start = new Point(center, center - radius);
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, isLargeArc: fraction > 0.5,
            sweepDirection: SweepDirection.Clockwise, isStroked: true));
        return new Path
        {
            Data = new PathGeometry { Figures = { figure } },
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private UIElement Empty(string title, string detail)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 50, 0, 50),
        };
        content.Children.Add(new TextBlock
        {
            Text = "◎",
            FontSize = 34,
            Foreground = new SolidColorBrush(ThemeEngine.Current.Neutral(0x33)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = Ui.Brush(Palette.Idle),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return Ui.Panel(content, radius: 18, padding: 16);
    }
}
