using BambuBar.Core.I18n;

namespace BambuBar.Core.Models;

/// <summary>What the printer is doing, folded from <c>print.gcode_state</c>.</summary>
public enum JobState
{
    Idle,
    Preparing,
    Printing,
    Paused,
    Finished,
    Failed,
}

public static partial class JobStateExtensions
{
    public static JobState FromGcodeState(this string? gcodeState)
    {
        switch ((gcodeState ?? "").ToUpperInvariant())
        {
            case "PREPARE" or "SLICING": return JobState.Preparing;
            case "RUNNING": return JobState.Printing;
            case "PAUSE": return JobState.Paused;
            case "FINISH": return JobState.Finished;
            case "FAILED": return JobState.Failed;
            default: return JobState.Idle;
        }
    }

    public static bool IsActive(this JobState state) =>
        state is JobState.Preparing or JobState.Printing or JobState.Paused;

    public static string Title(this JobState state) => state switch
    {
        JobState.Idle => Loc.T("state.idle"),
        JobState.Preparing => Loc.T("state.preparing"),
        JobState.Printing => Loc.T("state.printing"),
        JobState.Paused => Loc.T("state.paused"),
        JobState.Finished => Loc.T("state.finished"),
        JobState.Failed => Loc.T("state.failed"),
        _ => state.ToString(),
    };
}

/// <summary>The four motion profiles (<c>spd_lvl</c>).</summary>
public enum SpeedLevel
{
    Silent = 1,
    Standard = 2,
    Sport = 3,
    Ludicrous = 4,
}

public static class SpeedLevelExtensions
{
    public static string Title(this SpeedLevel level) => level switch
    {
        SpeedLevel.Silent => Loc.T("speed.silent"),
        SpeedLevel.Standard => Loc.T("speed.standard"),
        SpeedLevel.Sport => Loc.T("speed.sport"),
        SpeedLevel.Ludicrous => Loc.T("speed.ludicrous"),
        _ => level.ToString(),
    };

    /// <summary>Nominal speed relative to Standard, as Bambu Studio shows it.</summary>
    public static int Percent(this SpeedLevel level) => level switch
    {
        SpeedLevel.Silent => 50,
        SpeedLevel.Standard => 100,
        SpeedLevel.Sport => 124,
        SpeedLevel.Ludicrous => 166,
        _ => 100,
    };
}

/// <summary>Current/target pair. Target is zero when the heater is off.</summary>
public readonly record struct Temperature(double Current, double Target)
{
    public bool IsHeating => Target > 0;
    public bool IsAtTarget => Target > 0 && Math.Abs(Current - Target) <= 2;

    public override string ToString() =>
        Target > 0 ? $"{Current:0}° / {Target:0}°" : $"{Current:0}°";
}

/// <summary>One filament slot: an AMS tray or the external spool holder.</summary>
public class Spool
{
    public const int ExternalId = 254;

    /// <summary>Global slot number: <c>unit * 4 + slot</c> for AMS units 0-3, <c>128 + n</c> for AMS HT, 254 for the external spool.</summary>
    public int Id { get; set; }
    public int Unit { get; set; }
    public int Slot { get; set; }
    /// <summary>"PLA", "PETG"…; empty when the slot is empty or unknown.</summary>
    public string Material { get; set; } = "";
    /// <summary>"PLA Matte"…; empty for third-party spools.</summary>
    public string Name { get; set; } = "";
    /// <summary><c>RRGGBBAA</c>.</summary>
    public string Color { get; set; } = "00000000";
    /// <summary>More than one entry for multicolour spools.</summary>
    public List<string> Colors { get; set; } = new();
    /// <summary>Percent remaining, null when the printer can't tell (non-RFID spools).</summary>
    public int? Remain { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsBambu { get; set; }

    public bool IsExternal => Id == ExternalId;

    public string RemainText
    {
        get
        {
            if (!IsLoaded) return "Empty";
            return Remain is { } r ? $"{r}%" : "—";
        }
    }

    public string DisplayName => Name.Length > 0 ? Name : Material;

    /// <summary>"A1", "A2"… like the printer's screen; "Ext" for the external spool.</summary>
    public string Label
    {
        get
        {
            if (IsExternal) return "Ext";
            if (Unit >= 128) return $"HT{Unit - 127}";
            return $"{(char)('A' + Math.Min(Unit, 25))}{Slot + 1}";
        }
    }
}

public enum AmsKind
{
    Ams,
    Lite,
    Pro2,
    Ht,
}

public static class AmsKindExtensions
{
    public static string Title(this AmsKind kind) => kind switch
    {
        AmsKind.Ams => "AMS",
        AmsKind.Lite => "AMS Lite",
        AmsKind.Pro2 => "AMS 2 Pro",
        AmsKind.Ht => "AMS HT",
        _ => kind.ToString(),
    };
}

public class AmsUnit
{
    public int Id { get; set; }
    public AmsKind Kind { get; set; }
    /// <summary>Relative humidity in percent (<c>humidity_raw</c>), when the unit reports it.</summary>
    public int? Humidity { get; set; }
    /// <summary>1 (dry) to 5 (wet), Bambu Studio's scale.</summary>
    public int? HumidityLevel { get; set; }
    public double? Temperature { get; set; }
    public int DryingMinutesLeft { get; set; }
    public List<Spool> Spools { get; set; } = new();

