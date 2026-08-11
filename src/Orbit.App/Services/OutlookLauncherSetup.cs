using System.Diagnostics;
using Microsoft.Win32;

namespace Orbit_App.Services;

/// <summary>
/// Install / update / remove the thin Classic Outlook launcher (COM ribbon → orbit://).
/// Payload ships under <c>outlook-launcher\</c> next to Orbit.App.exe (installer) or
/// the net48 build output (dev). Runtime copy lives in LocalAppData.
/// </summary>
public static class OutlookLauncherSetup
{
    public const string ProgId = "Orbit.OutlookLauncher.Connect";
    public const string Clsid = "{E3C8A1F0-7B2D-4C9E-A6D1-5F8E2B4C9A01}";
    public const string AssemblyVersion = "0.1.0.0";
    public const string DllFileName = "Orbit.OutlookLauncher.dll";

    public static string InstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "OutlookLauncher");

    public static string InstalledDllPath => Path.Combine(InstallDirectory, DllFileName);

    public sealed record Status(
        bool IsRegistered,
        bool PayloadAvailable,
        bool InstalledFilesPresent,
        bool OutlookRunning,
        string? PayloadDirectory,
        string Summary);

    public sealed record Result(bool Ok, string Message);

    public static Status GetStatus()
    {
        var payload = FindPayloadDirectory();
        var registered = IsRegistered();
        var files = File.Exists(InstalledDllPath);
        var outlook = IsOutlookRunning();
        var summary = registered
            ? files
                ? outlook
                    ? "Installed — Classic Outlook is open (close it before updating the add-in DLL)."
                    : "Installed — Mail tab shows Send to Orbit after Outlook restart if needed."
                : "Registered, but DLL missing — click Install / Update."
            : payload is null
                ? "Not installed — Outlook launcher payload missing from this Orbit build."
                : "Not installed — click Install to add Send to Orbit in Classic Outlook.";

        return new Status(registered, payload is not null, files, outlook, payload, summary);
    }

    public static Result InstallOrUpdate()
    {
        try
        {
            var payload = FindPayloadDirectory();
            if (payload is null || !File.Exists(Path.Combine(payload, DllFileName)))
            {
                return new Result(
                    false,
                    "Outlook launcher files are not packaged with this Orbit build. Reinstall Orbit or use a Release installer.");
            }

            Directory.CreateDirectory(InstallDirectory);
            if (!TryStagePayload(payload, out var stageError))
            {
                return new Result(false, stageError);
            }

            // Prefer thin launcher over deprecated ingest COM.
            TryDeleteRegistryKey(@"Software\Microsoft\Office\Outlook\Addins\Orbit.OutlookAddIn.Connect");

            EnsureLockbackBypass();
            ClearOutlookQuarantine();
            RegisterCom(InstalledDllPath);
            RegisterAddIn();

            OrbitPushActivation.EnsureProtocolRegistered();

            var restart = IsOutlookRunning()
                ? " Restart Classic Outlook if the ribbon button does not appear."
                : " Start Classic Outlook — Mail tab → Send to Orbit.";

            return new Result(true, "Outlook add-in installed." + restart);
        }
        catch (Exception ex)
        {
            return new Result(false, "Install failed: " + ex.Message);
        }
    }

    public static Result Uninstall()
    {
        try
        {
            TryDeleteRegistryKey(@"Software\Microsoft\Office\Outlook\Addins\" + ProgId);
            TryDeleteRegistryKey(@"Software\Classes\CLSID\" + Clsid);
            TryDeleteRegistryKey(@"Software\Classes\" + ProgId);
            ClearOutlookQuarantine();

            if (IsOutlookRunning())
            {
                return new Result(
                    true,
                    "Outlook add-in unregistered. Close Outlook, then delete leftover files under LocalAppData\\Orbit\\OutlookLauncher if desired.");
            }

            try
            {
                if (Directory.Exists(InstallDirectory))
                {
                    Directory.Delete(InstallDirectory, recursive: true);
                }
            }
            catch
            {
                // Registry cleared; files can linger if locked.
            }

            return new Result(true, "Outlook add-in uninstalled.");
        }
        catch (Exception ex)
        {
            return new Result(false, "Uninstall failed: " + ex.Message);
        }
    }

    /// <summary>
    /// If the add-in is already registered, refresh DLL from the packaged payload
    /// (best-effort; skips when Outlook has the file locked).
    /// </summary>
    public static void TrySyncInstalledPayload()
    {
        if (!IsRegistered())
        {
            return;
        }

        var payload = FindPayloadDirectory();
        if (payload is null)
        {
            return;
        }

        try
        {
            _ = TryStagePayload(payload, out _);
        }
        catch
        {
            // ignore — Settings Install can retry
        }
    }

    public static string? FindPayloadDirectory()
    {
        var candidates = new List<string>();

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "outlook-launcher"));

        // Dev: repo layout from bin\...\win-x64 → src\Orbit.OutlookLauncher\bin\{Config}\net48
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var launcherProj = Path.Combine(dir.FullName, "src", "Orbit.OutlookLauncher");
                if (!Directory.Exists(launcherProj))
                {
                    continue;
                }

                foreach (var config in new[] { "Release", "Debug" })
                {
                    candidates.Add(Path.Combine(launcherProj, "bin", "x64", config, "net48"));
                    candidates.Add(Path.Combine(launcherProj, "bin", config, "net48"));
                }

                break;
            }
        }
        catch
        {
            // ignore
        }

        // Already-staged LocalAppData counts as payload for re-register.
        candidates.Add(InstallDirectory);

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(path, DllFileName)))
            {
                return path;
            }
        }

        return null;
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Office\Outlook\Addins\" + ProgId);
            if (key is null)
            {
                return false;
            }

            var load = key.GetValue("LoadBehavior");
            return load is int i && i == 3
                   || load is long l && l == 3;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsOutlookRunning()
    {
        try
        {
            return Process.GetProcessesByName("OUTLOOK").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStagePayload(string payloadDir, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            foreach (var file in Directory.GetFiles(payloadDir))
            {
                var name = Path.GetFileName(file);
                var dest = Path.Combine(InstallDirectory, name);
                if (!TryCopyReplace(file, dest, out error))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCopyReplace(string source, string dest, out string error)
    {
        error = string.Empty;
        try
        {
            File.Copy(source, dest, overwrite: true);
            return true;
        }
        catch (IOException) when (File.Exists(dest))
        {
            // Outlook often locks the loaded DLL — rename aside, then copy.
            try
            {
                var bak = dest + ".old";
                if (File.Exists(bak))
                {
                    try
                    {
                        File.Delete(bak);
                    }
                    catch
                    {
                        bak = dest + "." + Guid.NewGuid().ToString("N") + ".old";
                    }
                }

                File.Move(dest, bak);
                File.Copy(source, dest, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error =
                    "Could not update the Outlook add-in DLL while Outlook is running. "
                    + "Close Classic Outlook and click Install / Update again. ("
                    + ex.Message
                    + ")";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RegisterCom(string dllPath)
    {
        var codeBase = new Uri(dllPath).AbsoluteUri;
        var asmName = $"Orbit.OutlookLauncher, Version={AssemblyVersion}, Culture=neutral, PublicKeyToken=null";

        TryDeleteRegistryKey(@"Software\Classes\CLSID\" + Clsid);

        using (var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + Clsid))
        {
            cls.SetValue(string.Empty, "Orbit Outlook Launcher");
            cls.CreateSubKey("Programmable");
        }

        SetNetComInproc(@"Software\Classes\CLSID\" + Clsid + @"\InprocServer32", asmName, codeBase);
        SetNetComInproc(
            @"Software\Classes\CLSID\" + Clsid + @"\InprocServer32\" + AssemblyVersion,
            asmName,
            codeBase);

        using (var cat = Registry.CurrentUser.CreateSubKey(
                   @"Software\Classes\CLSID\" + Clsid + @"\Implemented Categories\{B65AD801-ABAF-11D0-BB8B-00A0C90F2744}"))
        {
            _ = cat;
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + Clsid + @"\ProgId"))
        {
            progIdKey.SetValue(string.Empty, ProgId);
        }

        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            prog.SetValue(string.Empty, ProgId);
            using var clsidKey = prog.CreateSubKey("CLSID");
            clsidKey.SetValue(string.Empty, Clsid);
        }
    }

    private static void SetNetComInproc(string keyPath, string asmName, string codeBase)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(string.Empty, "mscoree.dll");
        key.SetValue("Assembly", asmName);
        key.SetValue("Class", "Orbit.OutlookLauncher.Connect");
        key.SetValue("RuntimeVersion", "v4.0.30319");
        key.SetValue("ThreadingModel", "Both");
        key.SetValue("CodeBase", codeBase);
    }

    private static void RegisterAddIn()
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Office\Outlook\Addins\" + ProgId);
        key.SetValue("FriendlyName", "Orbit");
        key.SetValue("Description", "Send selected mail to the Orbit app (launch only)");
        key.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
    }

    private static void EnsureLockbackBypass()
    {
        const string lockback = @"Software\Classes\Interface\{000C0601-0000-0000-C000-000000000046}";
        using var key = Registry.CurrentUser.CreateSubKey(lockback);
        if (key.GetValue(string.Empty) is null)
        {
            key.SetValue(string.Empty, "Office .NET Framework Lockback Bypass Key");
        }
    }

    private static void ClearOutlookQuarantine()
    {
        TryDeleteRegistryKey(@"Software\Microsoft\Office\16.0\Outlook\Resiliency\CrashingAddinList");
        TryDeleteRegistryKey(@"Software\Microsoft\Office\16.0\Outlook\Resiliency\DisabledItems");
        TryDeleteRegistryKey(@"Software\Microsoft\Office\16.0\Outlook\Addins\" + ProgId);

        using var donot = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList");
        donot.SetValue(ProgId, 1, RegistryValueKind.DWord);
    }

    private static void TryDeleteRegistryKey(string relativePath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }
}
