using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BambuBar.Core.App;
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
    private readonly TextBlock _toast;
    private readonly DispatcherTimer _ticker;
    private string? _lastKey;

    public PanelWindow(PrinterStore store, Action openMainWindow, Action quit)
    {
        _store = store;
        _body = new Body(store, openMainWindow, quit);

        Title = "Bambu Bar";
        Width = 390;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF4));
        SnapsToDevicePixels = true;
        Icon = TrayIcon.AppImage();

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(_body.Host);

        _toast = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 2, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x74, 0x7B)),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_toast);

        root.Children.Add(new TextBlock
        {
            Text = "Double-click the tray icon for the full window",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });

        Content = root;

        _store.Changed += () => Dispatcher.BeginInvoke(() => Refresh());
        _store.Toasted += toast => Dispatcher.BeginInvoke(() => ShowToast(toast));

        // "Updated Xs ago" and ETAs need a heartbeat even when the printer is quiet.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ticker.Tick += (_, _) => Refresh(force: true);
        _ticker.Start();

        Deactivated += (_, _) => Close();
        Loaded += (_, _) => PositionNearTray();
    }

    private void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void ShowToast(PrinterStore.Toast toast)
    {
        _toast.Text = toast.Message;
        _toast.Foreground = Ui.Brush(toast.IsError ? Palette.Failed : Palette.AccentDeep);
        _toast.Visibility = Visibility.Visible;
    }

    private void Refresh(bool force = false)
    {
        var session = _store.Current;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;
        bool configured = _store.Sessions.Count > 0;

        string key = $"{configured}|{session?.Config.Serial}|{session?.CurrentPhase}|{status?.State}|{status?.Issues.Count}|{status?.ActiveSpoolId}";
        if (force || key != _lastKey)
        {
            _lastKey = key;
            _body.Rebuild(configured, online, session, status);
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
        if (_jobName is not null) _jobName.Text = status.JobName.Length > 0 ? status.JobName : "Print";
        if (_detail is not null) _detail.Text = status.DetailText;
        if (_percent is not null) _percent.Text = $"{status.Progress}%";
        if (_timing is not null)
        {
            _timing.Text = status.RemainingMinutes > 0
                ? $"~{Format.Duration(status.RemainingMinutes)} left"
                : (status.EstimatedEnd is { } end ? $"ends {Format.Finish(end)}" : "");
        }
        if (_progressBar is not null && _progressBar.Child is Border fill)
            fill.Width = Math.Max(0, _progressBar.ActualWidth * status.Progress / 100.0);
        if (_updated is not null)
            _updated.Text = $"Updated {Format.RelativeTime(status.UpdatedAt).Replace(" ago", "")}";
        if (_issueText is not null && status.MostSevereIssue is { } issue)
            _issueText.Text = issue.Message;
    }

    // MARK: - Pieces

    private UIElement Header(bool configured, PrinterSession? session, bool online)
    {
        var grid = new Grid { Margin = new Thickness(4, 2, 4, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Vertical };
        left.Children.Add(new TextBlock
        {
            Text = configured ? (session?.Config.Name ?? "Bambu Bar") : "Bambu Bar",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        Color dot = online ? Palette.Accent
            : session is { CurrentPhase: PrinterSession.Phase.Connecting } ? Palette.Paused
            : configured ? Palette.Failed : Palette.Idle;

        string linkText;
        if (!configured) linkText = "Set up your printer to begin";
        else
        {
            string model = session?.Config.ModelName ?? "printer";
            if (online)
            {
                int? wifi = session?.LastStatus?.WifiSignal;
                linkText = wifi is { } w ? $"{model} · {Format.Wifi(w)}" : $"{model} · Connected";
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
        buttons.Children.Add(SmallButton("⟳", "Refresh", () => _store.Refresh()));
        buttons.Children.Add(SmallButton("⚙", "Settings", _openMainWindow));
        buttons.Children.Add(SmallButton("✕", "Quit", _quit));

        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);
        return grid;
    }

    private static string LinkSummary(PrinterSession? session)
    {
        if (session is null) return "No printer set up";
        return (session.CurrentPhase, session.PhaseReason) switch
        {
            (PrinterSession.Phase.Online, _) => "Connected",
            (PrinterSession.Phase.Connecting, _) => "Connecting…",
            (PrinterSession.Phase.Unauthorized, _) => "Access code rejected — see Settings",
            (PrinterSession.Phase.Certificate, var r) => r is { Length: > 0 } reason ? $"Certificate: {reason}" : "Certificate not accepted",
            (PrinterSession.Phase.Offline, var r) => r is { Length: > 0 } reason ? reason : "Offline",
            _ => "Idle",
        };
    }

    private UIElement SetupPrompt()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "No printer set up", FontSize = 14, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Bambu Bar shows your print's progress, temperatures and filament in the system tray. It talks to the printer directly over your network — no account, no cloud.",
            FontSize = 12,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        });
        panel.Children.Add(PrimaryButton("Set Up…", _openMainWindow, width: 140));
        return Ui.Panel(panel, radius: 20, padding: 14);
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
            Text = status.JobName.Length > 0 ? status.JobName : "Print",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
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

        _percent = new TextBlock
        {
            Text = $"{status.Progress}%",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Ui.Brush(stateColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _timing = new TextBlock
        {
            Text = status.RemainingMinutes > 0 ? $"~{Format.Duration(status.RemainingMinutes)}" : "",
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _progressBar = Ui.Gauge(status.Progress / 100.0, Ui.Brush(stateColor), height: 7);

        Grid.SetColumn(_percent, 0);
        Grid.SetColumn(_progressBar, 1);
        Grid.SetColumn(_timing, 2);
        percentRow.Children.Add(_percent);
        percentRow.Children.Add(_progressBar);
        percentRow.Children.Add(_timing);
        panel.Children.Add(percentRow);

        return Ui.AccentPanel(panel, stateColor, radius: 16, padding: 16);
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
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush(color),
            Margin = new Thickness(0, 0, 0, 3),
        });
        _issueText = new TextBlock
        {
            Text = issue.Message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(_issueText);
        return Ui.Panel(content, radius: 14, padding: 11, tint: color);
    }

    private UIElement Temperatures(PrinterStatus status)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nozzle = TempTile("Nozzle", status.Nozzle.Current, status.Nozzle.Target, Palette.Nozzle, 300);
        Grid.SetColumn(nozzle, 0);
        grid.Children.Add(nozzle);

        var bed = TempTile("Bed", status.Bed.Current, status.Bed.Target, Palette.Bed, 110);
        Grid.SetColumn(bed, 1);
        grid.Children.Add(bed);

        var chamber = TempTile("Chamber", status.Chamber, null, Palette.Chamber, 65, readOnly: true);
        Grid.SetColumn(chamber, 2);
        grid.Children.Add(chamber);

        return grid;
    }

    private static UIElement TempTile(string title, double? current, double? target, Color tint, double max, bool readOnly = false)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Colored top accent bar
        var accentBar = new Border
        {
            Background = Ui.Brush(readOnly ? Palette.Chamber : (target is { } tt && tt > 0 ? tint : Color.FromArgb(0x40, 0x00, 0x00, 0x00))),
            CornerRadius = new CornerRadius(14, 14, 0, 0),
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
            Margin = new Thickness(0, 1, 0, 0),
        });
        string sub = readOnly ? "Read only"
            : target is { } t && t > 0
                ? (Math.Abs((current ?? 0) - t) <= 2 ? $"At {t:0}°"
                    : (current ?? 0) < t ? $"Heating to {t:0}°" : $"Cooling to {t:0}°")
                : "Off";
        content.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 2, 0, 5) });
        content.Children.Add(Ui.Gauge(
            (current ?? 0) / max,
            Ui.Brush(readOnly ? Palette.Chamber : (target is { } t2 && t2 > 0 ? tint : Color.FromArgb(0x4D, 0, 0, 0))),
            height: 4));
        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x0D, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
            BorderThickness = new Thickness(0.75),
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.06,
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
            },
        };
        return outer;
    }

    private UIElement FilamentStrip(PrinterStatus status)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "FILAMENT", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 0, 0, 7) });

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
                Background = color is { } c ? Ui.Brush(c) : new SolidColorBrush(Color.FromArgb(0x29, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
                BorderThickness = new Thickness(0.75),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            chip.Children.Add(new TextBlock
            {
                Text = $"{spool.Label} · {spool.DisplayName} {spool.RemainText}".Trim(),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = active is { Id: int id } && id == spool.Id ? Ui.Brush(Palette.AccentDeep) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            wrap.Children.Add(chip);
        }
        content.Children.Add(wrap);
        return Ui.Panel(content, radius: 14, padding: 12);
    }

    private UIElement PrintControls(PrinterStatus status)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (status.State == JobState.Paused)
        {
            row.Children.Add(PrimaryButton("Resume", () => _store.Resume(), width: 110));
        }
        else if (status.State.IsActive())
        {
            row.Children.Add(PrimaryButton("Pause", () => _store.Pause(), width: 110));
        }
        if (status.State.IsActive())
        {
            row.Children.Add(DangerButton("Stop", () =>
            {
                if (_store.Settings.ConfirmStop && !Confirm("Stop the print? The printer will be told to stop the current job."))
                    return;
                _store.StopPrint();
            }, width: 110));
        }
        row.Children.Add(PrimaryButton(status.ChamberLightOn is true ? "Light off" : "Light on", () => _store.ToggleLight(), width: 96));
        panel.Children.Add(row);

        var speedRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var level in new[] { SpeedLevel.Silent, SpeedLevel.Standard, SpeedLevel.Sport, SpeedLevel.Ludicrous })
            speedRow.Children.Add(SpeedButton(level, status.Speed == level, () => _store.SetSpeed(level)));
        panel.Children.Add(speedRow);

        if (status.ControlsLocked)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Pause, stop and speed changes need LAN Only mode with Developer Mode on the printer (Settings → Network).",
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
            Text = $"Updated {Format.RelativeTime(status.UpdatedAt).Replace(" ago", "")}",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 4),
        };
        return _updated;
    }

    private UIElement ConnectionCard(PrinterSession? session)
    {
        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        content.Children.Add(new TextBlock
        {
            Text = session?.CurrentPhase.ToString() ?? "Idle",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = session?.PhaseReason ?? "The printer isn't reachable on the network.",
            FontSize = 12,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(PrimaryButton("Retry", () => _store.Refresh(), width: 110));
        buttons.Children.Add(PrimaryButton("Set Up…", _openMainWindow, width: 110));
        content.Children.Add(buttons);
        return Ui.Panel(content, radius: 20, padding: 14);
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
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Background = selected ? Ui.Brush(Palette.AccentDeep) : new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)),
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