    /// <summary>"AMS A", "AMS 2 Pro B"…</summary>
    public string Title => Id >= 128
        ? $"{Kind.Title()} {Id - 127}"
        : $"{Kind.Title()} {(char)('A' + Math.Min(Id, 25))}";
}

public enum IssueSeverity
{
    Fatal = 1,
    Serious = 2,
    Common = 3,
    Info = 4,
}

public static class IssueSeverityExtensions
{
    public static string Title(this IssueSeverity severity) => severity switch
    {
        IssueSeverity.Fatal => Loc.T("issue.fatal"),
        IssueSeverity.Serious => Loc.T("issue.serious"),
        IssueSeverity.Common => Loc.T("issue.common"),
        _ => Loc.T("issue.info"),
    };
}

/// <summary>A Health Management System message or the current print error.</summary>
public class PrinterIssue
{
    /// <summary><c>HMS_0300_0100_0001_0007</c> or <c>0700_8006</c>.</summary>
    public string Id { get; set; } = "";
    public IssueSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public string? WikiUrl { get; set; }
}

/// <summary>Everything Bambu Bar shows about the printer, derived from the merged MQTT report.</summary>
public class PrinterStatus
{
    public JobState State { get; set; } = JobState.Idle;
    public int? StageId { get; set; }
    public string JobName { get; set; } = "";
    public int Progress { get; set; }
    public int RemainingMinutes { get; set; }
    public int Layer { get; set; }
    public int TotalLayers { get; set; }
    public Temperature Nozzle { get; set; } = new(0, 0);
    public Temperature Bed { get; set; } = new(0, 0);
    public double? Chamber { get; set; }
    /// <summary>Fan speeds in percent.</summary>
    public int? PartFan { get; set; }
    public int? AuxFan { get; set; }
    public int? ExhaustFan { get; set; }
    public int? HeatbreakFan { get; set; }
    public SpeedLevel Speed { get; set; } = SpeedLevel.Standard;
    public bool? ChamberLightOn { get; set; }
    public bool? DoorOpen { get; set; }
    public int? WifiSignal { get; set; }
    public List<AmsUnit> Ams { get; set; } = new();
    public Spool? ExternalSpool { get; set; }
    public int? ActiveSpoolId { get; set; }
    public List<PrinterIssue> Issues { get; set; } = new();
    /// <summary>The printer refuses unsigned print commands (Developer Mode off).</summary>
    public bool ControlsLocked { get; set; }
    public string? Firmware { get; set; }
    public double? NozzleDiameter { get; set; }
    public string? NozzleType { get; set; }
    public bool? LiveViewEnabled { get; set; }
    public bool? HasSDCard { get; set; }
    public int? PlateIndex { get; set; }
    public bool UpdateAvailable { get; set; }
    /// <summary>The printer's id for the current job (<c>task_id</c>, else <c>subtask_id</c>).</summary>
    public string? TaskId { get; set; }
    /// <summary>The job ended because someone stopped it (print_error 0300_400C), not because of a fault.</summary>
    public bool Canceled { get; set; }
    /// <summary>When this status was last refreshed from the printer.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>"Canceled" for a job someone stopped, otherwise the state's own title.</summary>
    public string StateTitle => State == JobState.Failed && Canceled ? Loc.T("detail.canceled") : State.Title();

