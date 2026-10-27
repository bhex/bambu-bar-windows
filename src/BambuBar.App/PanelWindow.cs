using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BambuBar.Core.App;
using BambuBar.Core.I18n;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>
/// The quick-glance panel under the tray icon (port of the macOS <c>MenuPanel</c>): the print at a
/// glance, common controls, and a way to open the full window. Closes when it loses focus, like a
/// menu bar extra.
/// </summary>
public sealed class PanelWindow : Window
{
    private readonly PrinterStore _store;
    private readonly Body _body;
    private readonly StackPanel _root;
    private readonly TextBlock _toast;
    private readonly DispatcherTimer _ticker;
    private string? _lastKey;

    public PanelWindow(PrinterStore store, Action openMainWindow, Action quit)
    {
        _store = store;
        _body = new Body(store, openMainWindow, quit);

        Title = "Bambu Bar";
        Width = 390;
        SizeToContent = SizeToContent.Manual;
        Height = 160; // placeholder until RelayoutHeight() pins the measured content height
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = ThemeEngine.Current.PanelBackground;
        SnapsToDevicePixels = true;
        Icon = TrayIcon.AppImage();
        ThemeEngine.Current.Changed += ApplyTheme;
        Loc.Changed += () => Dispatcher.BeginInvoke(() => Refresh(force: true));

        _root = new StackPanel { Margin = new Thickness(12) };
        _root.Children.Add(_body.Host);

        _toast = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 2, 0),
            Foreground = ThemeEngine.Current.SecondaryBrush,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_toast);

        _root.Children.Add(new TextBlock
        {
            Text = Loc.T("panel.hint_main"),
            FontSize = 10.5,
            Foreground = ThemeEngine.Current.TertiaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });

        Content = _root;

        _store.Changed += () => Dispatcher.BeginInvoke(() => Refresh());
        _store.Toasted += toast => Dispatcher.BeginInvoke(() => ShowToast(toast));

        // "Updated Xs ago" and ETAs need a heartbeat even when the printer is quiet.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ticker.Tick += (_, _) => Refresh(force: true);
        _ticker.Start();

        Deactivated += (_, _) => Hide();
        ContentRendered += (_, _) => PositionNearTray();
        SizeChanged += (_, _) => PositionNearTray();
    }

    private void ApplyTheme()
    {
        Background = ThemeEngine.Current.PanelBackground;
        Refresh(force: true);
    }

    private void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        double h = ActualHeight > 0 ? ActualHeight : 400;
        Left = area.Right - Width - 12;
        Top = Math.Max(0, area.Bottom - h - 12);
    }

    /// <summary>
    /// Pin the borderless window's height to its content. A transparent, chrome-less WPF window
    /// does not reliably re-run <c>SizeToContent</c> when content changes after first show, so the
    /// panel could clip its lower controls; measuring the content directly keeps it correct.
    /// </summary>
    private void RelayoutHeight()
    {
        double width = Width > 0 ? Width : 390;
        _root.Measure(new Size(width, double.PositiveInfinity));
        double desired = _root.DesiredSize.Height;
        if (desired > 1 && Math.Abs(desired - Height) > 0.5)
            Height = desired;
    }

    private void ShowToast(PrinterStore.Toast toast)
    {
        _toast.Text = toast.Message;
        _toast.Foreground = Ui.Brush(toast.IsError ? Palette.Failed : Palette.AccentDeep);
        _toast.Visibility = Visibility.Visible;
        RelayoutHeight();
    }

    public void Refresh(bool force = false)
    {
        var session = _store.Current;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;
        bool configured = _store.Sessions.Count > 0;

        string prepFlag = status is { State: JobState.Preparing, Progress: >= 2 } ? "p" : "-";
        string key = $"{configured}|{session?.Config.Serial}|{session?.CurrentPhase}|{status?.State}|{status?.Issues.Count}|{status?.ActiveSpoolId}|{prepFlag}";
        if (force || key != _lastKey)
        {
            _lastKey = key;
            _body.Rebuild(configured, online, session, status);
            RelayoutHeight();
        }
        else if (status is not null)
        {
            _body.Update(status);
        }
    }
}

/// <summary>
/// Builds and updates the panel body. Full rebuild on structural changes; the fast-changing bits
/// (progress, temperatures, "updated" time) are updated in place between rebuilds.
/// </summary>
public sealed class Body
{
    private readonly PrinterStore _store;
    private readonly Action _openMainWindow;
    private readonly Action _quit;
    private readonly ContentControl _host;

