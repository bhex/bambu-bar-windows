using System.Globalization;
using System.Text.Json;

namespace BambuBar.Core.I18n;

/// <summary>
/// Localizes user-facing strings. Catalogs are flat JSON maps (key → text) in
/// <c>Resources/loc/&lt;lang&gt;.json</c> next to the assembly. English is the base language and
/// the fallback for anything a catalog leaves out; a missing key returns the key itself so a
/// forgotten string is visible instead of silently blank.
/// Printer-state, stage and speed wording mirrors Bambu Lab's own terminology (as shown in
/// Bambu Studio) in every language.
/// </summary>
public static class Loc
{
    public const string Fallback = "en";

    /// <summary>Languages offered in the UI, in display order. "auto" is resolved in code.</summary>
    public static readonly string[] Supported =
    {
        "en", "de", "fr", "es", "it", "pt", "nl", "sv", "pl", "ru", "fi", "ja", "ko", "zh-CN",
    };

    /// <summary>Each language's own name, shown in the language picker.</summary>
    public static readonly Dictionary<string, string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["de"] = "Deutsch", ["fr"] = "Français", ["es"] = "Español",
        ["it"] = "Italiano", ["pt"] = "Português", ["nl"] = "Nederlands", ["sv"] = "Svenska",
        ["pl"] = "Polski", ["ru"] = "Русский", ["fi"] = "Suomi", ["ja"] = "日本語",
        ["ko"] = "한국어", ["zh-CN"] = "简体中文",
    };

    private static readonly object Gate = new();
    private static string _current = Fallback;
    private static bool _loaded;
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on any thread) after the active language actually changed.</summary>
    public static event Action? Changed;

    public static string Current { get { lock (Gate) return _current; } }

    /// <summary>Switches language; no-op when already active. Unknown codes fall back to English.</summary>
    public static void Set(string? code)
    {
        string resolved = Resolve(code);
        lock (Gate)
        {
            if (string.Equals(resolved, _current, StringComparison.OrdinalIgnoreCase)) return;
            _current = resolved;
        }
        Changed?.Invoke();
    }

    /// <summary>Best supported match for the OS language (UI culture first, then culture).</summary>
    public static string Auto()
    {
        foreach (var culture in new[] { CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture })
        {
            string? two = culture.TwoLetterISOLanguageName;
            if (two is { Length: 2 } && Supported.Contains(two, StringComparer.OrdinalIgnoreCase)) return two;
            if (Supported.Contains(culture.Name, StringComparer.OrdinalIgnoreCase)) return culture.Name;
        }
        return Fallback;
    }

    /// <summary>True when <paramref name="key"/> exists for the active language (or English).</summary>
    public static bool Has(string key) => T(key) != key;

    /// <summary>Looks up <paramref name="key"/>: active language, then English, then the key itself.</summary>
    public static string T(string key)
    {
        lock (Gate)
        {
            LoadAll();
            if (Catalogs.TryGetValue(_current, out var catalog) && catalog.TryGetValue(key, out var value))
                return value;
            if (Catalogs.TryGetValue(Fallback, out var english) && english.TryGetValue(key, out value))
                return value;
        }
        return key;
    }

    /// <summary><see cref="T"/> plus <c>{0}</c>-style formatting.</summary>
    public static string Tf(string key, params object[] args)
    {
        string text = T(key);
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    private static string Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Fallback;
        code = code.Trim();
        if (code.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Auto();
        if (Supported.Contains(code, StringComparer.OrdinalIgnoreCase)) return code;
        // "zh" → "zh-CN", "pt-BR" → "pt": match on the primary subtag before giving up.
        string primary = code.Split('-')[0];
        foreach (string lang in Supported)
            if (lang.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase)) return lang;
        if (Supported.Contains(primary, StringComparer.OrdinalIgnoreCase)) return primary;
        return Fallback;
    }

    private static void LoadAll()
    {
        if (_loaded) return;
        _loaded = true;
        var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "loc");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            string code = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in doc.RootElement.EnumerateObject())
                    map[property.Name] = property.Value.GetString() ?? "";
                Catalogs[code] = map;
            }
            catch
            {
                // A broken catalog is skipped; the English fallback (or the key) still works.
            }
        }
    }
}