    /// <summary>The printer's stage, localized when a catalog has it, else the built-in English name.</summary>
    public string? StageText
    {
        get
        {
            if (StageId is null or 255 or (-1)) return null;
            int id = StageId.Value;
            if (Loc.Has($"stage.{id}")) return Loc.T($"stage.{id}");
            return StageNames.TryGetValue(id, out var name) ? name : null;
        }
    }

    /// <summary>The line under the job name: "Heating hotend", "Paused: filament ran out", "Layer 170 of 305"…</summary>
    public string DetailText => State switch
    {
        JobState.Printing => StageText is { } st && StageId != 0 ? st
            : TotalLayers > 0 ? Loc.Tf("detail.layer_of", Layer, TotalLayers)
            : Loc.T("state.printing"),
        JobState.Preparing => Loc.T("detail.preparing"),
        JobState.Paused => StageText ?? Loc.T("state.paused"),
        JobState.Finished => Loc.T("state.finished"),
        JobState.Failed => Canceled ? Loc.T("detail.canceled") : (Issues.Count > 0 ? Issues[0].Message : Loc.T("detail.failed")),
        _ => StageText ?? Loc.T("detail.ready"),
    };

    public Spool? ActiveSpool
    {
        get
        {
            if (ActiveSpoolId is not { } id) return null;
            if (id == Spool.ExternalId) return ExternalSpool;
            foreach (var unit in Ams)
                foreach (var spool in unit.Spools)
                    if (spool.Id == id) return spool;
            return null;
        }
    }

    public IEnumerable<Spool> AllSpools
    {
        get
        {
            foreach (var unit in Ams) foreach (var spool in unit.Spools) yield return spool;
            if (ExternalSpool is { } ext) yield return ext;
        }
    }

    public PrinterIssue? MostSevereIssue =>
        Issues.Count > 0 ? Issues.MinBy(i => i.Severity) : null;

    /// <summary>Estimated completion, from the remaining minutes at <c>UpdatedAt</c>.</summary>
    public DateTime? EstimatedEnd =>
        State.IsActive() && RemainingMinutes > 0 ? UpdatedAt.AddMinutes(RemainingMinutes) : null;

    /// <summary>Progress expected at <c>date</c>, extrapolated linearly over the remaining time.</summary>
    public double ProjectedProgressAt(DateTime date)
    {
        var now = Progress / 100.0;
        if (State != JobState.Printing || EstimatedEnd is not { } end || end <= UpdatedAt) return now;
        var fraction = Math.Clamp((date - UpdatedAt).TotalSeconds / (end - UpdatedAt).TotalSeconds, 0, 1);
        return Math.Min(0.99, now + (1 - now) * fraction);
    }

    public static readonly Dictionary<int, string> StageNames = new()
    {
        [0] = "Printing", [1] = "Auto bed leveling", [2] = "Preheating heatbed", [3] = "Vibration compensation",
        [4] = "Changing filament", [5] = "M400 pause", [6] = "Paused: filament ran out", [7] = "Heating hotend",
        [8] = "Calibrating extrusion", [9] = "Scanning bed surface", [10] = "Inspecting first layer",
        [11] = "Identifying build plate", [12] = "Calibrating Micro Lidar", [13] = "Homing toolhead",
        [14] = "Cleaning nozzle tip", [15] = "Checking extruder temperature", [16] = "Paused by user",
        [17] = "Paused: front cover fell off", [18] = "Calibrating Micro Lidar", [19] = "Calibrating flow ratio",
        [20] = "Paused: nozzle temperature fault", [21] = "Paused: heatbed temperature fault", [22] = "Unloading filament",
        [23] = "Paused: skipped steps", [24] = "Loading filament", [25] = "Calibrating motor noise",
        [26] = "Paused: AMS offline", [27] = "Paused: heatbreak fan too slow", [28] = "Paused: chamber temperature fault",
        [29] = "Cooling chamber", [30] = "Paused by G-code", [31] = "Motor noise showoff",
        [32] = "Paused: nozzle clumping", [33] = "Paused: cutter error", [34] = "Paused: first layer error",
        [35] = "Paused: nozzle clog", [36] = "Measuring motion precision", [37] = "Enhancing motion precision",
        [38] = "Measuring motion accuracy", [39] = "Calibrating nozzle offset", [40] = "High-temperature bed leveling",
        [41] = "Checking quick-release lever", [42] = "Checking door and top cover", [43] = "Calibrating laser",
        [44] = "Checking platform", [45] = "Checking BirdsEye camera", [46] = "Calibrating BirdsEye camera",
        [47] = "Bed leveling, phase 1", [48] = "Bed leveling, phase 2", [49] = "Heating chamber",
        [50] = "Cooling heatbed", [51] = "Printing calibration lines", [52] = "Checking material",
        [53] = "Calibrating Live View camera", [54] = "Waiting for heatbed temperature", [55] = "Checking material position",
        [56] = "Calibrating cutter offset", [57] = "Measuring surface", [58] = "Thermal preconditioning",
        [59] = "Homing blade holder", [60] = "Calibrating camera offset", [61] = "Calibrating blade holder",
        [62] = "Hotend pick-and-place test", [63] = "Waiting for chamber temperature", [64] = "Preparing hotend",
        [65] = "Calibrating clumping detection", [66] = "Purifying chamber air", [67] = "Measuring rotary attachment",
        [68] = "Moving to purge chute", [69] = "Cooling nozzle", [70] = "Centering toolhead",
        [71] = "Arc fitting", [72] = "Detecting hotend type", [73] = "Detecting build plate alignment",
        [74] = "Checking heatbed for objects", [75] = "Checking heatbed underside", [76] = "Pre-extruding",
        [77] = "Preparing AMS",
    };
}

/// <summary>The last job the printer ran, kept so an idle printer can still say how its last print went.</summary>
public class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Outcome Result { get; set; }
    public int TotalLayers { get; set; }
    public string? FilamentColor { get; set; }
    public string? FilamentMaterial { get; set; }
    /// <summary>The printer's task id, to tell two jobs with the same name apart.</summary>
    public string? TaskId { get; set; }
    /// <summary>Which printer ran it.</summary>
    public string? PrinterSerial { get; set; }