    private TextBlock? _jobName, _detail, _percent, _timing, _issueText, _updated;
    private Border? _progressBar;
    private bool _indeterminate;

    /// <summary>Uniform vertical gap between the panel's stacked sections.</summary>
    private const double SectionGap = 10;
    /// <summary>Uniform width for every primary / danger action button.</summary>
    private const double ActionButtonWidth = 110;

    public Body(PrinterStore store, Action openMainWindow, Action quit)
    {
        _store = store;
        _openMainWindow = openMainWindow;
        _quit = quit;
        _host = new ContentControl();
    }

    public ContentControl Host => _host;

    public void Rebuild(bool configured, bool online, PrinterSession? session, PrinterStatus? status)
    {
        var body = new StackPanel();
        body.Children.Add(Header(configured, session, online));

        if (!configured)
        {
            body.Children.Add(SetupPrompt());
        }
        else if (online && status is not null)
        {
            body.Children.Add(JobHero(status));
            if (status.MostSevereIssue is { Severity: IssueSeverity.Fatal or IssueSeverity.Serious or IssueSeverity.Common } issue)
                body.Children.Add(IssueBanner(issue));
            body.Children.Add(Temperatures(status));
            if (status.Ams.Count > 0 || status.ExternalSpool is { IsLoaded: true })
                body.Children.Add(FilamentStrip(status));
            if (status.State.IsActive())
                body.Children.Add(PrintControls(status));
            body.Children.Add(UpdatedLine(status));
        }
        else
        {
            body.Children.Add(ConnectionCard(session));
        }

        _host.Content = body;
    }

    /// <summary>In-place update for the ~1 Hz status stream.</summary>
    public void Update(PrinterStatus status)
    {
        if (_jobName is not null) _jobName.Text = status.JobName.Length > 0 ? status.JobName : Loc.T("job.default_name");
        if (_detail is not null) _detail.Text = status.DetailText;
        if (_percent is not null && !_indeterminate) _percent.Text = $"{status.Progress}%";
        if (_timing is not null)
        {
            _timing.Text = status.RemainingMinutes > 0
                ? Loc.Tf("panel.left", Format.Duration(status.RemainingMinutes))
                : (status.EstimatedEnd is { } end ? Loc.Tf("panel.ends", Format.Finish(end)) : "");
        }
        if (_progressBar is not null && !_indeterminate && _progressBar.Child is Border fill)
            fill.Width = Math.Max(0, _progressBar.ActualWidth * status.Progress / 100.0);
        if (_updated is not null)
            _updated.Text = Loc.Tf("panel.updated", Format.RelativeTime(status.UpdatedAt));
        if (_issueText is not null && status.MostSevereIssue is { } issue)
            _issueText.Text = issue.Message;
    }

    // MARK: - Pieces

