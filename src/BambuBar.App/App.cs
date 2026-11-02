using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using BambuBar.Core.App;
using BambuBar.Core.Demo;
using BambuBar.Core.I18n;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;

namespace BambuBar.App;

/// <summary>
/// The application: CLI dispatch, the printer store, the tray, notifications and power handling.
/// (The macOS original is a menu bar extra; on Windows the tray icon + panel window fill that role.)
/// </summary>
public sealed class App : System.Windows.Application
{
    private const string PipeName = "BambuBar-ipc";

    private PrinterStore? _store;
    private TrayController? _tray;
    private PowerManager? _power;
    private Notifier? _notifier;
    private MainWindow? _mainWindow;
    private PanelWindow? _panelWindow;
    private WidgetWindow? _widgetWindow;
    private WidgetSize _widgetSize;
    private Thread? _pipeThread;
    private Mutex? _singleInstance;
    private bool _trayOnly;

    // Set from the CLI (--widget compact|full, --glass) before Run(): start with the widget already on screen.
    public bool StartWithWidget { get; set; }
    public WidgetSize StartWithWidgetSize { get; set; } = WidgetSize.Compact;
    public bool StartWithGlass { get; set; }

    // MARK: - Entry point & CLI

    [STAThread]
    public static int Main(string[] args)
    {
        var list = args.ToList();
        if (list.Count == 0) list = Environment.GetCommandLineArgs().Skip(1).ToList();

        if (list.Contains("--help") || list.Contains("-h"))
        {
            AttachConsole();
            PrintUsage();
            return 0;
        }
        if (list.Contains("--version"))
        {
            AttachConsole();
            Console.WriteLine($"Bambu Bar {Version()}");
            return 0;
        }

        int probeIndex = list.IndexOf("--probe");
        if (probeIndex >= 0)
        {
            AttachConsole();
            if (probeIndex + 3 >= list.Count)
            {
                Console.WriteLine("usage: --probe <host> <serial> <access-code> [seconds]");
                return 2;
            }
            var seconds = probeIndex + 4 < list.Count && int.TryParse(list[probeIndex + 4], out var s) ? s : 10;
            return Probe.Run(list[probeIndex + 1], list[probeIndex + 2], list[probeIndex + 3], seconds).GetAwaiter().GetResult();
        }

        var app = new App();
        int widgetIndex = list.IndexOf("--widget");
        if (widgetIndex >= 0)
        {
            app.StartWithWidget = true;
            app.StartWithWidgetSize = widgetIndex + 1 < list.Count
                && list[widgetIndex + 1].Equals("full", StringComparison.OrdinalIgnoreCase) ? WidgetSize.Full : WidgetSize.Compact;
        }
        app.StartWithGlass = list.Contains("--glass");
        app.Run();
        return 0;
    }

    private static void AttachConsole()
    {
        // A WinExe has no console; attach one so CLI output is visible.
        if (!NativeConsole.ConsoleAttached())
        {
            NativeConsole.AllocConsole();
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Bambu Bar — your Bambu Lab printer in the system tray.\n" +
            "\n" +
            "usage:\n" +
            "  BambuBar.exe                    start the app\n" +
            "  BambuBar.exe --tray             start quietly in the tray (used by \"open at login\")\n" +
            "  BambuBar.exe --demo [scenario]  try the app with a simulated printer\n" +
            "                                  (printing, preparing, paused, finished, failed, idle, offline)\n" +
            "  BambuBar.exe --widget compact|full\n" +
            "                                  start with the desktop widget visible\n" +
            "                                  (compact: progress % and time left; full: the whole mini panel)\n" +
            "  BambuBar.exe --glass            enable the Windows 11 glass (acrylic) widget backdrop\n" +
            "  BambuBar.exe --connect <host> <serial> <access-code>\n" +
            "                                  connect to a printer/emulator without saving anything\n" +
            "  BambuBar.exe --probe <host> <serial> <access-code> [seconds]\n" +
            "                                  diagnose a connection, then exit\n" +
            "  BambuBar.exe --insecure-tls     skip certificate checks (emulator/self-signed testing only)\n" +
            "  BambuBar.exe --version, --help\n");
    }

