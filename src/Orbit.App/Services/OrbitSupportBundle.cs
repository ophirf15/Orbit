using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Diagnostics;
using Orbit.Infrastructure.Settings;

namespace Orbit_App.Services;

/// <summary>
/// Builds a forwardable support zip: Host diagnostics + local logs + Outlook status.
/// </summary>
public static class OrbitSupportBundle
{
    public sealed record Result(bool Ok, string Message, string? ZipPath);

    public static async Task<Result> ExportAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        CancellationToken ct = default)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
            var dir = Path.Combine(settings.GeneratedFilesRoot, "diagnostics");
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, $"orbit-support-{stamp}.zip");

            string? hostDiagnosticsPath = null;
            string? hostError = null;
            try
            {
                using var client = new CoreHostClient(settings, store);
                hostDiagnosticsPath = await client.ExportDiagnosticsFileAsync("json", ct);
            }
            catch (Exception ex)
            {
                hostError = ex.Message;
                OrbitSupportLog.Write("support-bundle", "Host diagnostics export failed", ex);
            }

            var outlook = OutlookLauncherSetup.GetStatus();
            var context = new Dictionary<string, object?>
            {
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["machineName"] = Environment.MachineName,
                ["userName"] = Environment.UserName,
                ["osVersion"] = Environment.OSVersion.ToString(),
                ["appBaseDirectory"] = AppContext.BaseDirectory,
                ["coreHostBaseUrl"] = settings.CoreHostBaseUrl,
                ["hermesBaseUrl"] = settings.HermesBaseUrl,
                ["localDataRoot"] = settings.LocalDataRoot,
                ["generatedFilesRoot"] = settings.GeneratedFilesRoot,
                ["backgroundHostEnabled"] = settings.BackgroundHostEnabled,
                ["outlookAddIn"] = new
                {
                    outlook.IsRegistered,
                    outlook.InstalledFilesPresent,
                    outlook.PayloadAvailable,
                    outlook.OutlookRunning,
                    outlook.Summary,
                    loadBehavior = OutlookLauncherSetup.ReadLoadBehavior(),
                    progId = OutlookLauncherSetup.ProgId,
                },
                ["hostDiagnosticsError"] = hostError,
                ["redactions"] = new[] { "apiKeys", "emailBodies", "hermesKeyFileContents" },
            };

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "app-context.json", JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true }));

                if (!string.IsNullOrWhiteSpace(hostDiagnosticsPath) && File.Exists(hostDiagnosticsPath))
                {
                    zip.CreateEntryFromFile(hostDiagnosticsPath, "host-diagnostics.json", CompressionLevel.Optimal);
                }

                AddLogIfPresent(zip, OrbitSupportLog.AppLogPath, "logs/orbit-app.log");
                AddLogIfPresent(zip, OrbitSupportLog.HostLogPath, "logs/orbit-host.log");
                AddLogIfPresent(zip, OrbitSupportLog.ErrorsJsonlPath, "logs/errors.jsonl");
                AddLogIfPresent(
                    zip,
                    Path.Combine(OrbitSupportLog.LogDirectory, "outlook-launcher.log"),
                    "logs/outlook-launcher.log");

                // Tail copies even if files are huge / locked mid-write.
                WriteEntry(zip, "logs/orbit-app.tail.txt", OrbitSupportLog.ReadTail(OrbitSupportLog.AppLogPath));
                WriteEntry(zip, "logs/orbit-host.tail.txt", OrbitSupportLog.ReadTail(OrbitSupportLog.HostLogPath));
                WriteEntry(
                    zip,
                    "logs/outlook-launcher.tail.txt",
                    OrbitSupportLog.ReadTail(Path.Combine(OrbitSupportLog.LogDirectory, "outlook-launcher.log")));
                WriteEntry(zip, "README.txt", BuildReadme());
            }

            OrbitSupportLog.Write("support-bundle", "Wrote " + zipPath);
            return new Result(
                true,
                "Support bundle ready (redacted). Forward this zip: " + zipPath,
                zipPath);
        }
        catch (Exception ex)
        {
            OrbitSupportLog.WriteErrorEvent("support_bundle_failed", ex.Message, ex.ToString());
            return new Result(false, "Support bundle failed: " + ex.Message, null);
        }
    }

    private static void AddLogIfPresent(ZipArchive zip, string path, string entryName)
    {
        try
        {
            if (File.Exists(path))
            {
                zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
            }
        }
        catch
        {
            // ignore locked files — tail still included
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string BuildReadme() =>
        """
        Orbit support bundle
        ====================
        Safe to forward to Orbit maintainers. API keys and email bodies are redacted/omitted.

        Contents:
        - app-context.json — machine/app URLs (no keys), Outlook add-in status
        - host-diagnostics.json — Core Host redacted diagnostics (when Host was reachable)
        - logs/* — recent Orbit App / Host / Outlook launcher logs and error events

        Reproduce tips for mail push:
        1. Select a message in Classic Outlook
        2. In Orbit: Push Outlook mail (or Ctrl+Shift+O)
        3. If it fails, export this bundle again and send the new zip
        """;
}
