using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core.App;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>The main page: the job up front, then temperatures, filament, machine details and issues.</summary>
public sealed class OverviewPage
{
    private readonly PageContext _ctx;
    public OverviewPage(PageContext ctx) => _ctx = ctx;

    public FrameworkElement Build()
    {
        var store = _ctx.Store;
        var session = store.Current;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Center };

        string subtitle = "";
        if (session?.Config is { } config)
        {
            var parts = new List<string> { "Bambu Lab " + config.ModelName, config.Host };
            if (status?.Firmware is { Length: > 0 } fw) parts.Add("Firmware " + fw);
            subtitle = string.Join(" · ", parts);
        }
        root.Children.Add(PageKit.Header(session?.Config.Name ?? "Bambu Bar", subtitle, PageKit.Icon("⟳", "Refresh", () => store.Refresh())));

        if (online && status is not null)
        {
            root.Children.Add(JobHero(status));
            if (status.State.IsActive())
            {
                root.Children.Add(PrintControls(status));
                if (status.ControlsLocked)
                {
                    root.Children.Add(Ui.Panel(new TextBlock
                    {
                        Text = "The printer only accepts pause, stop and speed changes in LAN Only mode with Developer Mode on (printer screen → Settings → Network). Status and the chamber light work either way.",
                        FontSize = 11.5,
                        Foreground = Ui.Brush(Palette.Idle),
                        TextWrapping = TextWrapping.Wrap,
                    }, radius: 14, padding: 12));
                }
            }
            foreach (var issue in status.Issues)
                root.Children.Add(IssueBanner(issue));
            root.Children.Add(Metrics(status));
            if (status.Ams.Count > 0 || status.ExternalSpool is { IsLoaded: true })
            {
                var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
                block.Children.Add(Ui.SectionLabel("Filament"));
                block.Children.Add(FilamentStrip(status));
                root.Children.Add(block);
            }
            root.Children.Add(Machine(status));
        }
        else
        {
            root.Children.Add(ConnectionCard(session));
        }