    public static string Version()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString(3) ?? "dev";
        return $"{version} · Windows · {Environment.OSVersion}";
    }

    // MARK: - Lifecycle

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // A second launch asks the first one to show its window, then exits (like clicking a Dock icon).
        _singleInstance = new Mutex(initiallyOwned: true, PipeName + "-mutex", out bool isFirst);
        if (!isFirst)
        {
            TrySignalFirstInstance();
            Shutdown();
            return;
        }

        _trayOnly = Environment.GetCommandLineArgs().Contains("--tray");

        _store = new PrinterStore();
        _store.Start();

        // Resolve the configured language before any window is built ("auto" follows the OS language).
        Loc.Set(_store.Settings.LanguageSetting);

        // Resolve the configured light/dark/auto theme before any window is built (Auto = Windows system appearance).
        ThemeEngine.Current.SetMode(_store.Settings.ThemeModeSetting);

        _notifier = new Notifier(_store, (title, body, isError) =>
            _tray?.ShowToast(body, isError ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info));
        _notifier.Attach(_store);

        _power = new PowerManager(
            suspend: () => { foreach (var s in _store!.Sessions) s.Suspend(); },
            resume: () => { foreach (var s in _store!.Sessions) s.Refresh(); });

        _tray = new TrayController(_store, OpenPanel, TogglePanel, OpenMainWindow, () => Shutdown(), ToggleWidget, WidgetIsActive,
            () => WidgetVisible, () => _store!.Settings.WidgetEnabled, SetWidgetEnabled,
            () => _store!.Settings.WidgetOpacityResolved, SetWidgetOpacity,
            () => _store!.Settings.WidgetTopmost, SetWidgetTopmost,
            () => PanelVisible);

        // Build the mini panel now (hidden) so the first tray click is a plain Show/Activate
        // instead of paying full construction + first layout on the user's click.
        _panelWindow = new PanelWindow(_store!, OpenMainWindow, () => Shutdown(), () => WidgetScreenRect);
        new System.Windows.Interop.WindowInteropHelper(_panelWindow).EnsureHandle(); // HwndSource exists before first show
        _panelWindow.Refresh(force: true); // populate the body before it is ever shown

        StartPipeListener();

        // Decide the first visible surface.
        if (!_trayOnly)
        {
            bool firstRun = _store.Sessions.Count == 0 && !_store.IsDemo;
            if (firstRun || _store.IsDemo)
                OpenMainWindow();
        }

        if (StartWithGlass)
            _store!.Settings.WidgetOpacity = 62; // the legacy "--glass" look, now an opacity level

        // The widget starts shown when the CLI asked for it or the user left it enabled last run.
        if (StartWithWidget)
            ToggleWidget(StartWithWidgetSize);
        else if (_store.Settings.WidgetEnabled)
            ToggleWidget(_store.Settings.WidgetSizeIndex == 1 ? WidgetSize.Full : WidgetSize.Compact);
    }

    /// <summary>
    /// Teardown must never crash: at system shutdown the network stack and message pump are already
    /// dying, and an exception escaping OnExit is an unhandled fault (WER crash).
    /// Order matters: stop the store while the tray icon is still alive — the final phase change
    /// raises Changed → tray refresh, and the 10/27 crashes were exactly that refresh hitting an
    /// already-disposed icon (NotifyIcon NRE). Then dispose the tray. Every step is isolated, and
    /// the network teardown gets a bounded wait so a wedged socket can't stall the shutdown.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { _notifier?.Detach(); } catch { }
        try { _power?.Dispose(); } catch { }
        if (_store is not null)
        {
            try { _store.Stop(); } catch { }
            try
            {
                var done = _store.DisposeAsync().AsTask();
                if (!done.Wait(3000))
                {
                    // The socket is wedged on a dying network; abandon it — the process exits anyway.
                }
            }
            catch
            {
                // Never crash on exit.
            }
        }
        try { _widgetWindow?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { ThemeEngine.Current.Shutdown(); } catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        base.OnExit(e);
    }

    // MARK: - Windows

    public void OpenPanel()
    {
        if (_panelWindow is { } panel)
        {
            panel.Show();
            panel.Activate();
            return;
        }
        // Fallback for paths where pre-creation didn't happen (e.g. the first-instance handoff).
        _panelWindow = new PanelWindow(_store!, OpenMainWindow, () => Shutdown(), () => WidgetScreenRect);
        _panelWindow.Refresh(force: true);
        _panelWindow.Show();
        _panelWindow.Activate();
    }

    /// <summary>Show the mini panel if it is hidden, hide it if it is up (tray menu toggle).</summary>
    public void TogglePanel()
    {
        if (_panelWindow is { IsVisible: true } panel)
            panel.Hide();
        else
            OpenPanel();
    }

    public void OpenMainWindow()
    {
        if (_mainWindow is { } main)
        {
            main.Show();
            main.Activate();
            return;
        }
        _mainWindow = new MainWindow(_store!, _tray!, OpenPanel, () => Shutdown());
        _mainWindow.Closed += (_, _) => _mainWindow = null;
        _mainWindow.Show();
    }

    // MARK: - Widget

    /// <summary>
    /// Show the desktop widget at the requested size (enabling it). Hiding it is done through
    /// <see cref="SetWidgetEnabled"/> ("Widget → Show widget"), since Compact/Full are size picks.
    /// The window is kept around (pre-initialized, like the panel) so re-showing is instant.
    /// </summary>
    public void ToggleWidget(WidgetSize size)
    {
        int opacity = _store.Settings.WidgetOpacityResolved;
        bool topmost = _store.Settings.WidgetTopmost;
        if (_widgetWindow is { } w)
        {
            // A different size needs a rebuild (different width); opacity/topmost do not — they
            // apply in place on the existing layered window.
            if (w.Size != size)
            {
                w.Close();
                _widgetWindow = null;
            }
            else
            {
                w.SetFillOpacity(opacity);
                w.Topmost = topmost;
            }
        }
        if (_widgetWindow is null)
        {
            _widgetWindow = new WidgetWindow(_store!, OpenMainWindow, opacity, topmost);
            new System.Windows.Interop.WindowInteropHelper(_widgetWindow).EnsureHandle();
        }
        _widgetSize = size;
        if (_widgetWindow.Size != size)
            _widgetWindow.SetSize(size);
        _widgetWindow.Show();
        _widgetWindow.Refresh(force: true);
        _store.Settings.WidgetEnabled = true;
        _store.Settings.WidgetSizeIndex = size == WidgetSize.Full ? 1 : 0;
        _store.Settings.Save();
    }

    public bool WidgetIsActive(WidgetSize size) => _widgetWindow is { IsVisible: true } && _widgetSize == size;

    /// <summary>True while the widget is on screen (drives the tray's single-click suppression).</summary>
    public bool WidgetVisible => _widgetWindow is { IsVisible: true };

    /// <summary>Enable (show) or disable (hide) the widget; persisted so it survives restarts.</summary>
    public void SetWidgetEnabled(bool on)
    {
        _store.Settings.WidgetEnabled = on;
        _store.Settings.Save();
        if (on)
            ToggleWidget(_store.Settings.WidgetSizeIndex == 1 ? WidgetSize.Full : WidgetSize.Compact);
        else if (_widgetWindow is { } w)
            w.Hide();
    }

    /// <summary>Set the widget fill opacity (0–100%); applied in place when the widget is alive.</summary>
    public void SetWidgetOpacity(int percent)
    {
        _store.Settings.WidgetOpacity = Math.Clamp(percent, 0, 100);
        _store.Settings.Save();
        if (_widgetWindow is { } w)
            w.SetFillOpacity(percent);
    }

    /// <summary>Toggle "always on top"; applied in place when the widget is alive.</summary>
    public void SetWidgetTopmost(bool on)
    {
        _store.Settings.WidgetTopmost = on;
        _store.Settings.Save();
        if (_widgetWindow is { } w)
            w.Topmost = on;
    }

    /// <summary>True while the mini panel is on screen (tray menu check).</summary>
    public bool PanelVisible => _panelWindow is { IsVisible: true };

    /// <summary>
    /// The widget's screen rectangle while it is visible, so the mini panel can stack above it
    /// instead of opening underneath (the widget is topmost and owns the corner).
    /// </summary>
    public System.Windows.Rect? WidgetScreenRect =>
        _widgetWindow is { IsVisible: true } w && w.ActualHeight > 1
            ? new System.Windows.Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight)
            : null;

    // MARK: - Single instance

    private void StartPipeListener()
    {
        _pipeThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var line = reader.ReadLine();
                    if (line == "open")
                        Dispatcher.BeginInvoke(OpenMainWindow);
                    else if (line == "panel")
                        Dispatcher.BeginInvoke(OpenPanel);
                }
                catch
                {
                    // The app is shutting down, or the pipe was torn down; try again briefly.
                    System.Threading.Thread.Sleep(50);
                }
            }
        })
        { IsBackground = true, Name = "BambuBar.IPC" };
        _pipeThread.Start();
    }

    private static void TrySignalFirstInstance()
    {
        // A second launch forwards an intent to the running instance: "--panel" opens the mini
        // panel, anything else opens the main window (like clicking the Dock icon).
        string command = Environment.GetCommandLineArgs().Contains("--panel") ? "panel" : "open";
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            writer.Flush();
        }
        catch
        {
            // No one there; the mutex said there was, but the pipe is gone — nothing to do.
        }
    }
}

