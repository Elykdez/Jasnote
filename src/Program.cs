using Avalonia;

namespace Jesnote;

internal static class Program
{
    /// <summary>
    /// Compared to WinForms, the exact startup call changed, but the Windows requirement is basically the same:
    /// Many desktop UI features expect the main thread to be STA, especially clipboard, dialogs, drag/drop, and COM-backed shell integration.
    /// On macOS it is mostly harmless, but since this is one cross-platform entry point, keeping it is the right choice.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
