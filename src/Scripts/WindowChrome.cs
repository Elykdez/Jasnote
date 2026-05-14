using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Jesnote;

/// <summary>
/// On Windows the title bar is drawn by DWM (non-client area), so Avalonia's
/// ThemeVariant alone does not affect it. Tell DWM to render the caption in
/// dark mode so the chrome matches the rest of the UI.
/// </summary>
internal static class WindowChrome
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 20H1+, Win11
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19; // older Win10 builds

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Apply the title-bar color once the native handle exists. Theme changes
    /// are pushed explicitly by the caller through Apply.
    /// </summary>
    public static void AttachTo(Window window, Func<bool> isDark)
    {
        if (!OperatingSystem.IsWindows())
            return;

        window.Opened += (_, _) =>
        {
            Apply(window, isDark());
            Dispatcher.UIThread.Post(() => Apply(window, isDark()), DispatcherPriority.Loaded);
        };
    }

    /// <summary>
    /// Push the current dark/light state to the native Windows title bar.
    /// </summary>
    public static void Apply(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        int useDark = dark ? 1 : 0;
        if (
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int))
            != 0
        )
        {
            _ = DwmSetWindowAttribute(
                hwnd,
                DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1,
                ref useDark,
                sizeof(int)
            );
        }
    }
}