/// <summary>
/// Job lifecycle notifications, the Windows port of Notifier.swift: finished, failed/canceled,
/// paused and (opt-in) started — each gated by the settings toggles.
/// </summary>
public sealed class Notifier
{
    private readonly PrinterStore _store;
    private readonly Action<string, string, bool> _post;
    private readonly Dictionary<PrinterSession, JobState> _previous = new();
    private readonly object _gate = new();

    public Notifier(PrinterStore store, Action<string, string, bool> post)
    {
        _store = store;
        _post = post;
    }

    /// <summary>Hook the store: state transitions are diffed on every change, so sessions added later are covered.</summary>
    public void Attach(PrinterStore store)
    {
        store.Changed += () => OnChanged();
    }

    public void Detach()
    {
        _store.Changed -= OnChanged;
    }

    private void OnChanged()
    {
        if (_store.IsDemo) return; // The simulated printer shouldn't post real notifications.

        lock (_gate)
        {
            foreach (var session in _store.Sessions)
            {
                var current = session.LastStatus?.State ?? JobState.Idle;
                JobState previous = _previous.TryGetValue(session, out var p) ? p : JobState.Idle;
                if (current == previous) continue;
                _previous[session] = current;

                var status = session.LastStatus;
                if (status is null) continue;
                var settings = _store.Settings;
                string name = session.Config?.Name ?? "";
                var job = status.JobName.Length > 0 ? status.JobName : Loc.T("notif.job_default");

                switch (current)
                {
                    case JobState.Finished when previous != JobState.Finished && settings.NotifyFinished:
                        Post(Loc.T("notif.finished_title"), $"{job} · {name}", isError: false);
                        break;
                    case JobState.Failed when previous != JobState.Failed && settings.NotifyFailed:
                        string reason = status.MostSevereIssue?.Message ?? "";
                        Post(status.Canceled ? Loc.T("notif.stopped_title") : Loc.T("notif.failed_title"),
                            (reason.Length > 0 ? reason + " · " : "") + job + " · " + name, isError: true);
                        break;
                    case JobState.Paused when previous != JobState.Paused && settings.NotifyPaused:
                        Post(Loc.T("notif.paused_title"), $"{(status.StageText ?? Loc.T("state.paused"))} · {job} · {name}", isError: false);
                        break;
                    case JobState.Printing or JobState.Preparing when !previous.IsActive() && settings.NotifyStarted:
                        Post(Loc.T("notif.started_title"), $"{job} · {name}", isError: false);
                        break;
                }
            }
        }
    }

