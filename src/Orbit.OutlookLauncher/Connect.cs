using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Extensibility;
using Office;

namespace Orbit.OutlookLauncher;

/// <summary>
/// Launch-only Classic Outlook ribbon: signal Orbit App + start Orbit if needed.
/// Does not talk to Core Host, does not SaveAs .msg, does not use Vite.
/// </summary>
[ComVisible(true)]
[Guid("E3C8A1F0-7B2D-4C9E-A6D1-5F8E2B4C9A01")]
[ProgId("Orbit.OutlookLauncher.Connect")]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class Connect : IDTExtensibility2, IRibbonExtensibility, IOrbitRibbonCallbacks
{
    public void OnConnection(object Application, ext_ConnectMode ConnectMode, object AddInInst, ref Array custom)
    {
        try
        {
            Log("OnConnection " + ConnectMode);
        }
        catch
        {
            // Never throw into Outlook — surfaces as "A runtime error occurred while loading the COM add-in".
        }
    }

    public void OnDisconnection(ext_DisconnectMode RemoveMode, ref Array custom)
    {
        try
        {
            Log("OnDisconnection " + RemoveMode);
        }
        catch
        {
            // ignore
        }
    }

    public void OnAddInsUpdate(ref Array custom)
    {
    }

    public void OnStartupComplete(ref Array custom)
    {
        try
        {
            Log("OnStartupComplete");
        }
        catch
        {
            // ignore
        }
    }

    public void OnBeginShutdown(ref Array custom)
    {
    }

    public string GetCustomUI(string RibbonID)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Orbit.OutlookLauncher.Ribbon.xml");
            if (stream is null)
            {
                Log("GetCustomUI: Ribbon.xml missing");
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            var xml = reader.ReadToEnd();
            Log("GetCustomUI ok for " + RibbonID);
            return xml;
        }
        catch (Exception ex)
        {
            Log("GetCustomUI failed: " + ex.Message);
            return string.Empty;
        }
    }

    public void Ribbon_Load(object ribbonUi)
    {
        Log("Ribbon_Load");
        _ = ribbonUi;
    }

    /// <summary>Ribbon onAction — must take <see cref="IRibbonControl"/> or Outlook won't bind.</summary>
    public void OnSendToOrbit(IRibbonControl control)
    {
        _ = control;
        try
        {
            Log("OnSendToOrbit click");
            var signaled = WritePushSignal();

            // If Orbit is already open, the signal/event is enough. Launching again
            // causes a second/third handoff via peer-process + activation.
            var launched = false;
            if (!OrbitAppRunning())
            {
                launched = TryLaunchExe() || TryLaunchProtocol();
            }
            else
            {
                Log("Orbit.App already running — signal only");
            }

            Log($"signaled={signaled} launched={launched}");

            if (!launched && !signaled)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Could not reach Orbit.\n\n"
                    + "Start Orbit App first, then click Send to Orbit again.",
                    "Orbit",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log("OnSendToOrbit error: " + ex);
            System.Windows.Forms.MessageBox.Show(
                "Orbit launcher error:\n\n" + ex.Message,
                "Orbit",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private static bool OrbitAppRunning()
    {
        try
        {
            return Process.GetProcessesByName("Orbit.App").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool WritePushSignal()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orbit",
                "commands");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "push-outlook.request");
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O") + "\nsource=outlook-launcher\n");

            try
            {
                using var ev = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    @"Local\Orbit.PushOutlook");
                ev.Set();
            }
            catch
            {
                // file signal still written
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("WritePushSignal failed: " + ex.Message);
            return false;
        }
    }

    private static bool TryLaunchProtocol()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "orbit://push-outlook",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log("TryLaunchProtocol failed: " + ex.Message);
            return false;
        }
    }

    private static bool TryLaunchExe()
    {
        try
        {
            var candidates = new List<string>
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Orbit",
                    "Orbit.App.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Orbit",
                    "app",
                    "Orbit.App.exe"),
            };

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\orbit\shell\open\command");
                var cmd = key?.GetValue(string.Empty) as string;
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    var exe = ExtractQuotedPath(cmd);
                    if (!string.IsNullOrWhiteSpace(exe))
                    {
                        candidates.Insert(0, exe!);
                    }
                }
            }
            catch
            {
                // ignore
            }

            foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(exe))
                {
                    continue;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--push-outlook",
                    UseShellExecute = true,
                });
                Log("Started " + exe);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("TryLaunchExe failed: " + ex.Message);
        }

        return false;
    }

    private static string? ExtractQuotedPath(string command)
    {
        var t = command.Trim();
        if (t.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = t.IndexOf('"', 1);
            if (end > 1)
            {
                return t.Substring(1, end - 1);
            }
        }

        var space = t.IndexOf(" ", StringComparison.Ordinal);
        return space > 0 ? t.Substring(0, space) : t;
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orbit",
                "logs");
            Directory.CreateDirectory(dir);
            var line = DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine;
            File.AppendAllText(Path.Combine(dir, "outlook-launcher.log"), line);
        }
        catch
        {
            // ignore
        }
    }
}
