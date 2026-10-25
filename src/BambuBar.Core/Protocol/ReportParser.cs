using System.Text.Json;
using BambuBar.Core.Models;

namespace BambuBar.Core.Protocol;

/// <summary>Turns the merged report into a <see cref="PrinterStatus"/>.</summary>
public static class ReportParser
{
    private static int ClampedPercent(int v) => Math.Max(0, Math.Min(100, v));

    public static PrinterStatus From(ReportDocument doc, HmsCatalog hms)
    {
        var p = doc.Print;
        var s = new PrinterStatus { UpdatedAt = DateTime.Now };
        s.State = (JsonTools.GetString(JsonTools.Get(p, "gcode_state")) ?? "IDLE").FromGcodeState();
        s.StageId = JsonTools.GetInt(JsonTools.Get(p, "stg_cur")) ?? JsonTools.GetInt(JsonTools.Get(JsonTools.GetDictionary(JsonTools.Get(p, "stage")), "_id"));
        if (s.StageId == 0 && JsonTools.GetString(JsonTools.Get(p, "print_type")) == "idle" && !s.State.IsActive()) s.StageId = 255;

        s.TaskId = FirstNonEmpty(JsonTools.GetString(JsonTools.Get(p, "task_id")), JsonTools.GetString(JsonTools.Get(p, "subtask_id")));
        s.JobName = CleanJobName(JsonTools.GetString(JsonTools.Get(p, "subtask_name")) ?? "", JsonTools.GetString(JsonTools.Get(p, "gcode_file")) ?? "");
        s.Progress = ClampedPercent(JsonTools.GetInt(JsonTools.Get(p, "mc_percent")) ?? 0);
        s.RemainingMinutes = Math.Max(0, JsonTools.GetInt(JsonTools.Get(p, "mc_remaining_time")) ?? 0);
        s.Layer = JsonTools.GetInt(JsonTools.Get(p, "layer_num")) ?? 0;
        s.TotalLayers = JsonTools.GetInt(JsonTools.Get(p, "total_layer_num")) ?? 0;

        // Temperatures: the newer `device` block packs current (low 16 bits) and target (high 16 bits).
        var device = JsonTools.GetDictionary(JsonTools.Get(p, "device"));
        var extruders = JsonTools.GetList(JsonTools.GetDictionary(JsonTools.Get(device, "extruder"))?["info"]);
        if (JsonTools.GetInt(extruders is { Count: > 0 } ? JsonTools.GetDictionary(extruders[0])?["temp"] : null) is { } packedExtruder)
        {
            s.Nozzle = Unpack(packedExtruder);
        }
        else
        {
            s.Nozzle = new Temperature(JsonTools.GetDouble(JsonTools.Get(p, "nozzle_temper")) ?? 0, JsonTools.GetDouble(JsonTools.Get(p, "nozzle_target_temper")) ?? 0);
        }

        var bedPacked = JsonTools.GetInt(JsonTools.GetDictionary(JsonTools.GetDictionary(JsonTools.Get(device, "bed"))?["info"])?["temp"])
            ?? JsonTools.GetInt(JsonTools.Get(device, "bed_temp"));
        if (bedPacked is { } bp)
        {
            s.Bed = Unpack(bp);
        }
        else
        {
            s.Bed = new Temperature(JsonTools.GetDouble(JsonTools.Get(p, "bed_temper")) ?? 0, JsonTools.GetDouble(JsonTools.Get(p, "bed_target_temper")) ?? 0);
        }

        var ctcPacked = JsonTools.GetInt(JsonTools.GetDictionary(JsonTools.GetDictionary(JsonTools.Get(device, "ctc"))?["info"])?["temp"]);
        if (ctcPacked is { } cp)
        {
            s.Chamber = Unpack(cp).Current;
        }
        else
        {
            var chamberTemp = JsonTools.GetDouble(JsonTools.GetDictionary(JsonTools.Get(p, "info"))?["temp"]) ?? JsonTools.GetDouble(JsonTools.Get(p, "chamber_temper"));
            if (chamberTemp is { } ct) s.Chamber = ct;
        }

        if (s.Chamber is { } c && (c <= 0 || c > 120)) s.Chamber = null;

        // Fans: the `airduct` parts already report percent; the legacy fields are 0-15.
        var airductParts = JsonTools.GetList(JsonTools.GetDictionary(JsonTools.Get(device, "airduct"))?["parts"]);
        if (airductParts is { Count: > 0 })
        {
            foreach (var partValue in airductParts)
            {
                var part = JsonTools.GetDictionary(partValue);
                if (part is null || JsonTools.GetInt(JsonTools.Get(part, "id")) is not { } id || JsonTools.GetInt(JsonTools.Get(part, "state")) is not { } state) continue;
                var pct = ClampedPercent(state & 0xFF);
                switch (id >> 4)
                {
                    case 1: s.PartFan = pct; break;
                    case 2: s.AuxFan = pct; break;
                    case 3: s.ExhaustFan = pct; break;
                }
            }
        }
        else
        {
            s.PartFan = Fan(JsonTools.Get(p, "cooling_fan_speed"));
            s.AuxFan = Fan(JsonTools.Get(p, "big_fan1_speed"));
            var fan2 = Fan(JsonTools.Get(p, "big_fan2_speed"));
            if (fan2 is { } f2 && f2 > 0) s.ExhaustFan = f2;
        }

        s.PartFan ??= Fan(JsonTools.Get(p, "cooling_fan_speed"));
        s.HeatbreakFan = Fan(JsonTools.Get(p, "heatbreak_fan_speed"));

        s.Speed = Enum.IsDefined((SpeedLevel)(JsonTools.GetInt(JsonTools.Get(p, "spd_lvl")) ?? 2))
            ? (SpeedLevel)(JsonTools.GetInt(JsonTools.Get(p, "spd_lvl")) ?? 2)
            : SpeedLevel.Standard;

        if (JsonTools.GetList(JsonTools.Get(p, "lights_report")) is { } lights)
        {
            foreach (var l in lights)
            {
                var d = JsonTools.GetDictionary(l);
                if (d is not null && JsonTools.GetString(JsonTools.Get(d, "node")) == "chamber_light")
                {
                    s.ChamberLightOn = JsonTools.GetString(JsonTools.Get(d, "mode")) != "off";
                    break;
                }
            }
        }

        if (TryUInt64(JsonTools.GetString(JsonTools.Get(p, "stat")), out var statBits)) s.DoorOpen = (statBits & 0x0080_0000) != 0;

        if (JsonTools.GetString(JsonTools.Get(p, "wifi_signal")) is { } wifi &&
            int.TryParse(wifi.Replace("dBm", ""), out var dbm)) s.WifiSignal = dbm;

        if (TryUInt64(JsonTools.GetString(JsonTools.Get(p, "fun")), out var funBits)) s.ControlsLocked = (funBits & 0x2000_0000) != 0;

        s.HasSDCard = p.TryGetValue("sdcard", out var sd) ? (bool?)JsonTools.GetBool(sd) : null;
        s.PlateIndex = JsonTools.GetInt(JsonTools.Get(p, "plate_idx"));

        var ipcam = JsonTools.GetDictionary(JsonTools.Get(p, "ipcam"));
        if (ipcam is not null && JsonTools.GetString(JsonTools.Get(ipcam, "rtsp_url")) is { } url)
            s.LiveViewEnabled = url != "disable" && url.Length > 0;

        var nozzles = JsonTools.GetList(JsonTools.GetDictionary(JsonTools.Get(device, "nozzle"))?["info"]);
        if (nozzles is { Count: > 0 })
        {
            var n = JsonTools.GetDictionary(nozzles[0]);
            s.NozzleDiameter = JsonTools.GetDouble(JsonTools.Get(n, "diameter"));
            s.NozzleType = JsonTools.GetString(JsonTools.Get(n, "type"));
        }
        else
        {
            s.NozzleDiameter = JsonTools.GetDouble(JsonTools.Get(p, "nozzle_diameter"));
            s.NozzleType = JsonTools.GetString(JsonTools.Get(p, "nozzle_type"));
        }

        var upgrade = JsonTools.GetDictionary(JsonTools.Get(p, "upgrade_state"));
        if (upgrade is not null && JsonTools.GetInt(JsonTools.Get(upgrade, "new_version_state")) == 1)
            s.UpdateAvailable = true;

        if (doc.Modules.TryGetValue("ota", out var ota)) s.Firmware = ota.Version.Length > 0 ? ota.Version : null;

        ParseFilament(p, device, doc, s);
        s.Issues = Issues(p, hms, s.State).ToList();
        s.Canceled = s.State == JobState.Failed && WasCanceled(doc);
        return s;
    }

