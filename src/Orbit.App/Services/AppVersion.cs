using System.Reflection;

namespace Orbit_App.Services;

public static class AppVersion
{
    public static string GetInformationalVersion()
    {
        var assembly = typeof(AppVersion).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip SourceLink commit suffix if present.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>
    /// Semver used for GitHub release comparison — from <c>&lt;Version&gt;</c> (assembly),
    /// not marketing suffixes on InformationalVersion (e.g. <c>-phase17</c>).
    /// </summary>
    public static string GetUpdateCompareVersion()
    {
        var v = typeof(AppVersion).Assembly.GetName().Version;
        if (v is not null)
        {
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }

        return GetInformationalVersion();
    }

    public static string GetAssemblyVersion() =>
        typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public const string TargetFrameworkDisplay = "net9.0-windows10.0.26100.0";
}
