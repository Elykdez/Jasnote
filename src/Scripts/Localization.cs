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
    French,
    Japanese,
    Korean,
    Portuguese,
    Russian,
    Spanish,
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

    public static string LanguageName(LanguagePreference preference) =>
        preference switch
        {
            LanguagePreference.English => "English",
            LanguagePreference.ChineseSimplified => "简体中文",
            LanguagePreference.French => "Français",
            LanguagePreference.Japanese => "日本語",
            LanguagePreference.Korean => "한국어",
            LanguagePreference.Spanish => "Español",
            LanguagePreference.Portuguese => "Português",
            LanguagePreference.Russian => "Русский",
            _ => "System default",
        };

    public static string ThemeName(ColorThemePreference preference) => T("Theme." + preference);

    public static string SearchTypeName(SearchType type) => T("Search.Type." + type);

    public static string JsonNodeTypeName(JsonNodeType type) => T("JsonNodeType." + type);

    public static string StringStorageName(StringStorageMode mode) => T("StringStorage." + mode);

    static CultureInfo ResolveCulture(LanguagePreference preference) =>
        preference switch
        {
            LanguagePreference.English => CultureInfo.GetCultureInfo("en-US"),
            LanguagePreference.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            LanguagePreference.Spanish => CultureInfo.GetCultureInfo("es"),
            LanguagePreference.Portuguese => CultureInfo.GetCultureInfo("pt"),
            LanguagePreference.French => CultureInfo.GetCultureInfo("fr"),
            LanguagePreference.Russian => CultureInfo.GetCultureInfo("ru"),
            LanguagePreference.Japanese => CultureInfo.GetCultureInfo("ja"),
            LanguagePreference.Korean => CultureInfo.GetCultureInfo("ko"),
            _ => ResolveAutoCulture(),
        };

    static CultureInfo ResolveAutoCulture()
    {
        var installed = CultureInfo.InstalledUICulture;
        return installed.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "es" => CultureInfo.GetCultureInfo("es"),
            "fr" => CultureInfo.GetCultureInfo("fr"),
            "ja" => CultureInfo.GetCultureInfo("ja"),
            "ko" => CultureInfo.GetCultureInfo("ko"),
            "ru" => CultureInfo.GetCultureInfo("ru"),
            "pt" => CultureInfo.GetCultureInfo("pt"),
            "zh" => CultureInfo.GetCultureInfo("zh-CN"),
            _ => CultureInfo.GetCultureInfo("en-US"),
        };
    }
}