    // MARK: - Filament

    private static void ParseFilament(
        IReadOnlyDictionary<string, object?> p, IReadOnlyDictionary<string, object?>? device, ReportDocument doc, PrinterStatus s)
    {
        var amsRoot = JsonTools.GetDictionary(JsonTools.Get(p, "ams"));
        var units = JsonTools.GetList(JsonTools.Get(amsRoot, "ams")) ?? new List<object?>();
        ulong? existBits = ParseHex64(JsonTools.GetString(JsonTools.Get(amsRoot, "tray_exist_bits")));
        ulong? rfidBits = ParseHex64(JsonTools.GetString(JsonTools.Get(amsRoot, "tray_is_bbl_bits")));

        var parsed = new List<AmsUnit>();
        foreach (var unitValue in units)
        {
            var u = JsonTools.GetDictionary(unitValue);
            if (u is null || JsonTools.GetInt(JsonTools.Get(u, "id")) is not { } id) continue;
            var kind = DetectAmsKind(id, JsonTools.GetString(JsonTools.Get(u, "info")), doc.Modules);
            var trays = JsonTools.GetList(JsonTools.Get(u, "tray")) ?? new List<object?>();

            var spools = new List<Spool>();
            foreach (var trayValue in trays)
            {
                var t = JsonTools.GetDictionary(trayValue);
                if (t is null || JsonTools.GetInt(JsonTools.Get(t, "id")) is not { } slot) continue;
                var global = id >= 128 ? id : id * 4 + slot;
                var bit = id >= 128 ? 16 + (id - 128) : id * 4 + slot;
                var spool = SpoolFrom(t, global, id, slot);
                if (existBits is { } eb && (eb & (1UL << bit)) == 0) spool.IsLoaded = false;
                if (rfidBits is { } rb) spool.IsBambu = (rb & (1UL << bit)) != 0 && spool.IsLoaded;
                if (!spool.IsLoaded) { spool.Material = ""; spool.Name = ""; spool.Remain = null; }
                spools.Add(spool);
            }
            spools.Sort((a, b) => a.Slot.CompareTo(b.Slot));

            int? humidity = JsonTools.GetInt(JsonTools.Get(u, "humidity_raw"));
            if (humidity is { } h && (h < 1 || h > 100)) humidity = null;
            int? level = JsonTools.GetInt(JsonTools.Get(u, "humidity"));
            if (level is { } l && (l < 1 || l > 5)) level = null;
            double? temp = JsonTools.GetDouble(JsonTools.Get(u, "temp"));
            if (temp is { } tv && (tv <= 0 || tv > 100)) temp = null;

            parsed.Add(new AmsUnit
            {
                Id = id,
                Kind = kind,
                Humidity = humidity,
                HumidityLevel = level is { } lv ? 6 - lv : null,
                Temperature = temp,
                DryingMinutesLeft = JsonTools.GetInt(JsonTools.Get(u, "dry_time")) ?? 0,
                Spools = spools,
            });
        }
        parsed.Sort((a, b) => a.Id.CompareTo(b.Id));
        s.Ams = parsed;

        object? virtualTray = JsonTools.GetList(JsonTools.Get(p, "vir_slot")) is { Count: > 0 } first ? first[0] : JsonTools.GetDictionary(JsonTools.Get(p, "vt_tray"));
        if (virtualTray is not null)
        {
            var v = JsonTools.GetDictionary(virtualTray) ?? new Dictionary<string, object?>();
            var ext = SpoolFrom(v, Spool.ExternalId, 255, 0);
            ext.IsLoaded = (JsonTools.GetString(JsonTools.Get(v, "tray_type")) ?? "").Length > 0;
            ext.IsBambu = ext.IsLoaded &&
                          (JsonTools.GetString(JsonTools.Get(v, "tag_uid")) is { } uid && !uid.All(ch => ch == '0'));
            s.ExternalSpool = ext;
        }

        // Active slot: the extruder's `snow` (unit << 8 | slot) on newer firmware, else `tray_now`.
        var extruders = JsonTools.GetList(JsonTools.GetDictionary(JsonTools.Get(device, "extruder"))?["info"]);
        var snow = JsonTools.GetInt(extruders is { Count: > 0 } ? JsonTools.GetDictionary(extruders[0])?["snow"] : null);
        if (snow is { } sw && sw != 0xFFFF)
        {
            int unit = sw >> 8, slot = sw & 0xFF;
            if (unit == 254 || unit == 255)
                s.ActiveSpoolId = slot == 255 ? null : Spool.ExternalId;
            else if (unit >= 128)
                s.ActiveSpoolId = unit;
            else
                s.ActiveSpoolId = unit * 4 + (slot & 0x3);
        }
        else if (JsonTools.GetInt(JsonTools.Get(amsRoot, "tray_now")) is { } now)
        {
            s.ActiveSpoolId = now switch
            {
                255 => null,
                254 => Spool.ExternalId,
                _ => now,
            };
        }

        // Only meaningful while filament is actually in use.
        if (!s.State.IsActive() && s.ActiveSpoolId is { } aid && s.ActiveSpool?.IsLoaded != true)
            s.ActiveSpoolId = null;
    }

