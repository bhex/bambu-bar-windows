using System.Text.Json;

namespace BambuBar.Core.Protocol;

/// <summary>
/// The printer sends one full <c>print</c> report after <c>pushall</c>, then only the fields that changed.
/// <see cref="ReportDocument"/> keeps the merged picture.
/// </summary>
public sealed class ReportDocument
{
    private Dictionary<string, object?> _print = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Product, string Version)> _modules = new(StringComparer.Ordinal);

    /// <summary>True once a full (<c>gcode_state</c>-bearing) report has arrived.</summary>
    public bool HasFullReport { get; private set; }

    public IReadOnlyDictionary<string, object?> Print => _print;

    /// <summary>From <c>get_version</c>: module name → (product name, software version).</summary>
    public IReadOnlyDictionary<string, (string Product, string Version)> Modules => _modules;

    /// <summary>Merges one message from <c>device/&lt;serial&gt;/report</c>. Returns true if the print state changed.</summary>
    public bool Apply(IReadOnlyDictionary<string, object?> message)
    {
        var changed = false;
        if (message.TryGetValue("print", out var pv) && JsonTools.GetDictionary(pv) is { } dict)
        {
            var command = JsonTools.GetString(JsonTools.Get(dict, "command"));
            // Command acknowledgements echo the request back; only status pushes describe the printer.
            if (command is null or "push_status")
            {
                if (JsonTools.GetInt(JsonTools.Get(dict, "msg")) == 0 && dict.ContainsKey("gcode_state"))
                {
                    // A full report replaces everything, so unplugged AMS units and cleared errors go away.
                    HasFullReport = true;
                    _print = dict;
                }
                else
                {
                    _print = Merge(_print, dict);
                }
                changed = true;
            }
        }

        if (message.TryGetValue("info", out var iv) && JsonTools.GetDictionary(iv) is { } info &&
            JsonTools.GetString(JsonTools.Get(info, "command")) == "get_version" &&
            JsonTools.GetList(JsonTools.Get(info, "module")) is { } mods)
        {
            foreach (var mv in mods)
            {
                if (JsonTools.GetDictionary(mv) is not { } m || JsonTools.GetString(JsonTools.Get(m, "name")) is not { } name) continue;
                _modules[name] = (
                    JsonTools.GetString(JsonTools.Get(m, "product_name")) ?? "",
                    JsonTools.GetString(JsonTools.Get(m, "sw_ver")) ?? "");
            }
            changed = true;
        }

        return changed;
    }

    public void Reset()
    {
        _print = new Dictionary<string, object?>(StringComparer.Ordinal);
        _modules.Clear();
        HasFullReport = false;
    }

    /// <summary>
    /// Recursive merge. Arrays of objects carrying an <c>id</c> (or, for <c>lights_report</c>, a <c>node</c>)
    /// merge entry by entry; other arrays are replaced.
    /// </summary>
    public static Dictionary<string, object?> Merge(
        Dictionary<string, object?> baseDict, Dictionary<string, object?> newDict)
    {
        var outDict = new Dictionary<string, object?>(baseDict, StringComparer.Ordinal);
        foreach (var (key, newValue) in newDict)
        {
            var idKey = key == "lights_report" ? "node" : "id";
            if (newValue is Dictionary<string, object?> newD &&
                outDict.TryGetValue(key, out var existing) && existing is Dictionary<string, object?> oldD)
            {
                outDict[key] = Merge(oldD, newD);
            }
            else if (newValue is List<object?> newList &&
                     outDict.TryGetValue(key, out var existingList) && existingList is List<object?> oldList &&
                     newList.Count > 0 &&
                     newList.All(x => x is Dictionary<string, object?> d &&
                                      d.TryGetValue(idKey, out var v) && v != null))
            {
                var merged = new List<object?>(oldList);
                foreach (var item in newList)
                {
                    var dict = (Dictionary<string, object?>)item!;
                    var id = dict[idKey]!.ToString() ?? "";
                    var index = merged.FindIndex(m =>
                        m is Dictionary<string, object?> md &&
                        md.TryGetValue(idKey, out var mv) && (mv?.ToString() ?? "") == id);
                    if (index >= 0) merged[index] = Merge((Dictionary<string, object?>)merged[index]!, dict);
                    else merged.Add(dict);
                }
                outDict[key] = merged;
            }
            else
            {
                outDict[key] = newValue;
            }
        }
        return outDict;
    }
}
