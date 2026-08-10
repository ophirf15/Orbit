using System.Text.Json.Nodes;
using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class HermesHomeProvisionerTests
{
    [Fact]
    public void Provision_OnEmptyHome_CreatesSkillsScriptsManifestsAndCronJobs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-provision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = HermesHomeProvisioner.Provision(hermesHome: dir);

            Assert.True(result.SoulWrote);
            var soul = File.ReadAllText(Path.Combine(dir, "SOUL.md"));
            Assert.Contains("<!-- orbit:soul -->", soul, StringComparison.Ordinal);
            Assert.Contains("<!-- /orbit:soul -->", soul, StringComparison.Ordinal);

            Assert.True(result.SkillsCopied > 0);
            Assert.True(Directory.Exists(Path.Combine(dir, "skills", "orbit", "duty-scan")));
            Assert.True(Directory.Exists(Path.Combine(dir, "skills", "orbit", "pulse-refresh")));

            Assert.True(result.ScriptsCopied >= 2);
            Assert.True(File.Exists(Path.Combine(dir, "scripts", "orbit-pulse-monitor.py")));
            Assert.True(File.Exists(Path.Combine(dir, "scripts", "orbit-event-filter.py")));

            Assert.True(result.JobsManifestWrote);
            Assert.True(File.Exists(Path.Combine(dir, "orbit", "jobs.manifest.json")));
            Assert.True(File.Exists(Path.Combine(dir, "orbit", "webhooks.manifest.json")));

            Assert.Equal(4, result.CronJobsApplied);
            var jobsPath = Path.Combine(dir, "cron", "jobs.json");
            Assert.True(File.Exists(jobsPath));

            var root = JsonNode.Parse(File.ReadAllText(jobsPath))!.AsObject();
            var jobs = root["jobs"]!.AsArray();
            Assert.Equal(4, jobs.Count);

            var names = jobs.Select(j => j!["name"]!.GetValue<string>()).ToList();
            Assert.Contains("orbit-duty-scan-morning", names);
            Assert.Contains("orbit-duty-scan-evening", names);
            Assert.Contains("orbit-pulse-monitor", names);
            Assert.Contains("orbit-chase-waiting", names);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

            // Canonical skills live under skills/orbit/*/SKILL.md — no flat collisions.
            Assert.True(File.Exists(Path.Combine(dir, "skills", "orbit", "duty-scan", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(dir, "skills", "duty-scan.md")));
            Assert.Equal(0, result.FlatSkillsQuarantined);

            Assert.True(result.WebhooksConfigured);
            var config = File.ReadAllText(Path.Combine(dir, "config.yaml"));
            Assert.Contains("orbit-email-ingested", config, StringComparison.Ordinal);
            Assert.Contains("WEBHOOK_ENABLED=true", File.ReadAllText(Path.Combine(dir, ".env")), StringComparison.Ordinal);

            var pulseJob = jobs.First(j => j!["name"]!.GetValue<string>() == "orbit-pulse-monitor")!.AsObject();
            Assert.Equal("orbit-pulse-monitor.py", pulseJob["script"]!.GetValue<string>());
            Assert.Equal("cron", pulseJob["schedule"]!["kind"]!.GetValue<string>());
            Assert.True(pulseJob["enabled"]!.GetValue<bool>());
            Assert.Null(pulseJob["next_run_at"]);

            Assert.True(result.BootWrote);
            Assert.True(File.Exists(Path.Combine(dir, "BOOT.md")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Provision_SecondRun_IsIdempotent_NoDuplicateCronJobsAndPreservesRunState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-provision-idem-" + Guid.NewGuid().ToString("N"));
        try
        {
            HermesHomeProvisioner.Provision(hermesHome: dir);
            var jobsPath = Path.Combine(dir, "cron", "jobs.json");

            var firstRoot = JsonNode.Parse(File.ReadAllText(jobsPath))!.AsObject();
            var firstJobs = firstRoot["jobs"]!.AsArray();
            var firstIds = firstJobs
                .Select(j => j!["id"]!.GetValue<string>())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            // Simulate Hermes having actually run/pinned one of the jobs before Connect runs again.
            var pulseJob = firstJobs.First(j => j!["name"]!.GetValue<string>() == "orbit-pulse-monitor")!.AsObject();
            pulseJob["last_status"] = "ok";
            pulseJob["last_run_at"] = "2026-08-09T12:00:00.000Z";
            pulseJob["enabled"] = false;
            pulseJob["model"] = "operator-pinned-model";
            File.WriteAllText(jobsPath, firstRoot.ToJsonString());

            var second = HermesHomeProvisioner.Provision(hermesHome: dir);

            Assert.False(second.SoulWrote);
            Assert.Equal(4, second.CronJobsApplied);

            var secondRoot = JsonNode.Parse(File.ReadAllText(jobsPath))!.AsObject();
            var secondJobs = secondRoot["jobs"]!.AsArray();
            Assert.Equal(4, secondJobs.Count);

            var secondIds = secondJobs.Select(j => j!["id"]!.GetValue<string>()).ToList();
            Assert.Equal(secondIds.Count, secondIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(firstIds, secondIds.OrderBy(id => id, StringComparer.Ordinal).ToList());

            var pulseAfter = secondJobs.First(j => j!["name"]!.GetValue<string>() == "orbit-pulse-monitor")!.AsObject();
            Assert.Equal("ok", pulseAfter["last_status"]!.GetValue<string>());
            Assert.Equal("2026-08-09T12:00:00.000Z", pulseAfter["last_run_at"]!.GetValue<string>());
            Assert.False(pulseAfter["enabled"]!.GetValue<bool>());
            Assert.Equal("operator-pinned-model", pulseAfter["model"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Provision_WithoutDocsPack_CopiesOnlyChaseWaitingFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-nodocs-" + Guid.NewGuid().ToString("N"));
        var missingDocs = Path.Combine(Path.GetTempPath(), "orbit-missing-docs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = HermesHomeProvisioner.Provision(hermesHome: dir, docsHermesRoot: missingDocs);

            Assert.Equal(1, result.SkillsCopied);
            Assert.True(Directory.Exists(Path.Combine(dir, "skills", "orbit", "chase-waiting")));
            Assert.False(Directory.Exists(Path.Combine(dir, "skills", "orbit", "pulse-refresh")));
            Assert.Equal(0, result.CronJobsApplied);
            Assert.Contains("docs/hermes not found", result.Note!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Provision_FromPackagedDocsLayout_CopiesAllOrbitSkills()
    {
        var install = Path.Combine(Path.GetTempPath(), "orbit-install-layout-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(Path.GetTempPath(), "orbit-hermes-packaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repoDocs = FindRepoDocsHermes();
            Assert.False(string.IsNullOrWhiteSpace(repoDocs));
            CopyDirectory(repoDocs!, Path.Combine(install, "docs", "hermes"));

            var result = HermesHomeProvisioner.Provision(
                hermesHome: home,
                docsHermesRoot: Path.Combine(install, "docs", "hermes"));

            Assert.True(result.SkillsCopied >= 6);
            Assert.True(Directory.Exists(Path.Combine(home, "skills", "orbit", "pulse-refresh")));
            Assert.True(Directory.Exists(Path.Combine(home, "skills", "orbit", "duty-scan")));
            Assert.True(result.CronJobsApplied >= 4);
            Assert.DoesNotContain("docs/hermes not found", result.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(install))
            {
                Directory.Delete(install, recursive: true);
            }

            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    [Fact]
    public void Provision_QuarantinesFlatOrbitSkillCollisions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-quarantine-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "skills"));
            File.WriteAllText(Path.Combine(dir, "skills", "duty-scan.md"), "# legacy flat collision\n");
            File.WriteAllText(Path.Combine(dir, "skills", "pulse-refresh.md"), "# legacy flat collision\n");

            var result = HermesHomeProvisioner.Provision(hermesHome: dir);

            Assert.True(result.FlatSkillsQuarantined >= 2);
            Assert.False(File.Exists(Path.Combine(dir, "skills", "duty-scan.md")));
            Assert.False(File.Exists(Path.Combine(dir, "skills", "pulse-refresh.md")));
            Assert.True(File.Exists(Path.Combine(dir, "skills", "orbit", "duty-scan", "SKILL.md")));
            Assert.True(Directory.Exists(Path.Combine(dir, "skills", "_orbit_quarantine")));
            Assert.True(File.Exists(Path.Combine(dir, "skills", "_orbit_quarantine", "duty-scan.md")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string? FindRepoDocsHermes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "hermes");
            if (File.Exists(Path.Combine(candidate, "SOUL.md")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
