using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Jesnote;

/// <summary>
/// Avalonia application root
/// </summary>
internal sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(
            new Style(selector => selector.OfType<TextBlock>().Class("link"))
            {
                Setters =
                {
                    new Setter(
                        TextBlock.ForegroundProperty,
                        new SolidColorBrush(Color.FromRgb(0, 102, 204))
                    ),
                    new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline),
                    new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)),
                },
            }
        );
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            Localization.Apply(settings.Language);
            ApplyTheme(settings.ColorTheme);

            var window = new MainWindow(settings);
            desktop.MainWindow = window;

            if (desktop.Args is { Length: > 0 } args && !args[0].StartsWith('-'))
                window.LoadInitial(args[0]);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(ColorThemePreference preference)
    {
        RequestedThemeVariant = preference switch
        {
            ColorThemePreference.Light => ThemeVariant.Light,
            ColorThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
