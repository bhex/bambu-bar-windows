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

/// <summary>Widget sizes. Compact = progress % and time remaining; Full = the whole mini panel without its buttons.</summary>
public enum WidgetSize
{
    Compact,
    Full,
}

/// <summary>
/// The desktop widget: an always-on-top, borderless pane pinned to the bottom-right of the screen
/// — the Windows answer to the macOS original's menu bar item. It is purely informational (no
/// buttons), never steals focus from whatever the user is working in, and double-clicking it opens
/// the main window. Toggled from the tray ("Widget → Compact / Full").
///
/// The card's fill opacity (0–100%, solid at 100) is selectable from the tray ("Widget → Opacity")
/// and applies instantly without rebuilding the window, because the window is layered (per-pixel
/// alpha): the desktop shows through wherever the card's fill is semi-transparent.
///
/// "Always on top" is a plain WPF <c>Topmost</c> toggle: on (default) the widget floats above normal
/// windows; off it behaves like any background window.
/// </summary>
public sealed class WidgetWindow : Window
{
    private const double CompactWidth = 250;
    private const double FullWidth = 390;
    private const double CardPadding = 14;
    private const double CardRadius = 16;

    /// <summary>Card fill opacity in percent (0–100; 100 = fully opaque). Not WPF's <c>UIElement.Opacity</c>.</summary>
    public int FillOpacity { get; private set; } = 100;

    private readonly PrinterStore _store;
    private readonly Action _openMainWindow;
    private readonly Border _card;
    private readonly StackPanel _root;
    private readonly DispatcherTimer _ticker;
    private Body? _fullBody;
    private string? _lastKey;
    private WidgetSize _size = WidgetSize.Compact;

    // Compact's live-updated parts (kept between in-place updates).
    private TextBlock? _big, _sub;
    private Border? _gaugeTrack, _gaugeFill;
    private bool _activeMode, _indeterminate;

    public WidgetSize Size => _size;

    public WidgetWindow(PrinterStore store, Action openMainWindow, int opacity, bool topmost)
    {
        _store = store;
        _openMainWindow = openMainWindow;
        FillOpacity = Math.Clamp(opacity, 0, 100);

        Title = "Bambu Bar widget";
        Width = CompactWidth;
        SizeToContent = SizeToContent.Manual;
        Height = 120; // placeholder until RelayoutHeight() pins the measured content height
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;

        // A layered window gives real per-pixel transparency: the desktop shows through wherever the
        // card's fill is semi-transparent (glass) or fully transparent outside the rounded card.
        // (A non-layered window can never be see-through, which is why the old "glass" looked solid.)
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false; // passive pane: must never steal focus or take the keyboard
        Topmost = topmost;    // toggleable from the tray ("Widget → Always on top")
        SnapsToDevicePixels = true;
        Icon = TrayIcon.AppImage();

        // The card is the visible panel: a rounded fill (opaque for solid, translucent for glass)
        // that fills the window; the content sits inside its padding.
        _root = new StackPanel();
        _card = new Border
        {
            CornerRadius = new CornerRadius(CardRadius),
            Padding = new Thickness(CardPadding),
            Child = _root,
            SnapsToDevicePixels = true,
        };
        Content = _card;

        MouseDoubleClick += (_, _) => _openMainWindow();

        _store.Changed += () => Dispatcher.BeginInvoke(() => Refresh());
        Loc.Changed += () => Dispatcher.BeginInvoke(() => Refresh(force: true));
        ThemeEngine.Current.Changed += () =>
        {
            ApplyAppearance(); // re-resolve solid/glass fill + hairline against the new theme
            Refresh(force: true);
        };
        ApplyAppearance();

        // "Updated" time and ETAs need a heartbeat even when the printer is quiet.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ticker.Tick += (_, _) => Refresh(force: true);
        IsVisibleChanged += (object sender, System.Windows.DependencyPropertyChangedEventArgs e) => { if ((bool)e.NewValue) _ticker.Start(); else _ticker.Stop(); };

        ContentRendered += (_, _) => PositionAtCorner();
        SizeChanged += (_, _) => PositionAtCorner();
    }

    /// <summary>Switch between compact and full, re-laying out immediately.</summary>
    public void SetSize(WidgetSize size)
    {
        if (size == _size) return;
        _size = size;
        _lastKey = null; // force a rebuild with the new size
        _fullBody = null;
        Width = size == WidgetSize.Compact ? CompactWidth : FullWidth;
        Refresh(force: true);
    }

