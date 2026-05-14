using System.Reflection;

namespace Jesnote.Core;

public sealed class GlobalSettings
{
    // Use SemVer 2.0 format without the leading 'v'
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public const string AppInfo = "Jesnote.AppInfo.json";
    public const string AppIcon = AppIconDark;
    public const string AppIconDark = "Jesnote.Icons.Logo.ico";
    public const string AppIconLight = "Jesnote.Icons.LogoLight.ico";
}
