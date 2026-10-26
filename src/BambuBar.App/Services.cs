using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;

namespace BambuBar.App;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// "Open at login" — the HKCU Run key. The macOS original starts menu-bar-only at login; the Windows
/// equivalent passes <c>--tray</c> so the app comes up quietly in the notification area.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Bambu Bar";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Contains("BambuBar", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return;
            key.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>
/// Suspends the MQTT sessions when the machine sleeps or the session locks, and asks for a fresh report
/// on wake — the macOS app does the same with NSWorkspace notifications.
/// </summary>
public sealed class PowerManager : IDisposable
{
    private readonly System.Windows.Window _host;
    private readonly HwndSource? _source;
    private readonly Action _suspend;
    private readonly Action _resume;

    public PowerManager(Action suspend, Action resume)
    {
        _suspend = suspend;
        _resume = resume;

        // A hidden window whose HWND we can hook for Win32 power/session broadcasts.
        _host = new System.Windows.Window
        {
            Width = 1,
            Height = 1,
            Visibility = Visibility.Hidden,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            Topmost = false,
        };
        _host.Show();
        var handle = new WindowInteropHelper(_host).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmPowerBroadcast = 0x0218;
        const int WmWtsSessionChange = 0x02B1;
        const int PbtApmSuspend = 0x29;
        const int PbtApmResumed = 0x0012;
        const int WtsSessionLock = 7;
        const int WtsSessionUnlock = 8;

        switch (msg)
        {
            case WmPowerBroadcast:
                if (wParam.ToInt64() == PbtApmSuspend) _suspend();
                else if (wParam.ToInt64() == PbtApmResumed) _resume();
                break;
            case WmWtsSessionChange:
                if (wParam.ToInt64() == WtsSessionLock) _suspend();
                else if (wParam.ToInt64() == WtsSessionUnlock) _resume();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
        _host.Close();
    }
}