    private static Spool SpoolFrom(IReadOnlyDictionary<string, object?> t, int id, int unit, int slot)
    {
        var material = JsonTools.GetString(JsonTools.Get(t, "tray_type")) ?? "";
        int? state = JsonTools.GetInt(JsonTools.Get(t, "state"));
        var loaded = material.Length > 0;
        if (state is { } st && st > 3)
            loaded = (st & 0x01) != 0 && (st & 0x08) != 0 || st == 9;
        var color = JsonTools.GetString(JsonTools.Get(t, "tray_color")) ?? "00000000";
        var colors = (JsonTools.GetList(JsonTools.Get(t, "cols")) ?? new List<object?>())
            .Select(x => JsonTools.GetString(x))
            .Where(x => x is { Length: >= 6 })
            .Cast<string>()
            .ToList();
        int? remain = JsonTools.GetInt(JsonTools.Get(t, "remain"));
        if (remain is { } r && (r < 0 || r > 100)) remain = null;
        var tag = JsonTools.GetString(JsonTools.Get(t, "tag_uid")) ?? "";

        return new Spool
        {
            Id = id,
            Unit = unit,
            Slot = slot,
            Material = material,
            Name = JsonTools.GetString(JsonTools.Get(t, "tray_sub_brands")) ?? "",
            Color = color,
            Colors = colors.Count > 0 ? colors : new List<string> { color },
            Remain = remain,
            IsLoaded = loaded,
            IsBambu = tag.Length > 0 && !tag.All(ch => ch == '0'),
        };
    }

