using System.Windows.Forms;
using BambuBar.Core.App;
using BambuBar.Core.I18n;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;
using BambuBar.Core.Store;
using Application = System.Windows.Application;

namespace BambuBar.App;

/// <summary>
/// The notification-area icon (the Windows stand-in for the macOS menu bar item): a state glyph with
/// the print's progress, a tooltip in the user's chosen style, quick actions in the right-click menu,
/// and balloon toasts for command results.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly PrinterStore _store;
    private readonly NotifyIcon _tray;
    private readonly Action _openPanel;
    private readonly Action _togglePanel;
    private readonly Action _openMainWindow;
    private readonly Action _quit;
    private readonly Action<WidgetSize> _toggleWidget;
    private readonly Func<WidgetSize, bool> _widgetActive;
    private readonly Func<bool> _widgetVisible;
    private readonly Func<bool> _widgetEnabled;
    private readonly Action<bool> _setWidgetEnabled;
    private readonly Func<int> _widgetOpacity;
    private readonly Action<int> _setWidgetOpacity;
    private readonly Func<bool> _widgetTopmost;
    private readonly Action<bool> _setWidgetTopmost;
    private readonly Func<bool> _panelVisible;
    private readonly Func<bool> _notificationsOn;
    private readonly Action<bool> _setNotifications;
    private System.Windows.Forms.Timer? _clickTimer;
    private System.Windows.Forms.Timer? _prepAnim;
    private int _prepFrame;

    public TrayController(PrinterStore store, Action openPanel, Action togglePanel, Action openMainWindow, Action quit,
        Action<WidgetSize> toggleWidget, Func<WidgetSize, bool> widgetActive,
        Func<bool> widgetVisible, Func<bool> widgetEnabled, Action<bool> setWidgetEnabled,
        Func<int> widgetOpacity, Action<int> setWidgetOpacity,
        Func<bool> widgetTopmost, Action<bool> setWidgetTopmost,
        Func<bool> panelVisible,
        Func<bool> notificationsOn, Action<bool> setNotifications)
    {
        _store = store;
        _openPanel = openPanel;
        _togglePanel = togglePanel;
        _openMainWindow = openMainWindow;
        _quit = quit;
        _toggleWidget = toggleWidget;
        _widgetActive = widgetActive;
        _widgetVisible = widgetVisible;
        _widgetEnabled = widgetEnabled;
        _setWidgetEnabled = setWidgetEnabled;
        _widgetOpacity = widgetOpacity;
        _setWidgetOpacity = setWidgetOpacity;
        _widgetTopmost = widgetTopmost;
        _setWidgetTopmost = setWidgetTopmost;
        _panelVisible = panelVisible;
        _notificationsOn = notificationsOn;
        _setNotifications = setNotifications;

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Get(null, 0, false, true),
            Text = "Bambu Bar",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem(Loc.T("tray.open")) { ShortcutKeys = Keys.Control | Keys.O };
        open.Click += (_, _) => _openMainWindow();
        var miniItem = new ToolStripMenuItem(Loc.T("tray.mini_panel"));
        miniItem.Click += (_, _) =>
        {
            _togglePanel();
            OnUi(Refresh); // keep the check-mark honest immediately, even if the printer is idle
        };
        var widget = new ToolStripMenuItem(Loc.T("tray.widget"));
        var onItem = new ToolStripMenuItem(Loc.T("tray.widget.on"));
        onItem.Click += (_, _) => _setWidgetEnabled(!_widgetEnabled());
        var widgetCompact = new ToolStripMenuItem(Loc.T("tray.widget.compact"));
        widgetCompact.Click += (_, _) => _toggleWidget(WidgetSize.Compact);
        var widgetFull = new ToolStripMenuItem(Loc.T("tray.widget.full"));
        widgetFull.Click += (_, _) => _toggleWidget(WidgetSize.Full);
        var opacityMenu = new ToolStripMenuItem(Loc.T("tray.widget.opacity"));
        var opacityItems = new List<(int Percent, ToolStripMenuItem Item)>();
        foreach (int percent in new[] { 100, 75, 50, 25 })
        {
            int chosen = percent;
            var item = new ToolStripMenuItem($"{chosen}%");
            item.Click += (_, _) => _setWidgetOpacity(chosen);
            opacityMenu.DropDownItems.Add(item);
            opacityItems.Add((chosen, item));
        }
        _opacityItems = opacityItems.ToArray();
        var topmostItem = new ToolStripMenuItem(Loc.T("tray.widget.topmost"));
        topmostItem.Click += (_, _) => _setWidgetTopmost(!_widgetTopmost());
        widget.DropDownItems.Add(onItem);
        widget.DropDownItems.Add(widgetCompact);
        widget.DropDownItems.Add(widgetFull);
        widget.DropDownItems.Add(new ToolStripSeparator());
        widget.DropDownItems.Add(opacityMenu);
        widget.DropDownItems.Add(topmostItem);
        var notifItem = new ToolStripMenuItem(Loc.T("tray.notifications"));
        notifItem.Click += (_, _) => _setNotifications(!_notificationsOn());
        menu.Items.Add(open);
        menu.Items.Add(miniItem);
        menu.Items.Add(widget);
        menu.Items.Add(notifItem);
        var pause = new ToolStripMenuItem(Loc.T("tray.pause")) { Visible = false };
        pause.Click += (_, _) => OnUi(() => _store.Pause());
        var resume = new ToolStripMenuItem(Loc.T("tray.resume")) { Visible = false };
        resume.Click += (_, _) => OnUi(() => _store.Resume());
        var stop = new ToolStripMenuItem(Loc.T("tray.stop")) { Visible = false };
        stop.Click += (_, _) => OnUi(() => _store.StopPrint());
        var quitItem = new ToolStripMenuItem(Loc.T("tray.quit"));
        quitItem.Click += (_, _) => _quit();
        menu.Items.Add(pause);
        menu.Items.Add(resume);
        menu.Items.Add(stop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);
        _tray.ContextMenuStrip = menu;
        _pauseItem = pause;
        _resumeItem = resume;
        _stopItem = stop;
        _openItem = open;
        _quitItem = quitItem;
        _widgetItem = widget;
        _widgetOnItem = onItem;
        _widgetCompactItem = widgetCompact;
        _widgetFullItem = widgetFull;
        _widgetOpacityItem = opacityMenu;
        _widgetTopmostItem = topmostItem;
        _miniPanelItem = miniItem;
        _notifItem = notifItem;

        // Single click → mini panel (suppressed while the widget is on screen — the widget is
        // that panel's replacement, so a stray pop-up would just cover it); double click → main
        // window. De-bounced with a short timer.
        _clickTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer!.Stop();
            if (_widgetVisible()) return;
            _openPanel();
        };

        // While the printer loads the 3MF and slices it no numeric progress is reported: spin an arc.
        _prepAnim = new System.Windows.Forms.Timer { Interval = 100 };
        _prepAnim.Tick += (_, _) =>
        {
            if (_disposed) return;
            _prepFrame = (_prepFrame + 1) % 24;
            try { _tray.Icon = TrayIcon.PrepSpin(_prepFrame); } catch { }
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_pendingDoubleClick)
                {
                    _pendingDoubleClick = false;
                    _clickTimer?.Stop();
                    _openMainWindow();
                }
                else
                {
                    _clickTimer?.Stop();
                    _clickTimer?.Start();
                }
            }
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _pendingDoubleClick = true;
                _clickTimer?.Stop();
            }
        };

        // Named handlers so Dispose can unhook them: a store phase change arriving during
        // teardown must not queue a refresh against an icon that is already gone.
        _changedHandler = () => OnUi(Refresh);
        _locHandler = () => OnUi(Refresh);
        _toastHandler = toast =>
        {
            // Command acknowledgements and store-level toasts become balloons so they reach the user
            // even when no window is open.
            OnUi(() => ShowToast(toast.Message, toast.IsError ? ToolTipIcon.Warning : ToolTipIcon.Info));
        };
        _store.Changed += _changedHandler;
        Loc.Changed += _locHandler;
        _store.Toasted += _toastHandler;

        Refresh();
    }

    private bool _pendingDoubleClick;
    private volatile bool _disposed;
    private readonly Action _changedHandler;
    private readonly Action _locHandler;
    private readonly Action<PrinterStore.Toast> _toastHandler;
    private ToolStripMenuItem _pauseItem = null!, _resumeItem = null!, _stopItem = null!, _openItem = null!, _quitItem = null!;
    private ToolStripMenuItem _widgetItem = null!, _widgetOnItem = null!, _widgetCompactItem = null!, _widgetFullItem = null!;
    private ToolStripMenuItem _widgetOpacityItem = null!, _widgetTopmostItem = null!, _miniPanelItem = null!;
    private ToolStripMenuItem _notifItem = null!;
    private (int Percent, ToolStripMenuItem Item)[] _opacityItems = Array.Empty<(int, ToolStripMenuItem)>();

    /// <summary>
    /// Runs an action on the WPF dispatcher (the tray lives on that thread; store events don't).
    /// Never propagates: work queued just before a Dispose can still run just after it, during
    /// app exit, and an exception escaping there is an unhandled fault (WER crash).
    /// </summary>
    private static void OnUi(Action action)
    {
        void Run()
        {
            try { action(); }
            catch { /* Outlived the tray; a stale refresh is worthless. */ }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Run();
        else dispatcher.BeginInvoke(Run);
    }

    /// <summary>Toast the user (balloon) — used for job lifecycle events too.</summary>
    public void ShowToast(string message, ToolTipIcon icon)
    {
        if (_disposed) return;
        try
        {
            _tray.ShowBalloonTip(3000, "Bambu Bar", message, icon);
        }
        catch
        {
            // A failed balloon is not worth surfacing.
        }
    }

    private void Refresh()
    {
        if (_disposed) return;
        var session = _store.TraySession;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;
        bool configured = _store.Sessions.Count > 0;

        JobState? state = null;
        double progress = 0;
        bool alert = false;
        if (online && status is not null)
        {
            state = status.Canceled && status.State == JobState.Failed ? JobState.Idle : status.State;
            progress = status.Progress / 100.0;
            alert = status.MostSevereIssue is { Severity: IssueSeverity.Fatal or IssueSeverity.Serious };
        }

        bool preparing = state == JobState.Preparing && progress < 0.02 && online;
        try
        {
            _tray.Icon = preparing
                ? TrayIcon.PrepSpin(_prepFrame)
                : TrayIcon.Get(state, progress, alert, configured && !online);
        }
        catch
        {
            // The icon can die (app exit, explorer restart) in the same breath; a stale refresh
            // is worthless and must never become an unhandled fault.
        }
        if (preparing) _prepAnim?.Start(); else _prepAnim?.Stop();
        try { _tray.Text = TooltipText(session, status, online); } catch { }
        UpdateMenu(session, status, online);
    }

    private void UpdateMenu(PrinterSession? session, PrinterStatus? status, bool online)
    {
        bool active = online && status is not null && status.State.IsActive();
        bool paused = online && status is not null && status.State == JobState.Paused;
        _pauseItem.Visible = active && !paused;
        _resumeItem.Visible = paused;
        _stopItem.Visible = active;
        // Keep the menu wording in sync with the active language, and mark the live widget state.
        _openItem.Text = Loc.T("tray.open");
        _miniPanelItem.Text = Loc.T("tray.mini_panel");
        _miniPanelItem.Checked = _panelVisible();
        _widgetItem.Text = Loc.T("tray.widget");
        _widgetOnItem.Text = Loc.T("tray.widget.on");
        _widgetOnItem.Checked = _widgetEnabled();
        _widgetCompactItem.Text = Loc.T("tray.widget.compact");
        _widgetFullItem.Text = Loc.T("tray.widget.full");
        _widgetCompactItem.Checked = _widgetActive(WidgetSize.Compact);
        _widgetFullItem.Checked = _widgetActive(WidgetSize.Full);
        _widgetOpacityItem.Text = Loc.T("tray.widget.opacity");
        int currentOpacity = _widgetOpacity();
        foreach (var (percent, item) in _opacityItems)
            item.Checked = percent == currentOpacity;
        _widgetTopmostItem.Text = Loc.T("tray.widget.topmost");
        _widgetTopmostItem.Checked = _widgetTopmost();
        _notifItem.Text = Loc.T("tray.notifications");
        _notifItem.Checked = _notificationsOn();
        _pauseItem.Text = Loc.T("tray.pause");
        _resumeItem.Text = Loc.T("tray.resume");
        _stopItem.Text = Loc.T("tray.stop");
        _quitItem.Text = Loc.T("tray.quit");
    }

    /// <summary>The tooltip, shaped by the "while printing, show" setting (the macOS menu bar text).</summary>
    private string TooltipText(PrinterSession? session, PrinterStatus? s, bool online)
    {
        if (session is null || !online)
        {
            var name = session?.Config.Name ?? "Bambu Bar";
            return session is null
                ? Loc.T("tray.no_printer_tip")
                : Loc.Tf("tray.tip_phase", name, PhaseName(session.CurrentPhase));
        }

        if (s is null) return Loc.Tf("tray.waiting", session.Config.Name);

        if (!s.State.IsActive())
            return Loc.Tf("tray.tip_state", session.Config.Name, s.StateTitle);

        string paused = JobState.Paused.Title();
        string pct = s.State == JobState.Preparing ? JobState.Preparing.Title() + "…" : $"{s.Progress}%";
        string? left = s.RemainingMinutes > 0 ? Format.Duration(s.RemainingMinutes) : null;
        return _store.Settings.MenuBarStyleSetting switch
        {
            SettingsStore.MenuBarStyle.Icon => s.State.Title(),
            SettingsStore.MenuBarStyle.Percent => s.State == JobState.Paused ? paused : pct,
            SettingsStore.MenuBarStyle.Remaining => s.State == JobState.Paused ? paused : left ?? pct,
            _ => s.State == JobState.Paused
                ? $"{pct} · {paused}"
                : (left is null ? pct : $"{pct} · {left}"),
        };
    }

    private static string PhaseName(PrinterSession.Phase phase) => phase switch
    {
        PrinterSession.Phase.Online => Loc.T("link.connected"),
        PrinterSession.Phase.Connecting => Loc.T("link.connecting"),
        PrinterSession.Phase.Offline => Loc.T("link.offline"),
        PrinterSession.Phase.Unauthorized => Loc.T("link.unauthorized_short"),
        PrinterSession.Phase.Certificate => Loc.T("link.cert"),
        _ => phase.ToString(),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unhook first: a phase change during store teardown would otherwise queue a Refresh
        // against a dead icon — the exact NRE (NotifyIcon.UpdateIcon) behind the WER crashes.
        _store.Changed -= _changedHandler;
        Loc.Changed -= _locHandler;
        _store.Toasted -= _toastHandler;

        _clickTimer?.Stop();
        _clickTimer?.Dispose();
        _prepAnim?.Stop();
        _prepAnim?.Dispose();
        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch
        {
            // The icon may already be half-dead; the process is exiting either way.
        }
    }
}
