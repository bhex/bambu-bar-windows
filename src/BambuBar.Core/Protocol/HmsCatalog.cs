using System.Text.Json;

namespace BambuBar.Core.Protocol;

/// <summary>
/// English texts for HMS codes and print errors, P2S wording where it differs.
/// Generated from ha-bambulab's catalogue (MIT) by the upstream <c>scripts/make-hms.py</c>.
/// </summary>
public sealed class HmsCatalog
{
    public static readonly HmsCatalog Shared = new();

    private readonly object _gate = new();
    private Dictionary<string, string> _hms = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public string? HmsText(string key)
    {
        Load();
        return _hms.TryGetValue(key, out var text) ? text : null;
    }

    public string? ErrorText(string key)
    {
        Load();
        return _errors.TryGetValue(key, out var text) ? text : null;
    }

    public static string WikiUrl(string code) => "https://wiki.bambulab.com/en/hms/home";

    private void Load()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "hms_en.json");
            if (!File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("hms", out var hms) && hms.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in hms.EnumerateObject())
                        _hms[property.Name] = property.Value.GetString() ?? "";
                }
                if (root.TryGetProperty("error", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in errors.EnumerateObject())
                        _errors[property.Name] = property.Value.GetString() ?? "";
                }
            }
            catch (Exception)
            {
                // The catalog is a nice-to-have; the app still works without pretty texts.
            }
        }
    }
}
