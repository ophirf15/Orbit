using System.Diagnostics;
using Microsoft.Win32;
using Orbit.Infrastructure.Diagnostics;

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
        bool DisabledByOutlook,
        string? PayloadDirectory,
        string Summary);

    public sealed record Result(bool Ok, string Message);

    public static Status GetStatus()
    {
        var payload = FindPayloadDirectory();
        var loadBehavior = ReadLoadBehavior();
        var registered = loadBehavior == 3;
        var disabled = loadBehavior is 0 or 2;
        var files = File.Exists(InstalledDllPath);
        var outlook = IsOutlookRunning();
        string summary;
        if (disabled && files)
        {
            summary = outlook
                ? "Installed but disabled by Outlook (slow-start quarantine). Click Install / Update, then restart Outlook. If prompted that Orbit slows startup, choose Always enable."
                : "Installed but disabled by Outlook. Click Install / Update to clear quarantine, then start Outlook and choose Always enable if prompted.";
        }
        else if (registered)
        {
            summary = files
                ? outlook
                    ? "Installed — Classic Outlook is open (close it before updating the add-in DLL)."
                    : "Installed — Mail tab shows Send to Orbit after Outlook restart if needed."
                : "Registered, but DLL missing — click Install / Update.";
        }
        else if (payload is null)
        {
            summary = "Not installed — Outlook launcher payload missing from this Orbit build.";
        }
        else
        {
            summary = "Not installed — click Install to add Send to Orbit in Classic Outlook.";
        }

        return new Status(registered, payload is not null, files, outlook, disabled, payload, summary);
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

            MotwUnblocker.UnblockDirectory(InstallDirectory);

            // Prefer thin launcher over deprecated ingest COM.
            TryDeleteRegistryKeyAllViews(@"Software\Microsoft\Office\Outlook\Addins\Orbit.OutlookAddIn.Connect");
            TryDeleteRegistryKeyAllViews(@"Software\Microsoft\Office\16.0\Outlook\Addins\Orbit.OutlookAddIn.Connect");

            EnsureLockbackBypass();
            ClearOutlookQuarantine();
            RegisterCom(InstalledDllPath);
            RegisterAddIn();
            ForceEnableLoadBehavior();
            PinDoNotDisable();

            OrbitPushActivation.EnsureProtocolRegistered();

            var restart = IsOutlookRunning()
                    ? " Close Classic Outlook completely (tray too), then reopen. If COM Add-ins flips Orbit off immediately, Outlook failed to load the DLL — close Outlook, Install again, then reopen (file locks while Outlook is open are the usual cause)."
                : " Start Classic Outlook. If it warns about slow startup, choose Always enable. File → Options → Add-ins → COM Add-ins should show Orbit checked.";

            return new Result(true, "Outlook add-in installed (64-bit Classic M365 + registry dual-write)." + restart);
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
            TryDeleteRegistryKeyAllViews(@"Software\Microsoft\Office\Outlook\Addins\" + ProgId);
            TryDeleteRegistryKeyAllViews(@"Software\Microsoft\Office\16.0\Outlook\Addins\" + ProgId);
            TryDeleteRegistryKeyAllViews(@"Software\Microsoft\Office\15.0\Outlook\Addins\" + ProgId);
            TryDeleteRegistryKeyAllViews(@"Software\Classes\CLSID\" + Clsid);
            TryDeleteRegistryKeyAllViews(@"Software\Classes\" + ProgId);
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

    public static bool IsRegistered() => ReadLoadBehavior() == 3;

    /// <summary>Outlook LoadBehavior DWORD, or null if the add-in key is missing.</summary>
    public static int? ReadLoadBehavior()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                foreach (var path in new[]
                         {
                             @"Software\Microsoft\Office\Outlook\Addins\" + ProgId,
                             @"Software\Microsoft\Office\16.0\Outlook\Addins\" + ProgId,
                         })
                {
                    using var key = hive.OpenSubKey(path);
                    if (key is null)
                    {
                        continue;
                    }

                    var load = key.GetValue("LoadBehavior");
                    var value = load switch
                    {
                        int i => i,
                        long l => (int)l,
                        _ => (int?)null,
                    };
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // try next view
            }
        }

        return null;
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
            MotwUnblocker.UnblockFile(dest);
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
                MotwUnblocker.UnblockFile(dest);
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

        // Register CLSID in both registry views — covers 64-bit M365 Classic and any 32-bit Outlook.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            TryDeleteSubKeyTree(hive, @"Software\Classes\CLSID\" + Clsid);
            TryDeleteSubKeyTree(hive, @"Software\Classes\" + ProgId);

            using (var cls = hive.CreateSubKey(@"Software\Classes\CLSID\" + Clsid))
            {
                cls.SetValue(string.Empty, "Orbit Outlook Launcher");
                cls.CreateSubKey("Programmable");
            }

            SetNetComInproc(hive, @"Software\Classes\CLSID\" + Clsid + @"\InprocServer32", asmName, codeBase);
            SetNetComInproc(
                hive,
                @"Software\Classes\CLSID\" + Clsid + @"\InprocServer32\" + AssemblyVersion,
                asmName,
                codeBase);

            using (var cat = hive.CreateSubKey(
                       @"Software\Classes\CLSID\" + Clsid + @"\Implemented Categories\{B65AD801-ABAF-11D0-BB8B-00A0C90F2744}"))
            {
                _ = cat;
            }

            using (var progIdKey = hive.CreateSubKey(@"Software\Classes\CLSID\" + Clsid + @"\ProgId"))
            {
                progIdKey.SetValue(string.Empty, ProgId);
            }

            using (var prog = hive.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                prog.SetValue(string.Empty, ProgId);
                using var clsidKey = prog.CreateSubKey("CLSID");
                clsidKey.SetValue(string.Empty, Clsid);
            }
        }
    }

    private static void SetNetComInproc(RegistryKey hive, string keyPath, string asmName, string codeBase)
    {
        using var key = hive.CreateSubKey(keyPath);
        key.SetValue(string.Empty, "mscoree.dll");
        key.SetValue("Assembly", asmName);
        key.SetValue("Class", "Orbit.OutlookLauncher.Connect");
        key.SetValue("RuntimeVersion", "v4.0.30319");
        key.SetValue("ThreadingModel", "Both");
        key.SetValue("CodeBase", codeBase);
    }

    private static void RegisterAddIn()
    {
        // Both the version-agnostic and 16.0 (M365/2016+) add-in keys — Outlook 365 reads both.
        WriteAddInLoadBehavior(@"Software\Microsoft\Office\Outlook\Addins\" + ProgId);
        WriteAddInLoadBehavior(@"Software\Microsoft\Office\16.0\Outlook\Addins\" + ProgId);
        WriteAddInLoadBehavior(@"Software\Microsoft\Office\15.0\Outlook\Addins\" + ProgId);
    }

    private static void WriteAddInLoadBehavior(string relativePath)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = hive.CreateSubKey(relativePath);
                key.SetValue("FriendlyName", "Orbit");
                key.SetValue("Description", "Send selected mail to the Orbit app (launch only)");
                key.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
            }
            catch
            {
                // ignore per-view failures
            }
        }
    }

    private static void ForceEnableLoadBehavior() => RegisterAddIn();

    private static void EnsureLockbackBypass()
    {
        const string lockback = @"Software\Classes\Interface\{000C0601-0000-0000-C000-000000000046}";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = hive.CreateSubKey(lockback);
                if (key.GetValue(string.Empty) is null)
                {
                    key.SetValue(string.Empty, "Office .NET Framework Lockback Bypass Key");
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Clears Outlook's "disabled for slowing startup / crashing" quarantine and pins
    /// DoNotDisable so the thin launcher stays on (HKCU only — no admin required).
    /// </summary>
    private static void ClearOutlookQuarantine()
    {
        foreach (var version in new[] { "16.0", "15.0", "14.0" })
        {
            var root = $@"Software\Microsoft\Office\{version}\Outlook\Resiliency";
            TryDeleteRegistryKeyAllViews(root + @"\CrashingAddinList");
            TryDeleteRegistryKeyAllViews(root + @"\DisabledItems");
            TryDeleteRegistryKeyAllViews(root + @"\StartupItems");

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    using var times = hive.OpenSubKey(
                        $@"Software\Microsoft\Office\{version}\Outlook\AddInLoadTimes",
                        writable: true);
                    times?.DeleteValue(ProgId, throwOnMissingValue: false);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    using var donot = hive.CreateSubKey(root + @"\DoNotDisableAddinList");
                    // 1 = boot-load disable reason — keep enabled (MS resiliency docs).
                    donot.SetValue(ProgId, 1, RegistryValueKind.DWord);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    using var allow = hive.CreateSubKey(root + @"\AddinList");
                    allow.SetValue(ProgId, 1, RegistryValueKind.DWord);
                }
                catch
                {
                    // ignore
                }

                // Policy-style always-enable (when policies hive is writable for the user).
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    using var policy = hive.CreateSubKey(
                        $@"Software\Policies\Microsoft\Office\{version}\Outlook\Resiliency\AddinList");
                    policy.SetValue(ProgId, 1, RegistryValueKind.DWord);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static void PinDoNotDisable() => ClearOutlookQuarantine();

    private static void TryDeleteRegistryKeyAllViews(string relativePath)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                TryDeleteSubKeyTree(hive, relativePath);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryDeleteSubKeyTree(RegistryKey hive, string relativePath)
    {
        try
        {
            hive.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }
}
