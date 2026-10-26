using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using BambuBar.Core.App;
using BambuBar.Core.Demo;
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
    private Thread? _pipeThread;
    private Mutex? _singleInstance;
    private bool _trayOnly;

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

        // Resolve the configured light/dark/auto theme before any window is built (Auto = Windows system appearance).
        ThemeEngine.Current.SetMode(_store.Settings.ThemeModeSetting);

        _notifier = new Notifier(_store, (title, body, isError) =>
            _tray?.ShowToast(body, isError ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info));
        _notifier.Attach(_store);

        _power = new PowerManager(
            suspend: () => { foreach (var s in _store!.Sessions) s.Suspend(); },
            resume: () => { foreach (var s in _store!.Sessions) s.Refresh(); });

        _tray = new TrayController(_store, OpenPanel, OpenMainWindow, () => Shutdown());

        StartPipeListener();

        // Decide the first visible surface.
        if (!_trayOnly)
        {
            bool firstRun = _store.Sessions.Count == 0 && !_store.IsDemo;
            if (firstRun || _store.IsDemo)
                OpenMainWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifier?.Detach();
        _power?.Dispose();
        _tray?.Dispose();
        ThemeEngine.Current.Shutdown();
        if (_store is not null)
        {
            _store.Stop();
            _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
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
        _panelWindow = new PanelWindow(_store!, OpenMainWindow, () => Shutdown());
        _panelWindow.Show();
        _panelWindow.Activate();
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
                var job = status.JobName.Length > 0 ? status.JobName : "the print";

                switch (current)
                {
                    case JobState.Finished when previous != JobState.Finished && settings.NotifyFinished:
                        Post("Print finished", $"{job} · {name}", isError: false);
                        break;
                    case JobState.Failed when previous != JobState.Failed && settings.NotifyFailed:
                        string reason = status.MostSevereIssue?.Message ?? "";
                        Post("Print " + (status.Canceled ? "stopped" : "failed"),
                            (reason.Length > 0 ? reason + " · " : "") + job + " · " + name, isError: true);
                        break;
                    case JobState.Paused when previous != JobState.Paused && settings.NotifyPaused:
                        Post("Print paused", $"{(status.StageText ?? "paused")} · {job} · {name}", isError: false);
                        break;
                    case JobState.Printing or JobState.Preparing when !previous.IsActive() && settings.NotifyStarted:
                        Post("Print started", $"{job} · {name}", isError: false);
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
