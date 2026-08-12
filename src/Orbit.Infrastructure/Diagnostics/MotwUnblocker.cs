using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orbit.Infrastructure.Diagnostics;

/// <summary>
/// Clears Mark-of-the-Web (Zone.Identifier) so Windows / Outlook / .NET will load
/// DLLs and EXEs downloaded from GitHub without a manual "Unblock".
/// </summary>
public static class MotwUnblocker
{
    public static bool UnblockFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // Delete Zone.Identifier alternate data stream (MOTW).
            var ads = path + ":Zone.Identifier";
            if (DeleteFile(ads))
            {
                return true;
            }

            // Fallback: PowerShell Unblock-File (covers edge cases DeleteFile misses).
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -NonInteractive -Command \"Unblock-File -LiteralPath '"
                    + path.Replace("'", "''")
                    + "'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.WaitForExit(15_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int UnblockDirectory(string directory, string searchPattern = "*")
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var n = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories))
            {
                if (UnblockFile(file))
                {
                    n++;
                }
            }
        }
        catch
        {
            // best-effort
        }

        return n;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string lpFileName);
}
