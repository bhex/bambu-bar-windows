using BambuBar.Core.Models;

namespace BambuBar.Core.Demo;

/// <summary>A mid-print P2S with an AMS 2 Pro, for previews, placeholders and the demo.</summary>
public static class PrinterStatusSamples
{
    public static PrinterStatus Sample()
    {
        var s = new PrinterStatus
        {
            State = JobState.Printing,
            StageId = 0,
            JobName = "Rear Dry Pod",
            Progress = 64,
            RemainingMinutes = 62,
            Layer = 170,
            TotalLayers = 305,
            Nozzle = new Temperature(220, 220),
            Bed = new Temperature(55, 55),
            Chamber = 38,
            PartFan = 90,
            AuxFan = 40,
            Speed = SpeedLevel.Standard,
            ChamberLightOn = true,
            DoorOpen = false,
            WifiSignal = -52,
            ActiveSpoolId = 3,
            Firmware = "01.03.00.00",
            NozzleDiameter = 0.4,
            NozzleType = "HS01",
            HasSDCard = true,
        };
        s.Ams =
        [
            new AmsUnit
            {
                Id = 0,
                Kind = AmsKind.Pro2,
                Humidity = 16,
                HumidityLevel = 1,
                Temperature = 28.4,
                Spools =
                [
                    new Spool { Id = 0, Unit = 0, Slot = 0, Material = "PLA", Name = "PLA Matte", Color = "F4EEE6FF", Colors = ["F4EEE6FF"], Remain = 81, IsLoaded = true, IsBambu = true },
                    new Spool { Id = 1, Unit = 0, Slot = 1, Material = "PLA", Name = "PLA Basic", Color = "1C1C1EFF", Colors = ["1C1C1EFF"], Remain = 39, IsLoaded = true, IsBambu = true },
                    new Spool { Id = 2, Unit = 0, Slot = 2, Material = "PETG", Name = "PETG HF", Color = "2E7D5BFF", Colors = ["2E7D5BFF"], Remain = 64, IsLoaded = true, IsBambu = true },
                    new Spool { Id = 3, Unit = 0, Slot = 3, Material = "PLA", Name = "PLA Silk+", Color = "E2763AFF", Colors = ["E2763AFF"], Remain = 22, IsLoaded = true, IsBambu = true },
                ],
            },
        ];
        return s;
    }
}

/// <summary>
/// <c>--demo [scenario]</c>: a simulated P2S, for trying the app without a printer.
/// Nothing is saved and no network is touched.
/// </summary>
public sealed class DemoDriver : IDisposable
{
    public enum Scenario
    {
        Printing,
        Preparing,
        Paused,
        Finished,
        Failed,
        Idle,
        Offline,
        Unconfigured,
    }

    public event Action<PrinterStatus, bool>? Status;
    public event Action<PrinterSessionPhase, string?>? Phase;

    private readonly object _gate = new();
    private PrinterStatus _status;
    private System.Threading.Timer? _timer;

    public DemoDriver(Scenario scenario)
    {
        RequestedScenario = scenario;
        _status = PrinterStatusSamples.Sample();
        switch (scenario)
        {
            case Scenario.Preparing:
                _status.State = JobState.Preparing;
                _status.Progress = 0;
                _status.RemainingMinutes = 0;
                _status.Layer = 0;
                _status.StageId = 1;
                break;
            case Scenario.Paused:
                _status.State = JobState.Paused;
                _status.StageId = 6;
                break;
            case Scenario.Finished:
                _status.State = JobState.Finished;
                _status.Progress = 100;
                _status.RemainingMinutes = 0;
                _status.Layer = _status.TotalLayers;
                break;
            case Scenario.Failed:
                _status.State = JobState.Failed;
                _status.Issues =
                [
                    new PrinterIssue
                    {
                        Id = "HMS_0300_0100_0001_0007",
                        Severity = IssueSeverity.Serious,
                        Message = "The print head is clogged. Clean the nozzle and try again.",
                    },
                ];
                break;
            case Scenario.Idle:
                _status.State = JobState.Idle;
                _status.JobName = "";
                _status.Progress = 0;
                _status.RemainingMinutes = 0;
                _status.Layer = 0;
                _status.ActiveSpoolId = null;
                _status.Nozzle = new Temperature(25, 0);
                _status.Bed = new Temperature(25, 0);
                _status.PartFan = 0;
                _status.AuxFan = 0;
                break;
        }
    }

    public Scenario RequestedScenario { get; }

    public void Start()
    {
        if (RequestedScenario is Scenario.Offline or Scenario.Unconfigured)
        {
            Phase?.Invoke(PrinterSessionPhase.Offline, RequestedScenario == Scenario.Offline ? "The printer isn't reachable on the network." : null);
            return;
        }
        Phase?.Invoke(PrinterSessionPhase.Online, null);
        Status?.Invoke(_status, false);
        if (RequestedScenario != Scenario.Printing) return;

        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    private int _tick;

    private void Tick()
    {
        lock (_gate)
        {
            _tick++;
            _status.Progress = Math.Min(99, _status.Progress + (1 / 120));
            _status.RemainingMinutes = Math.Max(1, _status.RemainingMinutes - (1 / 60));
            _status.Layer = Math.Min(_status.TotalLayers, _status.Layer + (1 / 900));
            _status.Nozzle = new Temperature(219 + _tick % 3, 220);
            _status.Bed = new Temperature(54 + _tick % 3, 55);
            var copy = Clone(_status);
            Status?.Invoke(copy, false);
        }
    }

    public void Pause() => Apply(state => state.State = JobState.Paused);
    public void Resume() => Apply(state => state.State = JobState.Printing);

    public void StopPrint()
    {
        Apply(state =>
        {
            state.State = JobState.Finished;
            state.RemainingMinutes = 0;
        });
        _timer?.Dispose();
    }

    public void SetLight(bool on) => Apply(state => state.ChamberLightOn = on);

    public void SetSpeed(SpeedLevel level) => Apply(state => state.Speed = level);

    private void Apply(Action<PrinterStatus> change)
    {
        lock (_gate)
        {
            change(_status);
            var copy = Clone(_status);
            Status?.Invoke(copy, false);
        }
    }

    private static PrinterStatus Clone(PrinterStatus s) =>
        System.Text.Json.JsonSerializer.Deserialize<PrinterStatus>(
            System.Text.Json.JsonSerializer.Serialize(s))!;

    public void Dispose() => _timer?.Dispose();
}

/// <summary>Phase as the demo driver reports it (matches <c>PrinterSession.Phase</c>).</summary>
public enum PrinterSessionPhase
{
    Idle,
    Connecting,
    Online,
    Offline,
    Unauthorized,
    Certificate,
}
