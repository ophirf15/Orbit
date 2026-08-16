using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using WinRT.Interop;

namespace Orbit_App.Services;

/// <summary>
/// Thin handoff from Outlook: <c>orbit://push-outlook</c> or <c>--push-outlook</c>.
/// No Host/API keys in Outlook — only activates this App.
/// </summary>
public static class OrbitPushActivation
{
    public const string ProtocolScheme = "orbit";
    public const string PushOutlookHost = "push-outlook";
    public const string PushOutlookArg = "--push-outlook";
    public const string InstallOutlookAddInArg = "--install-outlook-addin";
    public const string AppInstanceKey = "Orbit.App";

    public static event Action? PushOutlookRequested;

    private static bool _pendingPushOutlook;

    public static bool ConsumePendingPushOutlook()
    {
        if (!_pendingPushOutlook)
        {
            return false;
        }

        _pendingPushOutlook = false;
        return true;
    }

    public static bool IsPushOutlookActivation(string? uriOrArg)
    {
        if (string.IsNullOrWhiteSpace(uriOrArg))
        {
            return false;
        }

        var raw = uriOrArg.Trim().Trim('"');
        if (string.Equals(raw, PushOutlookArg, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, PushOutlookHost, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ArgsRequestInstallOutlookAddIn(IEnumerable<string> args) =>
        args.Any(a => string.Equals(
            a.Trim().Trim('"'),
            InstallOutlookAddInArg,
            StringComparison.OrdinalIgnoreCase));

    public static bool ArgsRequestPushOutlook(IEnumerable<string> args) =>
        args.Any(IsPushOutlookActivation);

    public static void MarkPushOutlookRequested()
    {
        _pendingPushOutlook = true;
        // Do not write the signal file here — launcher/peer already do that.
        // Rewriting after the App consumes the first signal causes duplicate queues.
        try
        {
            PushOutlookRequested?.Invoke();
        }
        catch
        {
            // subscribers must not break activation
        }
    }

    public static void NoteActivationKind(AppActivationArguments? activated)
    {
        if (activated is null)
        {
            return;
        }

        if (activated.Kind == ExtendedActivationKind.Protocol
            && activated.Data is IProtocolActivatedEventArgs protocol
            && IsPushOutlookActivation(protocol.Uri?.AbsoluteUri))
        {
            MarkPushOutlookRequested();
            return;
        }

        if (activated.Kind == ExtendedActivationKind.Launch
            && activated.Data is ILaunchActivatedEventArgs launch
            && ArgsRequestPushOutlook(SplitArgs(launch.Arguments)))
        {
            MarkPushOutlookRequested();
        }
    }

    public static async Task<bool> DecideRedirectAsync()
    {
        var current = AppInstance.GetCurrent();
        var args = current.GetActivatedEventArgs();
        var pushRequested = IsPushActivation(args)
            || ArgsRequestPushOutlook(Environment.GetCommandLineArgs().Skip(1));

        // AppInstance redirect is unreliable for unpackaged / mixed launch paths.
        // If another Orbit.App is already running, never open a second window —
        // hand off via the signal file the live App polls.
        if (pushRequested && HasPeerOrbitAppProcess())
        {
            OrbitPushSignal.WritePushOutlookRequest("peer-process");
            try
            {
                var peer = AppInstance.FindOrRegisterForKey(AppInstanceKey);
                if (!peer.IsCurrent)
                {
                    await peer.RedirectActivationToAsync(args);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("peer redirect failed: " + ex.Message);
            }

            return true;
        }

        NoteActivationKind(args);

        if (ArgsRequestPushOutlook(Environment.GetCommandLineArgs().Skip(1)))
        {
            MarkPushOutlookRequested();
        }

        var main = AppInstance.FindOrRegisterForKey(AppInstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += OnRedirectedActivation;
            return false;
        }

        OrbitPushSignal.WritePushOutlookRequest("secondary-instance");
        try
        {
            await main.RedirectActivationToAsync(args);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("RedirectActivationToAsync failed: " + ex.Message);
        }

        return true;
    }

    private static bool IsPushActivation(AppActivationArguments? activated)
    {
        if (activated is null)
        {
            return false;
        }

        if (activated.Kind == ExtendedActivationKind.Protocol
            && activated.Data is IProtocolActivatedEventArgs protocol
            && IsPushOutlookActivation(protocol.Uri?.AbsoluteUri))
        {
            return true;
        }

        if (activated.Kind == ExtendedActivationKind.Launch
            && activated.Data is ILaunchActivatedEventArgs launch
            && ArgsRequestPushOutlook(SplitArgs(launch.Arguments)))
        {
            return true;
        }

        return false;
    }

    private static bool HasPeerOrbitAppProcess()
    {
        try
        {
            var self = Environment.ProcessId;
            return Process.GetProcessesByName("Orbit.App").Any(p =>
            {
                try
                {
                    return p.Id != self;
                }
                finally
                {
                    p.Dispose();
                }
            });
        }
        catch
        {
            return false;
        }
    }

    private static void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        NoteActivationKind(e);
        if (ArgsRequestPushOutlook(Environment.GetCommandLineArgs().Skip(1)))
        {
            MarkPushOutlookRequested();
        }

        try
        {
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                BringMainWindowToFront();
                PushOutlookRequested?.Invoke();
            });
        }
        catch
        {
            // ignore
        }
    }

    public static void BringMainWindowToFront()
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return;
        }

        try
        {
            window.Activate();
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // best-effort
        }
    }

    public static void EnsureProtocolRegistered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                return;
            }

            // Prefer the built WinUI exe, not `dotnet.exe` when launched via `dotnet run`.
            if (string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(
                    AppContext.BaseDirectory,
                    "Orbit.App.exe");
                if (File.Exists(candidate))
                {
                    exe = candidate;
                }
            }

            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            using var classes = hkcu.CreateSubKey(@"Software\Classes\" + ProtocolScheme);
            classes?.SetValue(string.Empty, "URL:Orbit Protocol");
            classes?.SetValue("URL Protocol", string.Empty);
            using var command = classes?.CreateSubKey(@"shell\open\command");
            command?.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Orbit protocol registration failed: " + ex.Message);
        }
    }

    public static void LaunchPushOutlookProtocol()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"{ProtocolScheme}://{PushOutlookHost}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Launch orbit://push-outlook failed: " + ex.Message);
        }
    }

    private static IEnumerable<string> SplitArgs(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        // Launch args may be a single protocol URI or "--push-outlook".
        yield return arguments.Trim();
        foreach (var part in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
