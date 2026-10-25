using BambuBar.Core.Discovery;
using BambuBar.Core.Demo;
using BambuBar.Core.Models;
using BambuBar.Core.Protocol;
using BambuBar.Core.Sessions;
using BambuBar.Core.Store;

namespace BambuBar.Core.App;

/// <summary>
/// The app's single source of truth: the printers, which one is in view, job history and settings.
///
/// The window and the tray panel show the *selected* printer. The tray icon shows <see cref="TraySession"/>,
/// which follows the settings choice ("automatic" picks whichever printer is busy).
/// UI-agnostic: events are raised on thread-pool threads; the UI layer marshals.
/// </summary>
public sealed class PrinterStore : IAsyncDisposable
{
    public sealed record Toast(string Message, bool IsError);

    public IReadOnlyList<PrinterSession> Sessions { get; private set; } = Array.Empty<PrinterSession>();
    public string? SelectedSerial { get; private set; }
    public List<JobRecord> History { get; private set; } = new();
    public List<DiscoveredPrinter> Discovered { get; private set; } = new();
    public List<SlicerPrinter> SlicerPrinters { get; private set; } = new();
    public Toast? CurrentToast { get; private set; }
    public bool IsDemo { get; private set; }

    public SettingsStore Settings { get; }

    /// <summary>Raised on thread-pool threads; the UI marshals.</summary>
    public event Action? Changed;
    public event Action<Toast>? Toasted;

    private readonly PrinterDiscovery _discovery = new();
    private readonly object _gate = new();
    private DemoDriver? _demo;
    private DateTime _lastToastShown;
    private string? _lastToastText;

    public PrinterStore()
    {
        Settings = SettingsStore.Load();
        _discovery.Found += OnDiscovered;
    }

    // MARK: - Lifecycle

    public void Start()
    {
        if (DemoDriverRequested is { } scenario)
        {
            StartDemo(scenario);
            return;
        }


        if (DeveloperConnection is { } dev)
        {
            // `--connect <host> <serial> <code>`: a throwaway session (the local emulator, a borrowed
            // printer). Nothing is saved: not the printer, not the access code, not the history.
            IsDemo = true;
            var config = new PrinterConfig { Host = dev.Host, Serial = dev.Serial, Name = "P2S Emulator", ModelCode = "N7" };
            var session = Add(config);
            session.Start(config, dev.Code, insecure: InsecureTls);
            return;
        }

        foreach (var config in Settings.Printers)
        {
            var session = Add(config);
            var code = CredentialStore.AccessCode(config.Serial);
            if (code is not null)
                session.Start(config, code, insecure: InsecureTls);
        }

        History = HistoryStore.Load();
        if (Sessions.Count > 0 && History.Any(j => j.PrinterSerial is null))
        {
            for (var i = 0; i < History.Count; i++)
                if (History[i].PrinterSerial is null)
                    History[i].PrinterSerial = Sessions[0].Config.Serial;
            HistoryStore.Save(History);
        }

        if (SelectedSerial is null || Session(SelectedSerial) is null)
            SelectedSerial = Sessions.FirstOrDefault()?.Config.Serial;

        _discovery.Start();
        SlicerPrinters = SlicerImport.Scan();
        RaiseChanged();
    }

    public void Stop()
    {
        foreach (var session in Sessions) session.Stop();
        _discovery.Stop();
    }

    // MARK: - Demo / developer hooks (port of the macOS command-line flags)

