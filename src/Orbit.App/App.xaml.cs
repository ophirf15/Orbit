using Microsoft.UI.Xaml;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;
using Orbit_App.Services;
using Orbit_App.Shell;

namespace Orbit_App;

public partial class App : Application
{
    private Window? _window;

    public static OrbitSettings Settings { get; set; } = OrbitSettingsDefaults.CreateDefaults();

    public static JsonOrbitSettingsStore SettingsStore { get; } = new();

    public static Window? MainWindow { get; private set; }

    public static CoreHostConnectionService? HostConnection { get; private set; }

    public App()
    {
        InitializeComponent();

        try
        {
            Settings = SettingsStore.Load();
        }
        catch (Exception)
        {
            Settings = OrbitSettingsDefaults.CreateDefaults();
        }

        ThemeService.ApplyToApplication(Settings.ThemePreference);
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        BackdropService.Apply(_window);
        ThemeService.ApplyToWindow(_window, Settings.ThemePreference);
        HostConnection = new CoreHostConnectionService(Settings, SettingsStore);
        _window.Activate();

        _ = ConnectCoreHostAsync();
    }

    private static async Task ConnectCoreHostAsync()
    {
        if (HostConnection is null)
        {
            return;
        }

        try
        {
            await HostConnection.EnsureConnectedAsync();
        }
        catch (Exception)
        {
            // Degraded status remains on the service; shell pages read LastStatus.
        }
    }
}
