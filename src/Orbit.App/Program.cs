using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Orbit_App.Services;

namespace Orbit_App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Silent COM registration for Inno (runasoriginaluser) — no UI.
        if (OrbitPushActivation.ArgsRequestInstallOutlookAddIn(args))
        {
            var result = OutlookLauncherSetup.InstallOrUpdate();
            Environment.ExitCode = result.Ok ? 0 : 1;
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        var redirected = OrbitPushActivation.DecideRedirectAsync().GetAwaiter().GetResult();
        if (redirected)
        {
            return;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