        return root;
    }

    private UIElement JobHero(PrinterStatus status)
    {
        var color = status.State == JobState.Paused ? Palette.Paused
            : status.State == JobState.Failed ? (status.Canceled ? Palette.Idle : Palette.Failed)
            : status.State == JobState.Finished ? Palette.Finished
            : Palette.ForState(status.State);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        var pill = new TextBlock
        {
            Text = status.StateTitle + (status.State == JobState.Printing && status.Speed != SpeedLevel.Standard ? $" · {status.Speed.Title()}" : "")
                 + (status.State == JobState.Idle && status.DoorOpen is true ? " · Door open" : ""),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush(color),
        };
        panel.Children.Add(pill);

        panel.Children.Add(new TextBlock
        {
            Text = status.JobName.Length > 0 ? status.JobName : "Print",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = status.DetailText,
            FontSize = 13,
            Foreground = Ui.Brush(Palette.Idle),
            Margin = new Thickness(0, 3, 0, 12),
        });

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var percent = new TextBlock { Text = $"{status.Progress}%", FontSize = 30, FontWeight = FontWeights.Bold };
        var bar = Ui.Gauge(status.Progress / 100.0, Ui.Brush(color), height: 8);
        var timing = new TextBlock
        {
            Text = status.RemainingMinutes > 0
                ? $"~{Format.Duration(status.RemainingMinutes)} left"
                : status.EstimatedEnd is { } end ? $"ends {Format.Finish(end)}" : "",
            FontSize = 12.5,
            Foreground = Ui.Brush(Palette.Idle),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(percent, 0);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(timing, 2);
        row.Children.Add(percent);
        row.Children.Add(new Grid { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { bar } });
        row.Children.Add(timing);
        panel.Children.Add(row);

        // Layer + filament in use.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (status.TotalLayers > 0)
            meta.Children.Add(MetaChip($"Layer {status.Layer} of {status.TotalLayers}"));
        if (status.ActiveSpool is { IsLoaded: true } spool)
            meta.Children.Add(MetaChip($"{spool.DisplayName} · {spool.Label}"));
        if (meta.Children.Count > 0)
        {
            for (int i = 0; i < meta.Children.Count; i++)
                if (meta.Children[i] is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(meta);
        }

        return Ui.Panel(panel, radius: 26, padding: 22);
    }

    private static UIElement MetaChip(string text) => new Border
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10, 3, 10, 3),
        Background = new SolidColorBrush(Color.FromArgb(0x0F, 0, 0, 0)),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)) },
    };

    private UIElement PrintControls(PrinterStatus status)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16), MaxWidth = 620 };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        if (status.State == JobState.Paused)
        {
            row.Children.Add(PageKit.Primary("Resume", () => _ctx.Store.Resume(), width: 130));
        }
        else if (status.State.IsActive())
        {
            row.Children.Add(PageKit.Primary(status.ControlsLocked ? "Pause (locked)" : "Pause", () => _ctx.Store.Pause(), width: 130));
        }
        if (status.State.IsActive())
        {
            row.Children.Add(PageKit.Danger(status.ControlsLocked ? "Stop (locked)" : "Stop", () =>
            {
                if (_ctx.Store.Settings.ConfirmStop && !PageKit.Confirm("Stop the print? The printer will be told to stop the current job."))
                    return;
                _ctx.Store.StopPrint();
            }));
        }
        row.Children.Add(PageKit.Secondary(status.ChamberLightOn is true ? "Chamber light off" : "Chamber light on", () => _ctx.Store.ToggleLight()));
        panel.Children.Add(row);

        var speedRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var level in new[] { SpeedLevel.Silent, SpeedLevel.Standard, SpeedLevel.Sport, SpeedLevel.Ludicrous })
        {
            bool selected = status.Speed == level;
            var button = new Button
            {
                Content = $"{level.Title()} ({level.Percent()}%)",
                FontSize = 11.5,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Background = selected ? Ui.Brush(Palette.AccentDeep) : new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            button.Click += (_, _) => _ctx.Store.SetSpeed(level);
            speedRow.Children.Add(button);
        }
        panel.Children.Add(speedRow);
        return panel;
    }

    private UIElement IssueBanner(PrinterIssue issue)
    {
        var color = issue.Severity switch
        {
            IssueSeverity.Fatal => Palette.Failed,
            IssueSeverity.Serious => Palette.Paused,
            _ => Palette.Preparing,
        };
        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        content.Children.Add(new TextBlock
        {
            Text = issue.Severity.Title() + (issue.Id.Length > 0 ? $"  ·  {issue.Id}" : ""),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush(color),
            Margin = new Thickness(0, 0, 0, 3),
        });
        content.Children.Add(new TextBlock { Text = issue.Message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        return Ui.Panel(content, radius: 14, padding: 12, tint: color);
    }

    private UIElement Metrics(PrinterStatus status)
    {
        var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        block.Children.Add(Ui.SectionLabel("Temperatures & airflow"));
        var grid = new Grid();
        for (int i = 0; i < 4; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nozzle = TempTile("Nozzle", status.Nozzle.Current, status.Nozzle.Target, Palette.Nozzle, 300);
        Grid.SetColumn(nozzle, 0);
        grid.Children.Add(nozzle);

        var bedTile = TempTile("Bed", status.Bed.Current, status.Bed.Target, Palette.Bed, 110);
        if (bedTile is FrameworkElement be) be.Margin = new Thickness(5, 0, 5, 0);
        Grid.SetColumn(bedTile, 1);
        grid.Children.Add(bedTile);

        var chamber = TempTile("Chamber", status.Chamber, null, Palette.Chamber, 65, readOnly: true);
        Grid.SetColumn(chamber, 2);
        grid.Children.Add(chamber);

        var fan = FanTile(status);
        Grid.SetColumn(fan, 3);
        grid.Children.Add(fan);

        return block;
    }

    private static UIElement TempTile(string title, double? current, double? target, Color tint, double max, bool readOnly = false)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = Ui.Brush(Palette.Idle) });
        content.Children.Add(new TextBlock
        {
            Text = current is { } c ? $"{(int)Math.Round(c)}°" : "–",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0),
        });
        string sub = readOnly ? "Read only"
            : target is { } t && t > 0
                ? (Math.Abs((current ?? 0) - t) <= 2 ? $"At {t:0}°" : (current ?? 0) < t ? $"Heating to {t:0}°" : $"Cooling to {t:0}°")
                : "Off";
        content.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 1, 0, 6) });
        content.Children.Add(Ui.Gauge(
            (current ?? 0) / max,
            Ui.Brush(readOnly ? Palette.Chamber : (target is { } tt && tt > 0 ? tint : Color.FromArgb(0x4D, 0, 0, 0))),
            height: 4));
        return Ui.Panel(content, radius: 14, padding: 11);
    }

    private static UIElement FanTile(PrinterStatus status)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Fans", FontSize = 11, Foreground = Ui.Brush(Palette.Idle) });
        content.Children.Add(FanRow("Part", status.PartFan));
        content.Children.Add(FanRow("Aux", status.AuxFan));
        if (status.ExhaustFan is { } exhaust) content.Children.Add(FanRow("Exhaust", exhaust));
        else if (status.HeatbreakFan is { } hotend) content.Children.Add(FanRow("Hotend", hotend));
        return Ui.Panel(content, radius: 14, padding: 11);
    }

    private static UIElement FanRow(string title, int? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var label = new TextBlock { Text = title, FontSize = 10.5, Foreground = Ui.Brush(Palette.Idle) };
        var bar = Ui.Gauge(value is { } v ? v / 100.0 : 0, Ui.Brush(Palette.Chamber), height: 4);
        var valueText = new TextBlock
        {
            Text = value is { } vv ? $"{vv}%" : "–",
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(label);
        grid.Children.Add(new Grid { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { bar } });
        grid.Children.Add(valueText);
        return grid;
    }

    private static UIElement FilamentStrip(PrinterStatus status)
    {
        var wrap = new WrapPanel();
        var active = status.ActiveSpool;
        foreach (var spool in status.AllSpools)
        {
            Color? color = Palette.Filament(spool.Color);
            var card = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            card.Children.Add(new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Background = color is { } c ? Ui.Brush(c) : new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
                BorderBrush = spool.IsLoaded ? new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)) : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 5),
            });
            card.Children.Add(new TextBlock
            {
                Text = spool.IsLoaded ? spool.DisplayName : "Empty",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
            });
            card.Children.Add(new TextBlock
            {
                Text = $"{spool.Label} · {spool.RemainText}",
                FontSize = 10.5,
                Foreground = active is { Id: int id } && id == spool.Id ? Ui.Brush(Palette.AccentDeep) : Ui.Brush(Palette.Idle),
            });
            wrap.Children.Add(card);
        }
        return wrap;
    }

    private UIElement Machine(PrinterStatus status)
    {
        var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        block.Children.Add(Ui.SectionLabel("Printer"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        left.Children.Add(Ui.DetailRow("Speed", $"{status.Speed.Title()} ({status.Speed.Percent()}%)"));
        left.Children.Add(Ui.DetailRow("Nozzle", NozzleText(status)));
        left.Children.Add(Ui.DetailRow("Door", status.DoorOpen is true ? "Open" : status.DoorOpen is false ? "Closed" : "—"));
        left.Children.Add(Ui.DetailRow("Chamber light", status.ChamberLightOn is true ? "On" : status.ChamberLightOn is false ? "Off" : "—"));
        var leftPanel = Ui.Panel(left, radius: 16, padding: 13);

        var right = new StackPanel();
        right.Children.Add(Ui.DetailRow("Network", status.WifiSignal is { } w ? $"{Format.Wifi(w)} ({w} dBm)" : "—"));
        right.Children.Add(Ui.DetailRow("Storage", status.HasSDCard is true ? "SD card inserted" : status.HasSDCard is false ? "No SD card" : "—"));
        right.Children.Add(Ui.DetailRow("Live View", status.LiveViewEnabled is true ? "On" : status.LiveViewEnabled is false ? "Off" : "—"));
        right.Children.Add(Ui.DetailRow("Firmware", (status.Firmware ?? "—") + (status.UpdateAvailable ? " · Update available" : "")));
        var rightPanel = Ui.Panel(right, radius: 16, padding: 13);
        rightPanel.Margin = new Thickness(10, 0, 0, 0);

        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);
        block.Children.Add(grid);
        return block;
    }

    private static string NozzleText(PrinterStatus status)
    {
        var parts = new List<string>();
        if (status.NozzleDiameter is { } d) parts.Add($"{d:0.0} mm");
        if (status.NozzleType is { Length: > 0 } t) parts.Add(Format.NozzleType(t));
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private UIElement ConnectionCard(PrinterSession? session)
    {
        var content = new StackPanel { Margin = new Thickness(0, 30, 0, 30) };
        content.Children.Add(new TextBlock
        {
            Text = session is null ? "No printer set up" : session.CurrentPhase.ToString(),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = session?.PhaseReason
                ?? (session is null
                    ? "Add your printer to see its progress, temperatures and filament here."
                    : "The printer isn't reachable on the network."),
            FontSize = 13,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 14),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (session is not null)
            buttons.Children.Add(PageKit.Primary("Retry", () => _ctx.Store.Refresh(), width: 120));
        else
            buttons.Children.Add(PageKit.Primary("Set Up…", () => _ctx.Window.BeginAddPrinter(), width: 140));
        content.Children.Add(buttons);
        return Ui.Panel(content, radius: 20, padding: 18);
    }
}