    /// <summary>Change the fill opacity in place (no rebuild — opacity is just the layered fill's alpha).</summary>
    public void SetFillOpacity(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (FillOpacity == percent) return;
        FillOpacity = percent;
        ApplyAppearance();
    }

    /// <summary>Set the card's fill/edge from the current opacity + theme.</summary>
    private void ApplyAppearance()
    {
        var baseColor = (ThemeEngine.Current.PanelBackground as SolidColorBrush)?.Color
                        ?? Color.FromRgb(0x1E, 0x20, 0x23);
        byte alpha = (byte)Math.Clamp((int)Math.Round(FillOpacity * 255.0 / 100.0), 0, 255);
        if (alpha >= 255)
        {
            // Solid: the same opaque surface as the mini panel, no edge needed.
            _card.Background = ThemeEngine.Current.PanelBackground;
            _card.BorderBrush = Brushes.Transparent;
            _card.BorderThickness = new Thickness(0);
        }
        else
        {
            // Semi-transparent: see the desktop through the card; a hairline edge keeps it reading
            // as a distinct surface rather than a faint smudge.
            var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();
            _card.Background = brush;
            _card.BorderBrush = ThemeEngine.Current.HairlineBrush;
            _card.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>Bottom-right of the work area, just above the taskbar and off the screen edge.</summary>
    private void PositionAtCorner()
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        double h = ActualHeight > 0 ? ActualHeight : 120;
        Left = Math.Max(0, area.Right - Width - 16);
        Top = Math.Max(0, area.Bottom - h - 16);
    }

    /// <summary>
    /// Pin the borderless window's height to its content (measuring the card, which includes the
    /// padding), like <c>PanelWindow.RelayoutHeight</c>.
    /// </summary>
    private void RelayoutHeight()
    {
        double width = Width > 0 ? Width : CompactWidth;
        _card.Measure(new Size(width, double.PositiveInfinity));
        double desired = _card.DesiredSize.Height;
        if (desired > 1 && Math.Abs(desired - Height) > 0.5)
            Height = desired;
    }

    public void Refresh(bool force = false)
    {
        if (!IsVisible) return;
        var session = _store.Current;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;
        bool configured = _store.Sessions.Count > 0;

        string prepFlag = status is { State: JobState.Preparing, Progress: >= 2 } ? "p" : "-";
        string key = $"{_size}|{configured}|{session?.Config.Serial}|{session?.CurrentPhase}|{status?.State}|{status?.Issues.Count}|{status?.ActiveSpoolId}|{prepFlag}";
        if (force || key != _lastKey)
        {
            _lastKey = key;
            _root.Children.Clear();
            _fullBody = null;
            _big = _sub = null;
            _gaugeTrack = _gaugeFill = null;
            _activeMode = false;

            if (_size == WidgetSize.Full)
            {
                // The whole mini panel, minus the interactive buttons.
                _fullBody = new Body(_store, () => { }, () => { }, interactive: false);
                _root.Children.Add(_fullBody.Host);
                _fullBody.Rebuild(configured, online, session, status);
            }
            else
            {
                BuildCompact(configured, online, session, status);
            }
            RelayoutHeight();
        }
        else if (status is not null)
        {
            UpdateInPlace(status);
        }
    }

    /// <summary>In-place update for the ~1 Hz status stream (progress, ETA).</summary>
    private void UpdateInPlace(PrinterStatus status)
    {
        if (_fullBody is not null)
        {
            _fullBody.Update(status);
            return;
        }
        if (!_activeMode) return;

        if (_big is not null && !_indeterminate) _big.Text = $"{status.Progress}%";
        if (_sub is not null)
        {
            _sub.Text = status.RemainingMinutes > 0
                ? Loc.Tf("panel.left_short", Format.Duration(status.RemainingMinutes))
                : (status.EstimatedEnd is { } end ? Loc.Tf("panel.ends", Format.Finish(end)) : _sub.Text);
        }
        if (_gaugeTrack is not null && _gaugeFill is not null && !_indeterminate)
            _gaugeFill.Width = Math.Max(0, _gaugeTrack.ActualWidth * status.Progress / 100.0);
    }

    /// <summary>
    /// The compact card: status line (dot + state + printer), a big percentage with progress bar
    /// while a job runs, and time remaining (or the job name) below it. The card fill/edge is
    /// provided by <see cref="_card"/>; here we only add the content.
    /// </summary>
    private void BuildCompact(bool configured, bool online, PrinterSession? session, PrinterStatus? status)
    {
        var panel = new StackPanel();
        string printer = session?.Config?.Name ?? "Bambu Bar";

        Color dot = Palette.Idle;
        Color bigColor = Palette.Idle;
        string stateTitle = Loc.T("link.idle");
        string big = stateTitle;
        string sub = "";

        if (!configured)
        {
            stateTitle = Loc.T("panel.no_printer");
            sub = Loc.T("tray.open");
        }
        else if (online && status is not null)
        {
            bigColor = status.State == JobState.Paused ? Palette.Paused
                : status.State == JobState.Failed ? (status.Canceled ? Palette.Idle : Palette.Failed)
                : status.State == JobState.Finished ? Palette.Finished
                : Palette.ForState(status.State);
            dot = bigColor;
            stateTitle = status.StateTitle + (status.State == JobState.Printing && status.Speed != SpeedLevel.Standard ? $" · {status.Speed.Title()}" : "");

            if (status.State.IsActive())
            {
                _activeMode = true;
                // While the printer loads the 3MF and slices it there is no numeric progress: shimmer, don't pretend.
                bool indeterminate = status.State == JobState.Preparing && status.Progress < 2;
                _indeterminate = indeterminate;

                var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _big = new TextBlock
                {
                    Text = indeterminate ? "…" : $"{status.Progress}%",
                    FontSize = 26,
                    FontWeight = FontWeights.Bold,
                    Foreground = Ui.Brush(bigColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                _gaugeTrack = indeterminate
                    ? Ui.Indeterminate(Ui.Brush(bigColor), height: 7)
                    : Ui.Gauge(status.Progress / 100.0, Ui.Brush(bigColor), height: 7);
                if (_gaugeTrack.Child is Border fill) _gaugeFill = fill;
                Grid.SetColumn(_big, 0);
                Grid.SetColumn(_gaugeTrack, 1);
                row.Children.Add(_big);
                row.Children.Add(_gaugeTrack);
                panel.Children.Add(row);

                sub = status.RemainingMinutes > 0
                    ? Loc.Tf("panel.left_short", Format.Duration(status.RemainingMinutes))
                    : (status.EstimatedEnd is { } end
                        ? Loc.Tf("panel.ends", Format.Finish(end))
                        : (status.JobName.Length > 0 ? status.JobName : Loc.T("job.default_name")));
            }
            else
            {
                big = status.StateTitle;
                sub = status.State == JobState.Paused
                    ? (status.JobName.Length > 0 ? $"{status.Progress}% · {status.JobName}" : $"{status.Progress}%")
                    : (status.JobName.Length > 0 ? status.JobName : (status.MostSevereIssue?.Message ?? ""));
            }
        }
        else
        {
            dot = session is { CurrentPhase: PrinterSession.Phase.Connecting } ? Palette.Paused : Palette.Failed;
            stateTitle = (session?.CurrentPhase ?? PrinterSession.Phase.Idle) switch
            {
                PrinterSession.Phase.Connecting => Loc.T("link.connecting"),
                PrinterSession.Phase.Unauthorized => Loc.T("link.unauthorized"),
                PrinterSession.Phase.Certificate => Loc.T("link.certificate_title"),
                _ => Loc.T("link.offline"),
            };
            sub = session?.PhaseReason is { Length: > 0 } reason ? reason : Loc.T("panel.unreachable");
        }

        // Row 1: status dot + state title | printer name
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Ui.Brush(dot),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        });
        left.Children.Add(new TextBlock
        {
            Text = stateTitle,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(left, 0);
        row1.Children.Add(left);
        var right = new TextBlock
        {
            Text = printer,
            FontSize = 11,
            Foreground = ThemeEngine.Current.TertiaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 90,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(right, 1);
        row1.Children.Add(right);
        panel.Children.Add(row1);

        if (!_activeMode)
        {
            _big = new TextBlock
            {
                Text = big,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Ui.Brush(bigColor),
                Margin = new Thickness(0, 8, 0, 0),
            };
            panel.Children.Add(_big);
        }

        if (sub.Length > 0)
        {
            _sub = new TextBlock
            {
                Text = sub,
                FontSize = 11.5,
                Foreground = Ui.Brush(Palette.Idle),
                Margin = new Thickness(0, _activeMode ? 8 : 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            panel.Children.Add(_sub);
        }

        // The card (solid or glass) is the window's _card; the content sits directly on it.
        _root.Children.Add(panel);
    }
}
