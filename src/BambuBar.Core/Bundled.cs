using System.Reflection;

namespace BambuBar.Core;

/// <summary>
/// The data bundled with the app — localization catalogs, the HMS error catalog and the CA
/// certificates — is embedded in the assembly so the app can ship as a single executable.
/// Loose copies under <c>Resources/…</c> next to the executable take precedence, so a dev
/// build can swap data without recompiling.
/// </summary>
internal static class Bundled
{
    private static readonly Assembly Asm = typeof(Bundled).Assembly;
    private const string Prefix = "BambuBar.Core.Resources.";

    /// <summary>
    /// The text of <c>Resources/<paramref name="subPath"/></c>: the loose file next to the
    /// executable first (dev override), then the embedded copy. Null when neither exists.
    /// </summary>
    public static string? Read(string subPath)
    {
        var loose = Path.Combine(AppContext.BaseDirectory, subPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose))
        {
            try { return File.ReadAllText(loose); }
            catch { /* unreadable loose file; fall through to the embedded copy */ }
        }
        try
        {
            using var stream = Asm.GetManifestResourceStream(Prefix + subPath.Replace('/', '.'));
            return stream is null ? null : new StreamReader(stream).ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The file names embedded under <c>Resources/<paramref name="dir"/>/</c> (e.g. "en.json").</summary>
    public static IReadOnlyList<string> List(string dir)
    {
        var prefix = Prefix + dir + ".";
        return Asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .ToList();
    }
}