    public static DemoDriver.Scenario? DemoDriverRequested
    {
        get
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            var index = args.IndexOf("--demo");
            if (index < 0) return null;
            if (index + 1 < args.Count && Enum.TryParse<DemoDriver.Scenario>(args[index + 1], ignoreCase: true, out var s))
                return s;
            return DemoDriver.Scenario.Printing;
        }
    }

    public static (string Host, string Serial, string Code)? DeveloperConnection
    {
        get
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            var i = args.IndexOf("--connect");
            if (i < 0 || i + 3 >= args.Count) return null;
            return (args[i + 1], args[i + 2], args[i + 3]);
        }
    }

    public bool InsecureTls { get; } =
        Environment.GetCommandLineArgs().Contains("--insecure-tls");

    private void StartDemo(DemoDriver.Scenario scenario)
    {
        IsDemo = true;
        _demo = new DemoDriver(scenario);
        _demo.Status += (status, _) =>
        {
            _demoSession?.RaiseDemoStatus(status);
            RaiseChanged();
        };
        _demo.Phase += (phase, reason) => RaiseChanged();
        var config = new PrinterConfig { Host = "demo", Serial = "DEMO0001", Name = "P2S (demo)", ModelCode = "N7" };
        _demoSession = Add(config);
        _demo.Start();
        RaiseChanged();
    }

    private PrinterSession? _demoSession;

    // MARK: - Sessions

    public PrinterSession? Session(string? serial)
    {
        if (serial is null) return null;
        lock (_gate)
        {
            return Sessions.FirstOrDefault(s => s.Config.Serial == serial);
        }
    }

    /// <summary>The printer the window and panel show.</summary>
    public PrinterSession? Current =>
        Session(SelectedSerial) ?? Sessions.FirstOrDefault();

    /// <summary>The printer the tray icon follows: the one chosen in Settings, or automatically the busiest.</summary>
    public PrinterSession? TraySession
    {
        get
        {
            if (Settings.MenuBarSerial is { } chosen)
            {
                var session = Session(chosen);
                if (session is not null) return session;
            }

            int Rank(PrinterSession s)
            {
                var status = s.LastStatus;
                if (status is null) return 0;
                if (status.MostSevereIssue is { Severity: IssueSeverity.Fatal or IssueSeverity.Serious } && status.State.IsActive()) return 4;
                return status.State switch
                {
                    JobState.Paused => 4,
                    JobState.Printing or JobState.Preparing => 3,
                    JobState.Failed or JobState.Finished => 1,
                    _ => 0,
                };
            }

            var best = Sessions.OrderByDescending(Rank).FirstOrDefault();
            if (best is not null && Rank(best) >= 3) return best;
            return Current;
        }
    }

    public bool HasMultiplePrinters
    {
        get
        {
            lock (_gate)
            {
                return Sessions.Count > 1;
            }
        }
    }

    private PrinterSession Add(PrinterConfig config)
    {
        var session = new PrinterSession();
        session.AttachConfig(config);
        session.PhaseChanged += (phase, reason) =>
        {
            TrackPhase(session, phase);
            RaiseChanged();
        };
        session.StatusChanged += status =>
        {
            TrackJob(session, status);
            RaiseChanged();
        };
        session.Acknowledged += (command, ok, reason) =>
        {
            if (!ok)
            {
                var message = reason is { Length: > 0 } r ? $"{command}: {r}" : $"The printer refused “{command}”.";
                if (statusIsLocked(session))
                    message = "Controls need LAN Only mode with Developer Mode on the printer (Settings → Network).";
                ShowToast(message, isError: !ok);
            }
            else
            {
                ShowToast($"“{command}” accepted.", isError: false);
            }
            RaiseChanged();
        };
        session.Heartbeat += () =>
        {
            // A quiet, unchanging printer is still alive; refresh "updated Xs ago" occasionally.
            if (DateTime.Now - _lastToastShown > TimeSpan.FromMinutes(5))
                RaiseChanged();
        };

        lock (_gate)
        {
            Sessions = [.. Sessions, session];
        }
        return session;
    }

    private static bool statusIsLocked(PrinterSession session) =>
        session.LastStatus?.ControlsLocked ?? false;

    // MARK: - Printers

    /// <summary>Saves a printer (new, or a new address/code for a known one) and connects to it.</summary>
    public void Connect(PrinterConfig config, string accessCode, IEnumerable<string>? alternatives = null)
    {
        var code = accessCode.Trim();
        if (!IsDemo)
        {
            // Throwaway (emulator) sessions keep the code out of the Credential Manager entirely.
            // Credential save failure is non-fatal: the code is still passed in-memory below.
            if (!CredentialStore.SetAccessCode(code, config.Serial))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BambuBar] Warning: could not persist access code for {config.Serial} in Credential Manager.");
            }
        }

        PrinterSession session;
        lock (_gate)
        {
            var existing = Sessions.FirstOrDefault(s => s.Config.Serial == config.Serial);
            if (existing is not null)
            {
                session = existing;
            }
            else
            {
                session = Add(config);
                if (!IsDemo)
                {
                    Settings.Printers = [.. Settings.Printers, config];
                    Settings.Save();
                }
            }
        }

        Select(config.Serial);
        session.Start(config, code, insecure: InsecureTls);
        RaiseChanged();
    }

    public void Rename(string serial, string name)
    {
        var session = Session(serial);
        if (session is null) return;
        var trimmed = name.Trim();
        var displayName = trimmed.Length > 0 ? trimmed : serial;
        session.UpdateName(displayName);
        if (!IsDemo)
        {
            var config = Settings.Printers.FirstOrDefault(c => c.Serial == serial);
            if (config is not null)
            {
                config.Name = displayName;
                Settings.Save();
            }
        }
        RaiseChanged();
    }

    public void Forget(string serial)
    {
        lock (_gate)
        {
            var session = Sessions.FirstOrDefault(s => s.Config.Serial == serial);
            if (session is null) return;
            session.Stop();
            if (!IsDemo)
            {
                CredentialStore.RemoveAccessCode(serial);
                Settings.Printers = Settings.Printers.Where(c => c.Serial != serial).ToList();
                if (Settings.SelectedSerial == serial) Settings.SelectedSerial = Settings.Printers.FirstOrDefault()?.Serial;
                if (Settings.MenuBarSerial == serial) Settings.MenuBarSerial = null;
                Settings.Save();
            }
            Sessions = Sessions.Where(s => s.Config.Serial != serial).ToList();
            if (SelectedSerial == serial)
                SelectedSerial = Sessions.FirstOrDefault()?.Config.Serial;
        }
        RaiseChanged();
    }

    public void Select(string serial)
    {
        if (Session(serial) is null || SelectedSerial == serial) return;
        SelectedSerial = serial;
        if (!IsDemo)
        {
            Settings.SelectedSerial = serial;
            Settings.Save();
        }
        RaiseChanged();
    }

    public void Refresh()
    {
        _discovery.Search();
        Current?.Refresh();
    }

    public void SearchForPrinters()
    {
        _discovery.Search();
        SlicerPrinters = SlicerImport.Scan();
        RaiseChanged();
    }

    // MARK: - Commands (act on the selected printer)

    public void Pause() => Current?.Pause();
    public void Resume() => Current?.Resume();
    public void StopPrint() => Current?.StopPrint();
    public void ToggleLight()
    {
        var session = Current;
        if (session is null) return;
        var on = !(session.LastStatus?.ChamberLightOn ?? false);
        session.SetChamberLight(on);
    }
    public void SetSpeed(SpeedLevel level) => Current?.SetSpeed(level);

    // MARK: - Job tracking (port of the macOS store's history logic)

    private void TrackPhase(PrinterSession session, PrinterSession.Phase phase)
    {
        // Notifications are handled by the UI layer (it owns the toast surface); the store only tracks jobs.
    }

    private void TrackJob(PrinterSession session, PrinterStatus status)
    {
        var old = _previousStatuses.TryGetValue(session, out var prev) ? prev : null;
        _previousStatuses[session] = status;

        var key = (session.Config.Serial, status.TaskId ?? status.JobName);
        if (old is null)
        {
            if (status.State.IsActive())
                EnsureRunningJob(key, status, session);
            return;
        }

        if (old.State != status.State)
        {
            if (status.State is JobState.Finished or JobState.Failed)
            {
                FinishJob(key, status, session, canceled: WasCanceledDocument(status));
            }
            else if (status.State.IsActive() && !old.State.IsActive())
            {
                EnsureRunningJob(key, status, session);
            }
        }
    }

    private static bool WasCanceledDocument(PrinterStatus status) => status.Canceled;

    private readonly Dictionary<PrinterSession, PrinterStatus> _previousStatuses = new();

    private void EnsureRunningJob((string, string) key, PrinterStatus status, PrinterSession session)
    {
        lock (_gate)
        {
            var existing = History.FirstOrDefault(j =>
                j.PrinterSerial == key.Item1 && j.Result == JobRecord.Outcome.Running &&
                (j.TaskId == status.TaskId || j.Name == status.JobName));
            if (existing is null)
            {
                var spool = status.ActiveSpool;
                History.Insert(0, new JobRecord
                {
                    Name = status.JobName.Length > 0 ? status.JobName : "Print",
                    StartedAt = DateTime.Now,
                    Result = JobRecord.Outcome.Running,
                    TotalLayers = status.TotalLayers,
                    FilamentColor = spool?.Color,
                    FilamentMaterial = spool?.Material,
                    TaskId = status.TaskId,
                    PrinterSerial = key.Item1,
                });
                TrimHistory();
                HistoryStore.Save(History);
            }
        }
    }

    private void FinishJob((string, string) key, PrinterStatus status, PrinterSession session, bool canceled)
    {
        lock (_gate)
        {
            var record = History.FirstOrDefault(j =>
                j.PrinterSerial == key.Item1 && j.Result == JobRecord.Outcome.Running &&
                (j.TaskId == status.TaskId || j.Name == status.JobName));
            if (record is null)
            {
                var spool = status.ActiveSpool;
                History.Insert(0, new JobRecord
                {
                    Name = status.JobName.Length > 0 ? status.JobName : "Print",
                    StartedAt = status.UpdatedAt.AddMinutes(-Math.Max(1, status.RemainingMinutes)),
                    EndedAt = DateTime.Now,
                    Result = status.State == JobState.Failed
                        ? (canceled ? JobRecord.Outcome.Canceled : JobRecord.Outcome.Failed)
                        : JobRecord.Outcome.Finished,
                    TotalLayers = status.TotalLayers,
                    FilamentColor = spool?.Color,
                    FilamentMaterial = spool?.Material,
                    TaskId = status.TaskId,
                    PrinterSerial = key.Item1,
                });
            }
            else
            {
                record.EndedAt = DateTime.Now;
                record.Result = status.State == JobState.Failed
                    ? (canceled ? JobRecord.Outcome.Canceled : JobRecord.Outcome.Failed)
                    : JobRecord.Outcome.Finished;
                record.TotalLayers = status.TotalLayers;
            }
            TrimHistory();
            HistoryStore.Save(History);
        }
    }

    private void TrimHistory()
    {
        if (History.Count > 200)
            History = History.Take(200).ToList();
    }

    // MARK: - Discovery

    private void OnDiscovered(DiscoveredPrinter printer)
    {
        lock (_gate)
        {
            var existing = Discovered.FirstOrDefault(d => d.Serial == printer.Serial);
            if (existing is not null) Discovered.Remove(existing);
            Discovered.Insert(0, printer);
            if (Discovered.Count > 32) Discovered = Discovered.Take(32).ToList();
        }
        RaiseChanged();
    }

    // MARK: - Toasts

    private void ShowToast(string message, bool isError)
    {
        if (DateTime.Now - _lastToastShown < TimeSpan.FromSeconds(1) && _lastToastText == message) return;
        _lastToastShown = DateTime.Now;
        _lastToastText = message;
        CurrentToast = new Toast(message, isError);
        Toasted?.Invoke(CurrentToast);
        // Clear it after a few seconds so the UI can animate.
        Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_lastToastText == message)
                {
                    CurrentToast = null;
                    _lastToastText = null;
                    RaiseChanged();
                }
            }
        });
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        Stop();
        _discovery.Dispose();
        foreach (var session in Sessions)
            await session.DisposeAsync();
    }
}
