using Microsoft.UI.Xaml;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;
using Orbit_App.Services;

namespace Orbit_App;

public partial class App : Application
{
    private Window? _window;
    private IDisposable? _pushWatcher;
    private DispatcherTimer? _pushPollTimer;
    private int _signalAcceptGate;

    public static OrbitSettings Settings { get; set; } = OrbitSettingsDefaults.CreateDefaults();

    public static JsonOrbitSettingsStore SettingsStore { get; } = new();

    public static Window? MainWindow { get; private set; }

    public static CoreHostConnectionService? HostConnection { get; private set; }

    public static OutlookPushQueue OutlookPush { get; } = new();

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
        OrbitPushActivation.PushOutlookRequested += () => OutlookPush.EnqueueHandoff("activation");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        BackdropService.Apply(_window);
        ThemeService.ApplyToWindow(_window, Settings.ThemePreference);
        HostConnection = new CoreHostConnectionService(Settings, SettingsStore);
        OrbitPushActivation.EnsureProtocolRegistered();
        // Installer stages DLLs only — register COM on first launch / when Outlook disabled the add-in.
        _ = OutlookLauncherSetup.EnsureRegisteredOnLaunch();
        _pushWatcher = OrbitPushSignal.StartWatcher(() => TryAcceptPushSignal("signal-watcher"));

        // UI-thread poll — survives cases where background waiters stall under COM/load.
        _pushPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pushPollTimer.Tick += (_, _) =>
        {
            if (TryAcceptPushSignal("ui-poll"))
            {
                OrbitPushSignal.TryDeleteRequest();
            }
        };
        _pushPollTimer.Start();

        _window.Activate();

        _ = ConnectCoreHostAsync();

        if (OrbitPushActivation.ConsumePendingPushOutlook()
            || OrbitPushActivation.ArgsRequestPushOutlook(Environment.GetCommandLineArgs().Skip(1))
            || OrbitPushSignal.RequestPending())
        {
            if (TryAcceptPushSignal("startup"))
            {
                OrbitPushSignal.TryDeleteRequest();
            }
        }
    }

    /// <summary>
    /// Returns true when handoff work was accepted by the UI dispatcher
    /// (caller may delete the signal file).
    /// </summary>
    private bool TryAcceptPushSignal(string source)
    {
        var requireFile = source is "signal-watcher" or "ui-poll";
        if (requireFile && !OrbitPushSignal.RequestPending())
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _signalAcceptGate, 1, 0) != 0)
        {
            // Another acceptor is mid-flight; keep the file for the next tick.
            return false;
        }

        try
        {
            if (MainWindow is null)
            {
                return false;
            }

            return OutlookPush.EnqueueHandoff(source);
        }
        finally
        {
            Interlocked.Exchange(ref _signalAcceptGate, 0);
        }
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
