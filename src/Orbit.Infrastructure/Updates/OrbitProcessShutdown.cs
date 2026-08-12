using System.Diagnostics;
using System.Text;

namespace Orbit.Infrastructure.Updates;

/// <summary>
/// Force-stops Orbit App / Core Host / MCP (and anything holding
/// %LocalAppData%\Orbit\orbit-mcp) so upgrades can replace clrjit.dll without
/// a manual handle unlocker (WinToys / Unlocker).
/// </summary>
public static class OrbitProcessShutdown
{
    private static readonly string[] ProcessNames =
    [
        "Orbit.App",
        "Orbit.Core.Host",
        "Orbit.Mcp",
    ];

    public static void KillOrbitRelated(TimeSpan? settle = null)
    {
        // Pass 1–2: taskkill is more reliable than Process.Kill for trees + stuck hosts.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var name in ProcessNames)
            {
                RunTaskKill(name + ".exe");
            }

            TryKillByPathFragment(@"\Orbit\orbit-mcp\");
            TryKillByPathFragment(@"\Orbit\Orbit.App.exe");
            TryKillByPathFragment(@"\Orbit\Orbit.Core.Host.exe");
            TryKillByPathFragment(@"\Program Files\Orbit\");
            TryKillByCommandLineFragment("Orbit.Mcp");
            TryKillByCommandLineFragment(@"Orbit\orbit-mcp");

            Thread.Sleep(pass == 0 ? 1500 : 500);
        }

        Thread.Sleep(settle ?? TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// If files under orbit-mcp are still locked after kill, rename the folder aside
    /// so the next copy can create a fresh directory (Hermes will pick up the new path).
    /// </summary>
    public static string? QuarantineMcpDirectoryIfNeeded()
    {
        var mcp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "orbit-mcp");
        if (!Directory.Exists(mcp))
        {
            return null;
        }

        try
        {
            // Probe whether a runtime DLL is writable.
            var probe = Directory.EnumerateFiles(mcp, "clrjit.dll", SearchOption.AllDirectories).FirstOrDefault()
                        ?? Directory.EnumerateFiles(mcp, "Orbit.Mcp.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (probe is null)
            {
                return null;
            }

            try
            {
                using var _ = new FileStream(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return null; // not locked
            }
            catch (IOException)
            {
                // locked — quarantine
            }

            KillOrbitRelated(TimeSpan.FromSeconds(1));

            var bak = mcp + ".old." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try
            {
                Directory.Move(mcp, bak);
                return bak;
            }
            catch
            {
                // Last resort: cmd move
                RunHidden("cmd.exe", "/c move /Y \"" + mcp + "\" \"" + bak + "\"");
                return Directory.Exists(bak) ? bak : null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void RunTaskKill(string imageName)
    {
        RunHidden(
            Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
            "/F /IM " + imageName + " /T");
    }

    private static void RunHidden(string fileName, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(12_000);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryKillByPathFragment(string fragment)
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    string? path;
                    try
                    {
                        path = p.MainModule?.FileName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(path)
                        || path.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(8_000);
                    }
                    catch
                    {
                        RunTaskKill(p.ProcessName + ".exe");
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryKillByCommandLineFragment(string fragment)
    {
        // Win32_Process.CommandLine catches Hermes-spawned MCP when MainModule is inaccessible.
        var script = new StringBuilder()
            .Append("-NoProfile -NonInteractive -Command \"")
            .Append("Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ")
            .Append("Where-Object { $_.CommandLine -and ($_.CommandLine -like '*")
            .Append(fragment.Replace("'", "''"))
            .Append("*') -and $_.ProcessId -ne ")
            .Append(Environment.ProcessId)
            .Append(" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
            .Append('"')
            .ToString();

        RunHidden(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            script);
    }
}
