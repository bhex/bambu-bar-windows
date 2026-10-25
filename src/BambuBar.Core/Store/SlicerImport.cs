using System.Text;
using System.Text.Json;
using BambuBar.Core.Models;

namespace BambuBar.Core.Store;

/// <summary>
/// Reads access codes out of Bambu Studio's and OrcaSlicer's settings, so setup needs no typing.
///
/// Both slicers save <c>&lt;AppData&gt;/&lt;App&gt;/&lt;App&gt;.conf</c> (or <c>…/config/&lt;App&gt;.conf</c>) as
/// JSON — on Windows with an MD5 line after it — with one object per section: <c>access_code</c> and
/// <c>user_access_code</c> map serial → code, <c>ip_address</c> and <c>user_access_dev_ip</c> map
/// serial → IP (BambuStudio <c>DevManager.cpp</c>, <c>AppConfig.cpp</c>).
/// </summary>
public static class SlicerImport
{
    private static readonly (string Folder, string Title)[] KnownApps =
    {
        ("BambuStudio", "Bambu Studio"),
        ("BambuStudioBeta", "Bambu Studio Beta"),
        ("OrcaSlicer", "OrcaSlicer"),
        ("OrcaSlicerNightly", "OrcaSlicer Nightly"),
    };

    /// <summary>Every printer with a stored access code in this machine's slicers.</summary>
    public static List<SlicerPrinter> Scan()
    {
        var found = new List<SlicerPrinter>();
        foreach (var (folder, title) in KnownApps)
        {
            foreach (var url in CandidatePaths(folder))
            {
                foreach (var printer in Read(url, title))
                {
                    if (found.Any(f => f.Serial == printer.Serial)) continue;
                    found.Add(printer);
                }
            }
        }
        return found;
    }

    private static IEnumerable<string> CandidatePaths(string folder)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length == 0) yield break;
        // A bare "Orca" folder (older builds) maps to the OrcaSlicer config layout.
        var app = folder == "Orca" ? "OrcaSlicer" : folder;
        // Both the Electron layout (<app>/config/<conf>) and a flat layout are seen in the wild.
        yield return Path.Combine(appData, folder, "config", $"{folder}.conf");
        yield return Path.Combine(appData, folder, $"{folder}.conf");
    }

    /// <summary>Parses one settings file.</summary>
    public static List<SlicerPrinter> Read(string path, string source)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            // Drop anything after the JSON object (the "# MD5 checksum …" line some builds append).
            if (text.LastIndexOf('}') is { } end) text = text[..(end + 1)];
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            Dictionary<string, string> Section(string name)
            {
                var outDict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
                    return outDict;
                foreach (var property in element.EnumerateObject())
                {
                    var value = property.Value.GetString()?.Trim();
                    if (value is { Length: > 0 }) outDict[property.Name] = value;
                }
                return outDict;
            }

            var cloud = Section("access_code");
            var typed = Section("user_access_code");
            var ips = Section("ip_address");
            var typedIPs = Section("user_access_dev_ip");

            var serials = cloud.Keys.Concat(typed.Keys).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var printers = new List<SlicerPrinter>();
            foreach (var serial in serials)
            {
                var codes = new List<string>();
                foreach (var code in new[] { cloud.GetValueOrDefault(serial), typed.GetValueOrDefault(serial) })
                {
                    if (code is { Length: > 0 } && !codes.Contains(code)) codes.Add(code);
                }
                if (codes.Count == 0) continue;
                printers.Add(new SlicerPrinter
                {
                    Serial = serial,
                    AccessCodes = codes,
                    Host = typedIPs.GetValueOrDefault(serial) ?? ips.GetValueOrDefault(serial),
                    Source = source,
                });
            }
            return printers;
        }
        catch
        {
            return new List<SlicerPrinter>();
        }
    }
}
