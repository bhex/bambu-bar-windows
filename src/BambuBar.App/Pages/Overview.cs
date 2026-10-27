using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core.App;
using BambuBar.Core.I18n;
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
            if (status?.Firmware is { Length: > 0 } fw) parts.Add(Loc.Tf("ov.firmware", fw));
            subtitle = string.Join(" · ", parts);
        }
        root.Children.Add(PageKit.Header(session?.Config.Name ?? "Bambu Bar", subtitle, PageKit.Icon("⟳", Loc.T("btn.refresh"), () => store.Refresh())));

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
                        Text = Loc.T("ov.controls_locked"),
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
                block.Children.Add(Ui.SectionLabel(Loc.T("filament.title")));
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
                 + (status.State == JobState.Idle && status.DoorOpen is true ? " · " + Loc.T("ov.door_open") : ""),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush(color),
        };
        panel.Children.Add(pill);

        panel.Children.Add(new TextBlock
        {
            Text = status.JobName.Length > 0 ? status.JobName : Loc.T("job.default_name"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
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
        bool indeterminate = status.State == JobState.Preparing && status.Progress < 2;
        var percent = new TextBlock { Text = indeterminate ? "…" : $"{status.Progress}%", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = ThemeEngine.Current.PrimaryBrush };
        var bar = indeterminate
            ? Ui.Indeterminate(Ui.Brush(color), height: 8)
            : Ui.Gauge(status.Progress / 100.0, Ui.Brush(color), height: 8);
        var timing = new TextBlock
        {
            Text = status.RemainingMinutes > 0
                ? Loc.Tf("panel.left", Format.Duration(status.RemainingMinutes))
                : status.EstimatedEnd is { } end ? Loc.Tf("panel.ends", Format.Finish(end)) : "",
            FontSize = 12.5,
            Foreground = Ui.Brush(Palette.Idle),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(percent, 0);
        Grid.SetColumn(timing, 2);
        row.Children.Add(percent);
        var barHost = new Grid { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { bar } };
        Grid.SetColumn(barHost, 1);
        row.Children.Add(barHost);
        row.Children.Add(timing);
        panel.Children.Add(row);

        // Layer + filament in use.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (status.TotalLayers > 0)
            meta.Children.Add(MetaChip(Loc.Tf("detail.layer_of", status.Layer, status.TotalLayers)));
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
        Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x0F)),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = ThemeEngine.Current.NavTextBrush },
    };

    private UIElement PrintControls(PrinterStatus status)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16), MaxWidth = 620 };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        if (status.State == JobState.Paused)
        {
            row.Children.Add(PageKit.Primary(Loc.T("btn.resume"), () => _ctx.Store.Resume(), width: 130));
        }
        else if (status.State.IsActive())
        {
            row.Children.Add(PageKit.Primary(status.ControlsLocked ? Loc.T("btn.pause_locked") : Loc.T("btn.pause"), () => _ctx.Store.Pause(), width: 130));
        }
        if (status.State.IsActive())
        {
            row.Children.Add(PageKit.Danger(status.ControlsLocked ? Loc.T("btn.stop_locked") : Loc.T("btn.stop"), () =>
            {
                if (_ctx.Store.Settings.ConfirmStop && !PageKit.Confirm(Loc.T("confirm.stop")))
                    return;
                _ctx.Store.StopPrint();
            }));
        }
        row.Children.Add(PageKit.Secondary(status.ChamberLightOn is true ? Loc.T("btn.light_chamber_off") : Loc.T("btn.light_chamber_on"), () => _ctx.Store.ToggleLight()));
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
                Foreground = selected ? Brushes.White : ThemeEngine.Current.NavTextBrush,
                Background = selected ? Ui.Brush(Palette.AccentDeep) : new SolidColorBrush(ThemeEngine.Current.Neutral(0x14)),
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
        content.Children.Add(new TextBlock { Text = issue.Message, FontSize = 12.5, Foreground = ThemeEngine.Current.PrimaryBrush, TextWrapping = TextWrapping.Wrap });
        return Ui.Panel(content, radius: 14, padding: 12, tint: color);
    }

    private UIElement Metrics(PrinterStatus status)
    {
        var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        block.Children.Add(Ui.SectionLabel(Loc.T("section.temps")));
        var grid = new Grid();
        for (int i = 0; i < 4; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nozzle = TempTile(Loc.T("tile.nozzle"), status.Nozzle.Current, status.Nozzle.Target, Palette.Nozzle, 300);
        Grid.SetColumn(nozzle, 0);
        grid.Children.Add(nozzle);

        var bedTile = TempTile(Loc.T("tile.bed"), status.Bed.Current, status.Bed.Target, Palette.Bed, 110);
        if (bedTile is FrameworkElement be) be.Margin = new Thickness(5, 0, 5, 0);
        Grid.SetColumn(bedTile, 1);
        grid.Children.Add(bedTile);

        var chamber = TempTile(Loc.T("tile.chamber"), status.Chamber, null, Palette.Chamber, 65, readOnly: true);
        Grid.SetColumn(chamber, 2);
        grid.Children.Add(chamber);

        var fan = FanTile(status);
        Grid.SetColumn(fan, 3);
        grid.Children.Add(fan);

        block.Children.Add(grid);
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
            Foreground = ThemeEngine.Current.PrimaryBrush,
            Margin = new Thickness(0, 3, 0, 0),
        });
        string sub = readOnly
            ? (current is null ? Loc.T("tile.unavailable") : Loc.T("tile.readonly"))
            : target is { } t && t > 0
                ? (Math.Abs((current ?? 0) - t) <= 2 ? Loc.Tf("tile.at", (int)Math.Round(t)) : (current ?? 0) < t ? Loc.Tf("tile.heating", (int)Math.Round(t)) : Loc.Tf("tile.cooling", (int)Math.Round(t)))
                : Loc.T("tile.off");
        content.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 1, 0, 6) });
        content.Children.Add(Ui.Gauge(
            (current ?? 0) / max,
            Ui.Brush(readOnly ? Palette.Chamber : (target is { } tt && tt > 0 ? tint : ThemeEngine.Current.Neutral(0x4D))),
            height: 4));
        return Ui.Panel(content, radius: 14, padding: 11);
    }

    private static UIElement FanTile(PrinterStatus status)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = Loc.T("tile.fans"), FontSize = 11, Foreground = Ui.Brush(Palette.Idle) });
        content.Children.Add(FanRow(Loc.T("fan.part"), status.PartFan));
        content.Children.Add(FanRow(Loc.T("fan.aux"), status.AuxFan));
        if (status.ExhaustFan is { } exhaust) content.Children.Add(FanRow(Loc.T("fan.exhaust"), exhaust));
        else if (status.HeatbreakFan is { } hotend) content.Children.Add(FanRow(Loc.T("fan.hotend"), hotend));
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
            Foreground = ThemeEngine.Current.PrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(label);
        var barHost = new Grid { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { bar } };
        Grid.SetColumn(barHost, 1);
        grid.Children.Add(barHost);
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
                Background = color is { } c ? Ui.Brush(c) : new SolidColorBrush(ThemeEngine.Current.Neutral(0x22)),
                BorderBrush = spool.IsLoaded ? new SolidColorBrush(ThemeEngine.Current.Neutral(0x33)) : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 5),
            });
            card.Children.Add(new TextBlock
            {
                Text = spool.IsLoaded ? spool.DisplayName : Loc.T("spool.empty"),
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeEngine.Current.PrimaryBrush,
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
        block.Children.Add(Ui.SectionLabel(Loc.T("section.printer")));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        left.Children.Add(Ui.DetailRow(Loc.T("row.speed"), $"{status.Speed.Title()} ({status.Speed.Percent()}%)"));
        left.Children.Add(Ui.DetailRow(Loc.T("row.nozzle"), NozzleText(status)));
        left.Children.Add(Ui.DetailRow(Loc.T("row.door"), status.DoorOpen is true ? Loc.T("door.open") : status.DoorOpen is false ? Loc.T("door.closed") : "—"));
        left.Children.Add(Ui.DetailRow(Loc.T("row.light"), status.ChamberLightOn is true ? Loc.T("toggle.on") : status.ChamberLightOn is false ? Loc.T("toggle.off") : "—"));
        var leftPanel = Ui.Panel(left, radius: 16, padding: 13);

        var right = new StackPanel();
        right.Children.Add(Ui.DetailRow(Loc.T("row.network"), status.WifiSignal is { } w ? $"{Format.Wifi(w)} ({w} dBm)" : "—"));
        right.Children.Add(Ui.DetailRow(Loc.T("row.storage"), status.HasSDCard is true ? Loc.T("storage.sd") : status.HasSDCard is false ? Loc.T("storage.no_sd") : "—"));
        right.Children.Add(Ui.DetailRow(Loc.T("row.liveview"), status.LiveViewEnabled is true ? Loc.T("toggle.on") : status.LiveViewEnabled is false ? Loc.T("toggle.off") : "—"));
        right.Children.Add(Ui.DetailRow(Loc.T("row.firmware"), (status.Firmware ?? "—") + (status.UpdateAvailable ? " · " + Loc.T("ov.update_available") : "")));
        var rightPanel = Ui.Panel(right, radius: 16, padding: 13);
        rightPanel.Margin = new Thickness(10, 0, 0, 0);

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);
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
            Text = session is null ? Loc.T("panel.no_printer") : PhaseTitle(session.CurrentPhase),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
        });
        content.Children.Add(new TextBlock
        {
            Text = session?.PhaseReason
                ?? (session is null
                    ? Loc.T("ov.add_printer")
                    : Loc.T("panel.unreachable")),
            FontSize = 13,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 14),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (session is not null)
            buttons.Children.Add(PageKit.Primary(Loc.T("btn.retry"), () => _ctx.Store.Refresh(), width: 120));
        else
            buttons.Children.Add(PageKit.Primary(Loc.T("btn.setup"), () => _ctx.Window.BeginAddPrinter(), width: 140));
        content.Children.Add(buttons);
        return Ui.Panel(content, radius: 20, padding: 18);
    }

    private static string PhaseTitle(PrinterSession.Phase phase) => phase switch
    {
        PrinterSession.Phase.Online => Loc.T("link.connected"),
        PrinterSession.Phase.Connecting => Loc.T("link.connecting"),
        PrinterSession.Phase.Offline => Loc.T("link.offline"),
        PrinterSession.Phase.Unauthorized => Loc.T("link.unauthorized"),
        PrinterSession.Phase.Certificate => Loc.T("link.certificate_title"),
        _ => phase.ToString(),
    };
}
