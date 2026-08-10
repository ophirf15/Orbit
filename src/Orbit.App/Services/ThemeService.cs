using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Orbit.Core.Settings;

namespace Orbit_App.Services;

public static class ThemeService
{
    public static void ApplyToApplication(ThemePreference preference)
    {
        preference = ThemeMapping.Normalize(preference);
        if (ThemeMapping.FollowsSystem(preference))
        {
            return;
        }

        try
        {
            Application.Current.RequestedTheme = ThemeMapping.IsDark(preference)
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
        }
        catch (Exception)
        {
            // Some hosts only allow RequestedTheme during construction.
        }
    }

    public static void ApplyToWindow(Window window, ThemePreference preference)
    {
        ArgumentNullException.ThrowIfNull(window);
        preference = ThemeMapping.Normalize(preference);

        var elementTheme = ThemeMapping.FollowsSystem(preference)
            ? ElementTheme.Default
            : ThemeMapping.IsDark(preference) ? ElementTheme.Dark : ElementTheme.Light;

        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = elementTheme;
        }
    }

    public static ThemePreference ToggleLightDark(ThemePreference current)
    {
        current = ThemeMapping.Normalize(current);
        if (ThemeMapping.FollowsSystem(current))
        {
            return ThemePreference.Dark;
        }

        return ThemeMapping.IsDark(current) ? ThemePreference.Light : ThemePreference.Dark;
    }
}