    private static AmsKind DetectAmsKind(int id, string? info, IReadOnlyDictionary<string, (string Product, string Version)> modules)
    {
        if (id >= 128 || modules.ContainsKey($"n3s/{id}")) return AmsKind.Ht;
        if (modules.ContainsKey($"n3f/{id}")) return AmsKind.Pro2;
        if (modules.ContainsKey($"ams_f1/{id}")) return AmsKind.Lite;
        if (modules.ContainsKey($"ams/{id}")) return AmsKind.Ams;
        if (ParseHex64(info) is { } bits)
        {
            switch (bits & 0xF)
            {
                case 2: return AmsKind.Lite;
                case 3: return AmsKind.Pro2;
                case 4: return AmsKind.Ht;
            }
        }
        return AmsKind.Ams;
    }

    // MARK: - Issues

    private static List<PrinterIssue> Issues(IReadOnlyDictionary<string, object?> p, HmsCatalog hms, JobState state)
    {
        var outList = new List<PrinterIssue>();
        foreach (var entryValue in JsonTools.GetList(JsonTools.Get(p, "hms")) ?? new List<object?>())
        {
            var entry = JsonTools.GetDictionary(entryValue);
            if (entry is null || JsonTools.GetInt(JsonTools.Get(entry, "attr")) is not { } attrRaw || JsonTools.GetInt(JsonTools.Get(entry, "code")) is not { } codeRaw) continue;
            uint a = (uint)attrRaw, c = (uint)codeRaw;
            if ((c & 0xFFFF) < 0x4000 && (c >> 16) == 0) continue;
            var key = $"{a:X8}{c:X8}";
            var pretty = $"HMS_{a >> 16:X4}_{a & 0xFFFF:X4}_{c >> 16:X4}_{c & 0xFFFF:X4}";
            var rawSeverity = (int)(c >> 16);
            var severity = rawSeverity is >= 1 and <= 4 ? (IssueSeverity)rawSeverity : IssueSeverity.Common;
            outList.Add(new PrinterIssue
            {
                Id = pretty,
                Severity = severity,
                Message = hms.HmsText(key) ?? $"The printer reported {pretty}.",
                WikiUrl = HmsCatalog.WikiUrl(pretty),
            });
        }

        if (JsonTools.GetInt(JsonTools.Get(p, "print_error")) is { } errorRaw && errorRaw != 0)
        {
            uint e = (uint)errorRaw;
            var key = $"{e:X8}";
            // 0300_400C and 0500_400E are the printer's echo of a user cancel, not a fault.
            if (key is not ("0300400C" or "0500400E"))
            {
                var pretty = $"{e >> 16:X4}_{e & 0xFFFF:X4}";
                var level = (e & 0xF000) >> 12;
                var severity = level == 0x4 ? IssueSeverity.Fatal : level == 0x8 ? IssueSeverity.Serious : IssueSeverity.Common;
                if (!outList.Any(i => i.Id.EndsWith(pretty)))
                {
                    outList.Add(new PrinterIssue
                    {
                        Id = pretty,
                        Severity = severity,
                        Message = hms.ErrorText(key) ?? $"The printer reported error {pretty}.",
                        WikiUrl = HmsCatalog.WikiUrl(pretty),
                    });
                }
            }
        }

        return outList.OrderBy(i => i.Severity).ToList();
    }

