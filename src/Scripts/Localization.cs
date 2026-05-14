using System.Globalization;
using System.Reflection;
using System.Resources;
using Jesnote.Core;

[assembly: NeutralResourcesLanguage("en")]

namespace Jesnote;

public enum LanguagePreference
{
    Auto,
    English,
    ChineseSimplified,
}

public interface ILocalizable
{
    void ApplyLocalization();
}

public static class Localization
{
    static readonly ResourceManager s_resources =
        new("Jesnote.Resources.Strings", Assembly.GetExecutingAssembly());

    public static LanguagePreference CurrentPreference { get; private set; } =
        LanguagePreference.Auto;

    public static CultureInfo CurrentCulture { get; private set; } =
        ResolveCulture(LanguagePreference.Auto);

    public static void Apply(LanguagePreference preference)
    {
        CurrentPreference = preference;
        CurrentCulture = ResolveCulture(preference);
        CultureInfo.CurrentCulture = CurrentCulture;
        CultureInfo.CurrentUICulture = CurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = CurrentCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
    }

    public static string T(string key) => s_resources.GetString(key, CurrentCulture) ?? key;

    public static string F(string key, params object?[] args) =>
        string.Format(CurrentCulture, T(key), args);

    public static string LanguageName(LanguagePreference preference) => T("Language." + preference);

    public static string ThemeName(ColorThemePreference preference) => T("Theme." + preference);

    public static string SearchTypeName(SearchType type) => T("Search.Type." + type);

    public static string JsonNodeTypeName(JsonNodeType type) => T("JsonNodeType." + type);

    static CultureInfo ResolveCulture(LanguagePreference preference) =>
        preference switch
        {
            LanguagePreference.English => CultureInfo.GetCultureInfo("en-US"),
            LanguagePreference.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            _ => ResolveAutoCulture(),
        };

    static CultureInfo ResolveAutoCulture()
    {
        var installed = CultureInfo.InstalledUICulture;
        return installed.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en-US");
    }
}
