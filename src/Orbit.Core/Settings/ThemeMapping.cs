namespace Orbit.Core.Settings;

/// <summary>
/// Pure theme preference helpers (no WinUI dependency) for mapping and tests.
/// </summary>
public static class ThemeMapping
{
    public static ThemePreference Normalize(ThemePreference preference) =>
        Enum.IsDefined(preference) ? preference : ThemePreference.System;

    public static bool FollowsSystem(ThemePreference preference) =>
        Normalize(preference) == ThemePreference.System;

    public static bool IsDark(ThemePreference preference) =>
        Normalize(preference) == ThemePreference.Dark;

    public static bool IsLight(ThemePreference preference) =>
        Normalize(preference) == ThemePreference.Light;
}
