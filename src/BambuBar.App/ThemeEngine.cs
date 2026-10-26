using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using BambuBar.Core.Store;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace BambuBar.App;

/// <summary>
/// Light / dark / auto theming for both windows. "Auto" follows the Windows system appearance —
/// the <c>AppsUseLightTheme</c> personalization key — re-read while in the background, so flipping
/// the system theme updates the app live.
///
/// Pages build their UI with these tokens (they are rebuilt when the store changes), and the window
/// chrome (backgrounds, sidebar) is re-applied through the <see cref="Changed"/> event, which always
/// fires on the WPF dispatcher thread.
/// </summary>
public sealed class ThemeEngine
{
    public static ThemeEngine Current { get; } = new();

    private ThemeEngine()
    {
        _palette = LightPalette;
    }

    /// <summary>Raised (on the UI thread) when the effective light/dark state changes.</summary>
    public event Action? Changed;

    public bool IsDark { get; private set; }
    public double ShadowOpacity => _palette.ShadowOpacity;

    private Palette _palette;
    private SettingsStore.ThemeMode _mode = SettingsStore.ThemeMode.Auto;
    private readonly object _watchGate = new();
    private Thread? _watchThread;
    private volatile bool _watchRunning;
    private bool _lastSystemLight = true;

    /// <summary>
    /// A neutral overlay of the given alpha: black in light mode, white in dark mode.
    /// Replaces the app's former hard-coded <c>Color.FromArgb(alpha, 0, 0, 0)</c> neutrals.
    /// </summary>
    public Color Neutral(byte alpha) => IsDark
        ? Color.FromArgb(alpha, 255, 255, 255)
        : Color.FromArgb(alpha, 0, 0, 0);

    public Brush PrimaryBrush => _palette.Primary;
    public Brush SecondaryBrush => _palette.Secondary;
    public Brush TertiaryBrush => _palette.Tertiary;
    public Brush HairlineBrush => _palette.Hairline;
    public Brush NavTextBrush => _palette.NavText;
    public Brush WindowBackground => _palette.WindowBackground;
    public Brush SidebarBackground => _palette.SidebarBackground;
    public Brush PanelBackground => _palette.PanelBackground;
    public Brush InputBackground => _palette.InputBackground;

    /// <summary>Set the configured mode; resolves Auto against the system appearance and (re)starts the watcher.</summary>
    public void SetMode(SettingsStore.ThemeMode mode)
    {
        _mode = mode;
        bool systemLight = SystemAppearanceIsLight();
        _lastSystemLight = systemLight;
        bool dark = mode switch
        {
            SettingsStore.ThemeMode.Light => false,
            SettingsStore.ThemeMode.Dark => true,
            _ => !systemLight,
        };
        RestartWatcher(mode == SettingsStore.ThemeMode.Auto);
        if (dark != IsDark)
        {
            IsDark = dark;
            _palette = dark ? DarkPalette : LightPalette;
            RaiseChanged();
        }
    }

    public void Shutdown()
    {
        RestartWatcher(false);
    }

    // MARK: - System appearance

    /// <summary>Reads <c>HKCU\…\Themes\Personalize\AppsUseLightTheme</c>; true (light) when unavailable.</summary>
    private static bool SystemAppearanceIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i != 0;
            if (value is long l) return l != 0;
        }
        catch
        {
            // Fall through to the default.
        }
        return true;
    }

    // MARK: - Live watcher (Auto mode)

    private void RestartWatcher(bool shouldRun)
    {
        lock (_watchGate)
        {
            _watchRunning = false;
            _watchThread = null;
            if (shouldRun)
            {
                _watchRunning = true;
                _watchThread = new Thread(WatchLoop) { IsBackground = true, Name = "BambuBar.Theme" };
                _watchThread.Start();
            }
        }
    }

    /// <summary>Re-reads the system appearance every ~3 s; cheap and reliable enough for a desktop app.</summary>
    private void WatchLoop()
    {
        while (_watchRunning)
        {
            for (int i = 0; i < 3 && _watchRunning; i++)
                Thread.Sleep(1000);
            if (!_watchRunning) return;

            bool light = SystemAppearanceIsLight();
            if (light == _lastSystemLight) continue;
            _lastSystemLight = light;

            if (IsDark == light)
            {
                IsDark = !light;
                _palette = light ? LightPalette : DarkPalette;
                RaiseChanged();
            }
        }
    }

    private void RaiseChanged()
    {
        var changed = Changed;
        if (changed is null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) changed();
        else dispatcher.BeginInvoke(changed);
    }

    // MARK: - Palettes

    private sealed class Palette
    {
        public double ShadowOpacity { get; init; }
        public Brush Primary { get; init; } = null!;
        public Brush Secondary { get; init; } = null!;
        public Brush Tertiary { get; init; } = null!;
        public Brush Hairline { get; init; } = null!;
        public Brush NavText { get; init; } = null!;
        public Brush WindowBackground { get; init; } = null!;
        public Brush SidebarBackground { get; init; } = null!;
        public Brush PanelBackground { get; init; } = null!;
        public Brush InputBackground { get; init; } = null!;
    }

    /// <summary>The light palette; values match the app's original hard-coded light theme.</summary>
    private static Palette BuildLight() => new()
    {
        ShadowOpacity = 0.06,
        Primary = Frozen(0x1A, 0x1A, 0x1A),
        Secondary = Frozen(0x6E, 0x74, 0x7B),
        Tertiary = Frozen(0x9A, 0xA0, 0xA6),
        Hairline = FrozenAlpha(0x33, 0, 0, 0),
        NavText = Frozen(0x33, 0x33, 0x33),
        WindowBackground = Frozen(0xFA, 0xFB, 0xFA),
        SidebarBackground = Frozen(0xF1, 0xF3, 0xF1),
        PanelBackground = Frozen(0xF5, 0xF6, 0xF4),
        InputBackground = Frozen(0xFF, 0xFF, 0xFF),
    };

    /// <summary>The dark palette: near-neutral charcoal surfaces, slightly cool.</summary>
    private static Palette BuildDark() => new()
    {
        ShadowOpacity = 0.45,
        Primary = Frozen(0xF2, 0xF3, 0xF5),
        Secondary = Frozen(0xA9, 0xAF, 0xB6),
        Tertiary = Frozen(0x7E, 0x84, 0x8C),
        Hairline = FrozenAlpha(0x3D, 255, 255, 255),
        NavText = Frozen(0xE4, 0xE7, 0xEB),
        WindowBackground = Frozen(0x20, 0x21, 0x24),
        SidebarBackground = Frozen(0x19, 0x1A, 0x1D),
        PanelBackground = Frozen(0x1E, 0x20, 0x23),
        InputBackground = Frozen(0x2A, 0x2C, 0x31),
    };

    private static readonly Palette LightPalette = BuildLight();
    private static readonly Palette DarkPalette = BuildDark();

    private static Brush Frozen(byte r, byte g, byte b) => FrozenAlpha(0xFF, r, g, b);

    private static Brush FrozenAlpha(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
