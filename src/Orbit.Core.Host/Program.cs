using System.Text;
using Orbit.Core.Host.Hosting;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Settings;

namespace Orbit.Core.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Console.Error.WriteLine("Orbit Core Host is already running (single-instance mutex held).");
            return 2;
        }

        JsonOrbitSettingsStore store;
        HostOptions options;
        OrbitDatabase database;
        try
        {
            store = new JsonOrbitSettingsStore();
            options = HostStartupGuard.LoadOptions(store);
            HostStartupGuard.EnsureMayListen(options);
            database = OrbitDatabase.Open(options.LocalDataRoot);
            Console.WriteLine($"SQLite: {database.DatabasePath}");

            // Startup reconcile is best-effort; never block host listen for cloud issues.
            try
            {
                var lineage = new Orbit.Infrastructure.Sync.SyncLineageStore(options.LocalDataRoot);
                var sync = new Orbit.Infrastructure.Sync.SnapshotService(
                    database.Factory,
                    lineage,
                    options.LocalDataRoot,
                    options.DeviceId,
                    options.DeviceName,
                    () => options.OneDriveSnapshotFolder);
                var status = sync.Reconcile();
                Console.WriteLine($"Sync: {status.Kind} — {status.Message}");
            }
            catch (Exception syncEx)
            {
                Console.WriteLine($"Sync reconcile skipped: {syncEx.Message}");
            }

            if (args.Any(a => string.Equals(a, "--seed-demo", StringComparison.OrdinalIgnoreCase)))
            {
                var ids = database.SeedDemoIfEmpty();
                Console.WriteLine($"Demo seed ready (Harbor Court={ids.HarborProjectId}, Riverview={ids.RiverviewProjectId}).");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Host startup failed: {ex.Message}");
            return 1;
        }

        var builder = OrbitHostWebApp.CreateBuilder(options, args);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(database.Factory);
        builder.Services.AddSingleton(new ProjectReadStore(database.Factory));
        builder.Services.AddSingleton(new ProjectWriteStore(database.Factory));
        builder.Services.AddSingleton(new WorkbenchLayoutStore(database.Factory));
        builder.Services.AddSingleton(new WorkbenchReadStore(database.Factory));
        builder.Services.AddSingleton(new ProjectContextReadStore(database.Factory));
        // NoteWriteStore + sync services registered in OrbitHostWebApp
        var app = OrbitHostWebApp.BuildApp(builder, options);
        try
        {
            var dismissed = app.Services.GetRequiredService<Orbit.Infrastructure.Suggestions.SuggestionStore>()
                .DismissThinkingOnlyPending();
            if (dismissed > 0)
            {
                Console.WriteLine($"Cleared {dismissed} thinking-only suggestion chore(s).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Suggestion cleanup skipped: {ex.Message}");
        }

        try
        {
            var seeded = SeedOperatorIdentityIfEmpty(app.Services.GetRequiredService<OperatorMemoryStore>());
            if (seeded > 0)
            {
                Console.WriteLine($"Seeded {seeded} operator identity preference fact(s).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Operator identity seed skipped: {ex.Message}");
        }

        Console.WriteLine($"Orbit Core Host listening on {options.ResolveBaseUrl()}");
        Console.WriteLine($"Bind: {options.BindAddress}:{options.Port}");
        Console.WriteLine($"Generated root: {options.GeneratedFilesRoot}");
        Console.WriteLine(PathSafety.IsLoopbackAddress(options.BindAddress)
            ? "Auth: loopback (bearer required only if API key sidecar is present)"
            : "Auth: bearer required (non-loopback bind)");

        try
        {
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Host terminated: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Seeds generic Orbit product rules once when no global preference memory exists.
    /// Personal identity is learned via Ignition + orbit_remember (not hardcoded here).</summary>
    private static int SeedOperatorIdentityIfEmpty(OperatorMemoryStore memory)
    {
        var existing = memory.List(scope: "global", limit: 100)
            .Where(m => string.Equals(m.Kind, OperatorMemoryKinds.Preference, StringComparison.Ordinal)
                && string.Equals(m.Scope, "global", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (existing.Count > 0)
        {
            return 0;
        }

        string[] facts =
        [
            "Orbit is a Work Jarvis home for one operator: Hermes organizes and feeds insight on Pulse — never make them fill blank forms or Accept chores.",
            "Learn who the operator is over time with orbit_remember and Ignition; do not invent a fixed biography.",
            "Attach email to existing tasks/concerns when possible; always leave a living brief (body) and concrete nextAction on the task.",
            "Keep every project in the orbit visually warm: briefs and next moves, not chat dumps.",
        ];

        var count = 0;
        foreach (var text in facts)
        {
            memory.Remember(new RememberRequest
            {
                Text = text,
                Kind = OperatorMemoryKinds.Preference,
                Scope = "global",
                Source = "host-startup-seed",
                Confidence = 1.0,
            });
            count++;
        }

        return count;
    }
}