    /// <summary>True when <c>print_error</c> is the code for a job the user cancelled.</summary>
    public static bool WasCanceled(ReportDocument doc)
    {
        if (JsonTools.GetInt(JsonTools.Get(doc.Print, "print_error")) is not { } raw) return false;
        var key = $"{(uint)raw:X8}";
        return key is "0300400C" or "0500400E";
    }

    // MARK: - Helpers

    /// <summary>Bambu Studio names jobs after the 3MF; strip path and extensions for display.</summary>
    public static string CleanJobName(string name, string file)
    {
        var n = name.Trim();
        if (n.Length == 0)
        {
            n = System.IO.Path.GetFileName(file);
            if (n.StartsWith("plate_")) n = "";
        }
        foreach (var ext in new[] { ".gcode.3mf", ".3mf", ".gcode" })
        {
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                n = n[..^ext.Length];
                break;
            }
        }
        // Bambu Studio turns spaces into underscores when it names the job after the project file.
        if (!n.Contains(' ')) n = n.Replace('_', ' ');
        return n;
    }

    private static Temperature Unpack(int packed)
    {
        // Plain values (no target packed in) stay below 0x10000; a heater that's off reports target 0.
        return new Temperature(packed & 0xFFFF, (packed >> 16) & 0xFFFF);
    }

    private static int? Fan(object? value)
    {
        var raw = JsonTools.GetInt(value);
        if (raw is not { } r) return null;
        if (r <= 15) return (int)Math.Round(r / 15.0 * 100);
        return Math.Min(100, (int)Math.Round(r / 255.0 * 100));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (v is { Length: > 0 } && v != "0") return v;
        }
        return null;
    }

    private static bool TryUInt64(string? hex, out ulong value)
    {
        value = 0;
        return hex is { Length: > 0 } && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static ulong? ParseHex64(string? hex)
    {
        if (hex is { Length: > 0 } &&
            ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return null;
    }
}