    public enum Outcome
    {
        Running,
        Finished,
        Failed,
        Canceled,
        /// <summary>The app wasn't running when it ended, so how it ended isn't known.</summary>
        Ended,
    }

    public string ResultTitle => Result switch
    {
        Outcome.Running => Loc.T("outcome.running"),
        Outcome.Finished => Loc.T("state.finished"),
        Outcome.Failed => Loc.T("state.failed"),
        Outcome.Canceled => Loc.T("detail.canceled"),
        _ => Loc.T("outcome.ended"),
    };

    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
}

/// <summary>Where to find the printer. The access code lives in the Credential Manager, not here.</summary>
public class PrinterConfig
{
    public string Host { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SSDP model code ("N7" for the P2S).</summary>
    public string ModelCode { get; set; } = "";

    public string ModelName => PrinterModels.Name(ModelCode, Serial);
}

/// <summary>A printer some slicer already knows the access code for.</summary>
public class SlicerPrinter
{
    public string Serial { get; set; } = "";
    /// <summary>Best first. Bambu Studio keeps the code it got from the cloud (<c>access_code</c>) and the one typed in
    /// for LAN mode (<c>user_access_code</c>); either may be stale, so both are kept to try in turn.</summary>
    public List<string> AccessCodes { get; set; } = new();
    public string? Host { get; set; }
    public string? Name { get; set; }
    /// <summary>"Bambu Studio", "OrcaSlicer"…</summary>
    public string Source { get; set; } = "";
}

public static class PrinterModels
{
    public static string Name(string code, string serial = "")
    {
        switch (code.ToUpperInvariant())
        {
            case "N7": return "P2S";
            case "C11": return "P1P";
            case "C12": return "P1S";
            case "BL-P001": return "X1 Carbon";
            case "BL-P002": return "X1";
            case "C13" or "BL-P003": return "X1E";
            case "N1" or "A12" or "A04": return "A1 mini";
            case "N2S" or "A11": return "A1";
            case "O1D": return "H2D";
            case "O1E" or "O2D": return "H2D Pro";
            case "O1C" or "O1C2": return "H2C";
            case "O1S": return "H2S";
            case "N6": return "X2D";
            case "N9": return "A2L";
            default:
                switch (serial.Length >= 3 ? serial[..3] : serial)
                {
                    case "22E": return "P2S";
                    case "01P": return "P1S";
                    case "01S": return "P1P";
                    case "00M": return "X1 Carbon";
                    case "03W": return "X1E";
                    case "039": return "A1";
                    case "030": return "A1 mini";
                    case "093": return "H2S";
                    case "094": return "H2D";
                    default: return "Bambu Lab printer";
                }
        }
    }
}
