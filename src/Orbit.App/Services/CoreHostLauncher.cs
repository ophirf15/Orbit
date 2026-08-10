using System.Diagnostics;
using Orbit.Core.Settings;

namespace Orbit_App.Services;

public static class CoreHostLauncher
{
    public static bool TryStopExisting(out string detail)
    {
        try
        {
            var processes = Process.GetProcessesByName("Orbit.Core.Host");
            if (processes.Length == 0)
            {
                detail = "No Orbit.Core.Host process was running.";
                return true;
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4000);
                }
                catch (Exception)
                {
                    // best-effort — another instance may already be exiting
                }
                finally
                {
                    process.Dispose();
                }
            }

            detail = $"Stopped {processes.Length} Orbit.Core.Host process(es).";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public static bool TryStart(OrbitSettings settings, out string detail)
    {
        if (!settings.BackgroundHostEnabled)
        {
            detail = "Background host disabled in settings.";
            return false;
        }

        var exe = ResolveHostExecutable();
        if (exe is null)
        {
            detail = "Could not locate Orbit.Core.Host.exe near the app output.";
            return false;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };

            // Do not use a job object that kills children with the parent — Host should outlive App.
            var process = Process.Start(start);
            if (process is null)
            {
                detail = "Process.Start returned null.";
                return false;
            }

            detail = $"Started {exe} (pid {process.Id}).";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public static string? ResolveHostExecutable()
    {
        var candidates = new List<string>();

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "Orbit.Core.Host.exe"));
        candidates.Add(Path.Combine(baseDir, "Orbit.Core.Host", "Orbit.Core.Host.exe"));

        // Unpackaged WinUI: .../Orbit.App/bin/x64/Debug/net9.0-windows.../win-x64/
        // Host:              .../Orbit.Core.Host/bin/Debug/net9.0/Orbit.Core.Host.exe
        // Also x64|Debug layouts.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "Orbit.Core.Host.exe"));
            candidates.Add(Path.Combine(dir.FullName, "src", "Orbit.Core.Host", "bin", "Debug", "net9.0", "Orbit.Core.Host.exe"));
            candidates.Add(Path.Combine(dir.FullName, "src", "Orbit.Core.Host", "bin", "x64", "Debug", "net9.0", "Orbit.Core.Host.exe"));
            candidates.Add(Path.Combine(dir.FullName, "src", "Orbit.Core.Host", "bin", "Release", "net9.0", "Orbit.Core.Host.exe"));
            candidates.Add(Path.Combine(dir.FullName, "Orbit.Core.Host", "bin", "Debug", "net9.0", "Orbit.Core.Host.exe"));
            candidates.Add(Path.Combine(dir.FullName, "Orbit.Core.Host", "bin", "x64", "Debug", "net9.0", "Orbit.Core.Host.exe"));
        }

        // Prefer the newest *dev* build. Never quietly keep using Program Files install —
        // that binary often predates duty-operator and silently no-ops email→Hermes.
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Where(static path => !path.Contains(
                Path.Combine("Program Files", "Orbit"),
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault()
            ?? candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .OrderByDescending(static path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
    }
}
