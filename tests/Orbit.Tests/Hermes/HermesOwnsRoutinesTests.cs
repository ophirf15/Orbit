using Orbit.Core.Agent;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Changes;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class HermesOwnsRoutinesTests
{
    [Fact]
    public void OperatorPromptBuilder_IsSlim_NoPersona()
    {
        var prompt = OperatorPromptBuilder.Build(OperatorTriggers.CalendarSoon, """{"meetings":[]}""");
        Assert.DoesNotContain("You are Hermes", prompt, StringComparison.Ordinal);
        Assert.Contains("Trigger: calendar.soon", prompt, StringComparison.Ordinal);
        Assert.Contains("SOUL.md", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeLogStore_CursorIsMonotonic_EmptyDelta()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var log = new ChangeLogStore(factory);
        Assert.Equal(0, log.CurrentCursor());
        var (empty, next0) = log.ListSince(0);
        Assert.Empty(empty);
        Assert.Equal(0, next0);

        var r1 = log.Append("task", "t1", "updated", "task.updated");
        var r2 = log.Append("email", "e1", "updated", "email.ingested");
        Assert.True(r2 > r1);

        var (page, next) = log.ListSince(r1);
        Assert.Single(page);
        Assert.Equal(r2, next);
        Assert.Equal(r2, log.CurrentCursor());

        var (again, nextAgain) = log.ListSince(r2);
        Assert.Empty(again);
        Assert.Equal(r2, nextAgain);
    }

    [Fact]
    public void HermesHomeProvisioner_IdempotentCronJobs()
    {
        var docs = FindDocsHermes();
        Assert.False(string.IsNullOrWhiteSpace(docs), "docs/hermes not found from test host");

        using var temp = new TempHome();
        var first = HermesHomeProvisioner.Provision(hermesHome: temp.Root, docsHermesRoot: docs);
        Assert.True(first.ScriptsCopied >= 1 || File.Exists(Path.Combine(temp.Root, "scripts", "orbit-pulse-monitor.py")));
        Assert.True(first.JobsManifestWrote || File.Exists(Path.Combine(temp.Root, "orbit", "jobs.manifest.json")));
        Assert.True(first.CronJobsApplied >= 1);
        Assert.True(first.BootWrote);

        var jobsPath = Path.Combine(temp.Root, "cron", "jobs.json");
        Assert.True(File.Exists(jobsPath));
        var firstJson = File.ReadAllText(jobsPath);

        var second = HermesHomeProvisioner.Provision(hermesHome: temp.Root, docsHermesRoot: docs);
        Assert.Equal(first.CronJobsApplied, second.CronJobsApplied);
        var secondJson = File.ReadAllText(jobsPath);

        // Same logical job names — count of "orbit-duty-scan-morning" stays 1
        Assert.Equal(
            CountName(firstJson, "orbit-duty-scan-morning"),
            CountName(secondJson, "orbit-duty-scan-morning"));
        Assert.Equal(1, CountName(secondJson, "orbit-duty-scan-morning"));
        Assert.Equal(1, CountName(secondJson, "orbit-pulse-monitor"));
    }

    private static int CountName(string json, string name) =>
        System.Text.RegularExpressions.Regex.Matches(json, $"\"name\"\\s*:\\s*\"{name}\"").Count;

    private static string? FindDocsHermes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "hermes");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitChangeLogTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "orbit.db");

        public TempDb() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
    }

    private sealed class TempHome : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitHermesProvision", Guid.NewGuid().ToString("N"));

        public TempHome() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
    }
}