    private UIElement Header(bool configured, PrinterSession? session, bool online)
    {
        var grid = new Grid { Margin = new Thickness(4, 2, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Vertical };
        left.Children.Add(new TextBlock
        {
            Text = configured ? (session?.Config.Name ?? "Bambu Bar") : "Bambu Bar",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
        });

        Color dot = online ? Palette.Accent
            : session is { CurrentPhase: PrinterSession.Phase.Connecting } ? Palette.Paused
            : configured ? Palette.Failed : Palette.Idle;

        string linkText;
        if (!configured) linkText = Loc.T("panel.setup_cta");
        else
        {
            string model = session?.Config.ModelName ?? Loc.T("panel.printer");
            if (online)
            {
                int? wifi = session?.LastStatus?.WifiSignal;
                linkText = wifi is { } w ? Loc.Tf("panel.link_wifi", model, Format.Wifi(w)) : Loc.Tf("panel.link_connected", model);
            }
            else linkText = $"{model} · {LinkSummary(session)}";
        }

        left.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
            Children =
            {
                new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Ui.Brush(dot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) },
                new TextBlock { Text = linkText, FontSize = 11, Foreground = Ui.Brush(Palette.Idle) },
            },
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(SmallButton("⟳", Loc.T("btn.refresh"), () => _store.Refresh()));
        buttons.Children.Add(SmallButton("⚙", Loc.T("btn.settings"), _openMainWindow));


        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);
        return grid;
    }

    private static string LinkSummary(PrinterSession? session)
    {
        if (session is null) return Loc.T("panel.no_printer");
        return (session.CurrentPhase, session.PhaseReason) switch
        {
            (PrinterSession.Phase.Online, _) => Loc.T("link.connected"),
            (PrinterSession.Phase.Connecting, _) => Loc.T("link.connecting"),
            (PrinterSession.Phase.Unauthorized, _) => Loc.T("link.unauthorized"),
            (PrinterSession.Phase.Certificate, var r) => r is { Length: > 0 } reason ? Loc.Tf("link.certificate", reason) : Loc.T("link.certificate_rejected"),
            (PrinterSession.Phase.Offline, var r) => r is { Length: > 0 } reason ? reason : Loc.T("link.offline"),
            _ => Loc.T("link.idle"),
        };
    }

    private UIElement SetupPrompt()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = Loc.T("panel.no_printer"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("panel.setup_desc"),
            FontSize = 12,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        });
        panel.Children.Add(PrimaryButton(Loc.T("btn.setup"), _openMainWindow, width: ActionButtonWidth));
        var card = Ui.Panel(panel, radius: 20, padding: 14);
        card.Margin = new Thickness(0, SectionGap, 0, 0);
        return card;
    }

    private UIElement JobHero(PrinterStatus status)
    {
        var stateColor = status.State == JobState.Paused ? Palette.Paused
            : status.State == JobState.Failed ? (status.Canceled ? Palette.Idle : Palette.Failed)
            : status.State == JobState.Finished ? Palette.Finished
            : Palette.ForState(status.State);

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };

        // State pill
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 3, 10, 3),
            Background = Ui.Brush(Color.FromArgb(0x25, stateColor.R, stateColor.G, stateColor.B)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = status.StateTitle + (status.State == JobState.Printing && status.Speed != SpeedLevel.Standard ? $" · {status.Speed.Title()}" : ""),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Brush(stateColor),
            },
        });

        // Job name + detail
        _jobName = new TextBlock
        {
            Text = status.JobName.Length > 0 ? status.JobName : Loc.T("job.default_name"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        panel.Children.Add(_jobName);

        _detail = new TextBlock
        {
            Text = status.DetailText,
            FontSize = 12,
            Foreground = Ui.Brush(Palette.Idle),
            Margin = new Thickness(0, 3, 0, 0),
        };
        panel.Children.Add(_detail);

        // Progress row: large % + bar + time remaining
        var percentRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        percentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        percentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        percentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // While the printer loads the 3MF and slices it there is no numeric progress: shimmer, don't pretend.
        bool indeterminate = status.State == JobState.Preparing && status.Progress < 2;
        _indeterminate = indeterminate;
        _percent = new TextBlock
        {
            Text = indeterminate ? "…" : $"{status.Progress}%",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Ui.Brush(stateColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _timing = new TextBlock
        {
            Text = status.RemainingMinutes > 0 ? Loc.Tf("panel.left_short", Format.Duration(status.RemainingMinutes)) : "",
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _progressBar = indeterminate
            ? Ui.Indeterminate(Ui.Brush(stateColor), height: 7)
            : Ui.Gauge(status.Progress / 100.0, Ui.Brush(stateColor), height: 7);

        Grid.SetColumn(_percent, 0);
        Grid.SetColumn(_progressBar, 1);
        Grid.SetColumn(_timing, 2);
        percentRow.Children.Add(_percent);
        percentRow.Children.Add(_progressBar);
        percentRow.Children.Add(_timing);
        panel.Children.Add(percentRow);

        var hero = Ui.AccentPanel(panel, stateColor, radius: 16, padding: 16);
        hero.Margin = new Thickness(0, SectionGap, 0, 0);
        return hero;
    }

    private UIElement IssueBanner(PrinterIssue issue)
    {
        var color = issue.Severity switch
        {
            IssueSeverity.Fatal => Palette.Failed,
            IssueSeverity.Serious => Palette.Paused,
            _ => Palette.Preparing,
        };
        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        content.Children.Add(new TextBlock
        {
            Text = issue.Severity.Title() + (issue.Id.Length > 0 ? $"  ·  {issue.Id}" : ""),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush(color),
            Margin = new Thickness(0, 0, 0, 3),
        });
        _issueText = new TextBlock
        {
            Text = issue.Message,
            FontSize = 12,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(_issueText);
        var card = Ui.Panel(content, radius: 14, padding: 11, tint: color);
        card.Margin = new Thickness(0, SectionGap, 0, 0);
        return card;
    }

    private UIElement Temperatures(PrinterStatus status)
    {
        var grid = new Grid { Margin = new Thickness(0, SectionGap, 0, 0) };
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nozzle = TempTile(Loc.T("tile.nozzle"), status.Nozzle.Current, status.Nozzle.Target, Palette.Nozzle, 300);
        Grid.SetColumn(nozzle, 0);
        grid.Children.Add(nozzle);

        var bed = TempTile(Loc.T("tile.bed"), status.Bed.Current, status.Bed.Target, Palette.Bed, 110);
        Grid.SetColumn(bed, 1);
        grid.Children.Add(bed);

        var chamber = TempTile(Loc.T("tile.chamber"), status.Chamber, null, Palette.Chamber, 65, readOnly: true);
        Grid.SetColumn(chamber, 2);
        grid.Children.Add(chamber);

        return grid;
    }

    private static UIElement TempTile(string title, double? current, double? target, Color tint, double max, bool readOnly = false)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Colored top accent stripe. It is a flat, full-width bar; the rounded
        // top corners are produced by clipping the card to its shape below, so
        // it hugs the card's rounded top edge instead of collapsing into a lens.
        var accentBar = new Border
        {
            Background = Ui.Brush(readOnly ? Palette.Chamber : (target is { } tt && tt > 0 ? tint : ThemeEngine.Current.Neutral(0x40))),
            CornerRadius = new CornerRadius(0),
        };
        Grid.SetRow(accentBar, 0);
        grid.Children.Add(accentBar);

        var content = new StackPanel { Margin = new Thickness(10, 8, 10, 9) };
        content.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(Palette.Idle) });
        content.Children.Add(new TextBlock
        {
            Text = current is { } c ? $"{(int)Math.Round(c)}°" : "—",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            Margin = new Thickness(0, 1, 0, 0),
        });
        string sub = readOnly
            ? (current is null ? Loc.T("tile.unavailable") : Loc.T("tile.readonly"))
            : target is { } t && t > 0
                ? (Math.Abs((current ?? 0) - t) <= 2 ? Loc.Tf("tile.at", (int)Math.Round(t))
                    : (current ?? 0) < t ? Loc.Tf("tile.heating", (int)Math.Round(t)) : Loc.Tf("tile.cooling", (int)Math.Round(t)))
                : Loc.T("tile.off");
        content.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 2, 0, 5) });
        content.Children.Add(Ui.Gauge(
            (current ?? 0) / max,
            Ui.Brush(readOnly ? Palette.Chamber : (target is { } t2 && t2 > 0 ? tint : ThemeEngine.Current.Neutral(0x4D))),
            height: 4));
        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x0D)),
            BorderBrush = new SolidColorBrush(ThemeEngine.Current.Neutral(0x33)),
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

        // Clip the card content to a rounded rect matching the card's corner
        // radius so the top accent stripe follows the rounded top edge.
        var cardClip = new System.Windows.Media.RectangleGeometry
        {
            RadiusX = 14,
            RadiusY = 14,
        };
        void ApplyClipSize(double w, double h) => cardClip.Rect = new Rect(0, 0, w, h);
        grid.SizeChanged += (s, e) => ApplyClipSize(e.NewSize.Width, e.NewSize.Height);
        grid.Loaded += (s, e) => ApplyClipSize(grid.ActualWidth, grid.ActualHeight);
        grid.Clip = cardClip;

        return outer;
    }

    private UIElement FilamentStrip(PrinterStatus status)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = Loc.T("filament.title"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 0, 0, 7) });

        var wrap = new WrapPanel();
        var active = status.ActiveSpool;
        foreach (var spool in status.AllSpools.Where(s => s.IsLoaded || s.Material.Length > 0))
        {
            Color? color = Palette.Filament(spool.Color);
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 7) };
            chip.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = color is { } c ? Ui.Brush(c) : new SolidColorBrush(ThemeEngine.Current.Neutral(0x29)),
                BorderBrush = new SolidColorBrush(ThemeEngine.Current.Neutral(0x33)),
                BorderThickness = new Thickness(0.75),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            chip.Children.Add(new TextBlock
            {
                Text = $"{spool.Label} · {spool.DisplayName} {spool.RemainText}".Trim(),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = active is { Id: int id } && id == spool.Id ? Ui.Brush(Palette.AccentDeep) : ThemeEngine.Current.NavTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            wrap.Children.Add(chip);
        }
        content.Children.Add(wrap);
        var card = Ui.Panel(content, radius: 14, padding: 12);
        card.Margin = new Thickness(0, SectionGap, 0, 0);
        return card;
    }

    private UIElement PrintControls(PrinterStatus status)
    {
        var panel = new StackPanel { Margin = new Thickness(0, SectionGap, 0, 0) };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (status.State == JobState.Paused)
        {
            row.Children.Add(PrimaryButton(Loc.T("btn.resume"), () => _store.Resume(), width: ActionButtonWidth));
        }
        else if (status.State.IsActive())
        {
            row.Children.Add(PrimaryButton(Loc.T("btn.pause"), () => _store.Pause(), width: ActionButtonWidth));
        }
        if (status.State.IsActive())
        {
            row.Children.Add(DangerButton(Loc.T("btn.stop"), () =>
            {
                if (_store.Settings.ConfirmStop && !Confirm(Loc.T("confirm.stop")))
                    return;
                _store.StopPrint();
            }, width: ActionButtonWidth));
        }
        row.Children.Add(PrimaryButton(status.ChamberLightOn is true ? Loc.T("btn.light_off") : Loc.T("btn.light_on"), () => _store.ToggleLight(), width: ActionButtonWidth));
        panel.Children.Add(row);

        var speedRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var level in new[] { SpeedLevel.Silent, SpeedLevel.Standard, SpeedLevel.Sport, SpeedLevel.Ludicrous })
            speedRow.Children.Add(SpeedButton(level, status.Speed == level, () => _store.SetSpeed(level)));
        panel.Children.Add(speedRow);

        if (status.ControlsLocked)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("panel.controls_locked"),
                FontSize = 10.5,
                Foreground = Ui.Brush(Palette.Idle),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 0),
            });
        }
        return panel;
    }

    private UIElement UpdatedLine(PrinterStatus status)
    {
        _updated = new TextBlock
        {
            Text = Loc.Tf("panel.updated", Format.RelativeTime(status.UpdatedAt)),
            FontSize = 10.5,
            Foreground = ThemeEngine.Current.TertiaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, SectionGap, 0, 0),
        };
        return _updated;
    }

    private UIElement ConnectionCard(PrinterSession? session)
    {
        string phaseText = (session?.CurrentPhase ?? PrinterSession.Phase.Idle) switch
        {
            PrinterSession.Phase.Online => Loc.T("link.connected"),
            PrinterSession.Phase.Connecting => Loc.T("link.connecting"),
            PrinterSession.Phase.Unauthorized => Loc.T("link.unauthorized_short"),
            PrinterSession.Phase.Certificate => Loc.T("link.certificate_title"),
            PrinterSession.Phase.Offline => Loc.T("link.offline"),
            _ => Loc.T("link.idle"),
        };
        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        content.Children.Add(new TextBlock
        {
            Text = phaseText,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
        });
        content.Children.Add(new TextBlock
        {
            Text = session?.PhaseReason ?? Loc.T("panel.unreachable"),
            FontSize = 12,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(PrimaryButton(Loc.T("btn.retry"), () => _store.Refresh(), width: ActionButtonWidth));
        buttons.Children.Add(PrimaryButton(Loc.T("btn.setup"), _openMainWindow, width: ActionButtonWidth));
        content.Children.Add(buttons);
        var card = Ui.Panel(content, radius: 20, padding: 14);
        card.Margin = new Thickness(0, SectionGap, 0, 0);
        return card;
    }

    // MARK: - Buttons

    private static Button SmallButton(string glyph, string help, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 28,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Foreground = Ui.Brush(Palette.Idle),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = help,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button PrimaryButton(string text, Action action, double width = double.NaN)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            Background = Ui.Brush(Palette.AccentDeep),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (!double.IsNaN(width)) button.Width = width;
        button.Click += (_, _) => action();
        return button;
    }

    private static Button DangerButton(string text, Action action, double width = double.NaN)
    {
        var button = PrimaryButton(text, action, width);
        button.Background = new SolidColorBrush(Color.FromArgb(0x26, Palette.Failed.R, Palette.Failed.G, Palette.Failed.B));
        button.Foreground = Ui.Brush(Palette.Failed);
        return button;
    }

    private static Button SpeedButton(SpeedLevel level, bool selected, Action action)
    {
        var button = new Button
        {
            Content = level.Title(),
            FontSize = 11.5,
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = selected ? Brushes.White : ThemeEngine.Current.NavTextBrush,
            Background = selected ? Ui.Brush(Palette.AccentDeep) : new SolidColorBrush(ThemeEngine.Current.Neutral(0x14)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static bool Confirm(string message)
    {
        return MessageBox.Show(message, "Bambu Bar", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