    private void Post(string title, string body, bool isError)
    {
        // Marshal to the UI thread so the tray can show it safely.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) _post(title, body, isError);
        else if (dispatcher.CheckAccess()) _post(title, body, isError);
        else dispatcher.BeginInvoke(() => _post(title, body, isError));
    }
}

internal static class NativeConsole
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "AllocConsole")]
    private static extern bool AllocConsoleNative();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stdHandle);

    public static bool ConsoleAttached()
    {
        try { return GetConsoleWindow() != IntPtr.Zero; }
        catch { return false; }
    }

    /// <summary>Allocates a console and re-binds .NET's standard streams to it (a WinExe starts with none).</summary>
    public static void AllocConsole()
    {
        AllocConsoleNative();
        try
        {
            var outHandle = new System.IO.FileStream(GetStdHandle(StdOutputHandle), System.IO.FileAccess.Write);
            Console.SetOut(new System.IO.StreamWriter(outHandle) { AutoFlush = true });
            var errHandle = new System.IO.FileStream(GetStdHandle(StdErrorHandle), System.IO.FileAccess.Write);
            Console.SetError(new System.IO.StreamWriter(errHandle) { AutoFlush = true });
        }
        catch
        {
            // If rebinding fails, Console falls back and the CLI output is lost — acceptable for --help.
        }
    }
}
