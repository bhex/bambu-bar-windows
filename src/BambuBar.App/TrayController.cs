using System.Windows.Forms;
using BambuBar.Core.App;
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
    private readonly Action _openMainWindow;
    private readonly Action _quit;
    private System.Windows.Forms.Timer? _clickTimer;
    private System.Windows.Forms.Timer? _prepAnim;
    private int _prepFrame;

    public TrayController(PrinterStore store, Action openPanel, Action openMainWindow, Action quit)
    {
        _store = store;
        _openPanel = openPanel;
        _openMainWindow = openMainWindow;
        _quit = quit;

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Get(null, 0, false, true),
            Text = "Bambu Bar",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Bambu Bar") { ShortcutKeys = Keys.Control | Keys.O };
        open.Click += (_, _) => _openMainWindow();
        var pause = new ToolStripMenuItem("Pause") { Visible = false };
        pause.Click += (_, _) => OnUi(() => _store.Pause());
        var resume = new ToolStripMenuItem("Resume") { Visible = false };
        resume.Click += (_, _) => OnUi(() => _store.Resume());
        var stop = new ToolStripMenuItem("Stop print") { Visible = false };
        stop.Click += (_, _) => OnUi(() => _store.StopPrint());
        var quitItem = new ToolStripMenuItem("Quit Bambu Bar");
        quitItem.Click += (_, _) => _quit();
        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(resume);
        menu.Items.Add(stop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);
        _tray.ContextMenuStrip = menu;
        _pauseItem = pause;
        _resumeItem = resume;
        _stopItem = stop;

        // Single click → panel; double click → main window. De-bounced with a short timer.
        _clickTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer!.Stop();
            _openPanel();
        };

        // While the printer loads the 3MF and slices it no numeric progress is reported: spin an arc.
        _prepAnim = new System.Windows.Forms.Timer { Interval = 100 };
        _prepAnim.Tick += (_, _) =>
        {
            _prepFrame = (_prepFrame + 1) % 24;
            _tray.Icon = TrayIcon.PrepSpin(_prepFrame);
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

        _store.Changed += () => OnUi(Refresh);
        _store.Toasted += toast =>
        {
            // Command acknowledgements and store-level toasts become balloons so they reach the user
            // even when no window is open.
            OnUi(() => ShowToast(toast.Message, toast.IsError ? ToolTipIcon.Warning : ToolTipIcon.Info));
        };

        Refresh();
    }

    private bool _pendingDoubleClick;
    private ToolStripMenuItem _pauseItem = null!, _resumeItem = null!, _stopItem = null!;

    /// <summary>Runs an action on the WPF dispatcher (the tray lives on that thread; store events don't).</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>Toast the user (balloon) — used for job lifecycle events too.</summary>
    public void ShowToast(string message, ToolTipIcon icon)
    {
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
        _tray.Icon = preparing
            ? TrayIcon.PrepSpin(_prepFrame)
            : TrayIcon.Get(state, progress, alert, configured && !online);
        if (preparing) _prepAnim?.Start(); else _prepAnim?.Stop();
        _tray.Text = TooltipText(session, status, online);
        UpdateMenu(session, status, online);
    }

    private void UpdateMenu(PrinterSession? session, PrinterStatus? status, bool online)
    {
        bool active = online && status is not null && status.State.IsActive();
        bool paused = online && status is not null && status.State == JobState.Paused;
        _pauseItem.Visible = active && !paused;
        _resumeItem.Visible = paused;
        _stopItem.Visible = active;
    }

    /// <summary>The tooltip, shaped by the "while printing, show" setting (the macOS menu bar text).</summary>
    private string TooltipText(PrinterSession? session, PrinterStatus? s, bool online)
    {
        if (session is null || !online)
        {
            var name = session?.Config.Name ?? "Bambu Bar";
            return session is null ? "Bambu Bar — no printer set up" : $"{name} — {session.CurrentPhase}";
        }

        if (s is null) return $"{session.Config.Name} — waiting for data";

        if (!s.State.IsActive())
            return $"{session.Config.Name} — {s.StateTitle}";

        string pct = s.State == JobState.Preparing ? "Preparing…" : $"{s.Progress}%";
        string? left = s.RemainingMinutes > 0 ? Format.Duration(s.RemainingMinutes) : null;
        return _store.Settings.MenuBarStyleSetting switch
        {
            SettingsStore.MenuBarStyle.Icon => s.State.Title(),
            SettingsStore.MenuBarStyle.Percent => s.State == JobState.Paused ? "Paused" : pct,
            SettingsStore.MenuBarStyle.Remaining => s.State == JobState.Paused ? "Paused" : left ?? pct,
            _ => s.State == JobState.Paused
                ? $"{pct} · Paused"
                : (left is null ? pct : $"{pct} · {left}"),
        };
    }

    public void Dispose()
    {
        _clickTimer?.Dispose();
        _prepAnim?.Stop();
        _prepAnim?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
