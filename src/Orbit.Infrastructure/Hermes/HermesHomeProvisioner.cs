using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Hermes;

public sealed record HermesHomeProvisionResult(
    string HermesHome,
    bool SoulWrote,
    bool AgentsWrote,
    int SkillsCopied,
    bool McpMerged,
    int ScriptsCopied,
    bool JobsManifestWrote,
    bool BootWrote,
    int CronJobsApplied,
    int FlatSkillsQuarantined,
    bool WebhooksConfigured,
    string? Note);

/// <summary>
/// Provisions native Hermes HERMES_HOME for Orbit (ADR 0027/0028): SOUL, AGENTS,
/// skills/orbit/*/SKILL.md, mcp_servers.orbit merge, portable scripts, cron/webhook
/// manifests, materialized cron/jobs.json, and BOOT.md. Does not fork Hermes.
/// </summary>
public static class HermesHomeProvisioner
{
    private const string SoulMarkerStart = "<!-- orbit:soul -->";
    private const string SoulMarkerEnd = "<!-- /orbit:soul -->";

    public static string DefaultHermesHome =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hermes");

    public static HermesHomeProvisionResult Provision(
        string? hermesHome = null,
        bool overwriteSoul = false,
        string? docsHermesRoot = null,
        string? orbitMcpCommand = null,
        string? orbitCoreUrl = null,
        string? orbitApiKey = null)
    {
        var home = string.IsNullOrWhiteSpace(hermesHome) ? DefaultHermesHome : hermesHome.Trim();
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(home, "skills", "orbit"));

        var docsRoot = docsHermesRoot ?? ResolveDocsHermesRoot();
        if (docsRoot is not null && !File.Exists(Path.Combine(docsRoot, "SOUL.md")))
        {
            docsRoot = null;
        }

        var soulWrote = WriteSoul(home, docsRoot, overwriteSoul);
        var agentsWrote = WriteAgents(home, docsRoot);
        var skills = CopyOrbitSkills(home, docsRoot);
        var mcpMerged = MergeOrbitMcp(home, orbitMcpCommand, orbitCoreUrl, orbitApiKey);
        EnsureEnvHints(home, orbitCoreUrl, orbitApiKey);

        var scriptsCopied = CopyPortableScripts(home, docsRoot);
        var jobsManifestWrote = CopyManifests(home, docsRoot);
        var cronJobsApplied = ApplyCronJobsFromManifest(home, docsRoot);
        var bootWrote = WriteBoot(home, docsRoot);
        var flatQuarantined = QuarantineFlatOrbitSkillCollisions(home);
        var webhooksConfigured = ApplyWebhooksFromManifest(home, docsRoot);

        var note = Directory.Exists(Path.Combine(home, "hermes-agent"))
            ? "Native Hermes home present. Restart gateway or /reload-mcp after provision."
            : "HERMES_HOME prepared. Install native Hermes (install.ps1), then: hermes gateway install && hermes gateway start";
        if (docsRoot is null)
        {
            note += " Warning: docs/hermes not found beside Orbit (installer pack missing). "
                + $"Only {skills} fallback skill(s) were copied — cron jobs that need pulse-refresh/duty-scan will skip. "
                + "Reinstall a build that ships docs/hermes, then Connect Hermes again.";
        }
        else if (skills < 6)
        {
            note += $" Warning: expected Orbit skills under docs/hermes/skills/orbit; only copied {skills}.";
        }

        if (flatQuarantined > 0)
        {
            note += $" Quarantined {flatQuarantined} flat Orbit skill collision(s) under skills/_orbit_quarantine/.";
        }

        return new HermesHomeProvisionResult(
            home,
            soulWrote,
            agentsWrote,
            skills,
            mcpMerged,
            scriptsCopied,
            jobsManifestWrote,
            bootWrote,
            cronJobsApplied,
            flatQuarantined,
            webhooksConfigured,
            note);
    }

    private static bool WriteSoul(string home, string? docsRoot, bool overwrite)
    {
        var path = Path.Combine(home, "SOUL.md");
        var sourceContent = Normalize(TryRead(docsRoot, "SOUL.md") ?? EmbeddedFallbackSoul);
        var sourceSection = ExtractMarkedSection(sourceContent) ?? sourceContent.Trim();

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            File.WriteAllText(path, WrapMarkedSection(sourceSection));
            return true;
        }

        var existing = Normalize(File.ReadAllText(path));
        var existingSection = ExtractMarkedSection(existing);

        if (existingSection is not null)
        {
            if (string.Equals(existingSection.Trim(), sourceSection.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(path, ReplaceMarkedSection(existing, sourceSection));
            return true;
        }

        // Legacy SOUL.md without markers (pre-ADR-0028, or an operator-authored file).
        if (overwrite)
        {
            File.WriteAllText(path, WrapMarkedSection(sourceSection));
            return true;
        }

        // Preserve whatever the operator already has; append the owned section once
        // so future provisions can merge instead of appending again.
        var appended = existing.TrimEnd() + "\n\n" + WrapMarkedSection(sourceSection);
        File.WriteAllText(path, Normalize(appended));
        return true;
    }

    private static string? ExtractMarkedSection(string content)
    {
        var match = Regex.Match(
            content,
            @"<!--\s*orbit:soul\s*-->(.*?)<!--\s*/orbit:soul\s*-->",
            RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string WrapMarkedSection(string body) =>
        SoulMarkerStart + "\n" + body.Trim() + "\n" + SoulMarkerEnd + "\n";

    private static string ReplaceMarkedSection(string existing, string newBody)
    {
        var replaced = Regex.Replace(
            existing,
            @"<!--\s*orbit:soul\s*-->.*?<!--\s*/orbit:soul\s*-->",
            _ => WrapMarkedSection(newBody).TrimEnd(),
            RegexOptions.Singleline);
        return Normalize(replaced);
    }

    private static bool WriteAgents(string home, string? docsRoot)
    {
        var path = Path.Combine(home, "AGENTS.md");
        var content = TryRead(docsRoot, "AGENTS.md") ?? EmbeddedFallbackAgents;
        File.WriteAllText(path, Normalize(content));
        return true;
    }

    private static int CopyOrbitSkills(string home, string? docsRoot)
    {
        var destRoot = Path.Combine(home, "skills", "orbit");
        Directory.CreateDirectory(destRoot);
        var count = 0;

        // Prefer Hermes-native trees: docs/hermes/skills/orbit/<name>/SKILL.md
        var orbitSkills = docsRoot is null ? null : Path.Combine(docsRoot, "skills", "orbit");
        if (orbitSkills is not null && Directory.Exists(orbitSkills))
        {
            foreach (var dir in Directory.EnumerateDirectories(orbitSkills))
            {
                var skillMd = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillMd))
                {
                    continue;
                }

                var name = Path.GetFileName(dir);
                var dest = Path.Combine(destRoot, name);
                Directory.CreateDirectory(dest);
                File.Copy(skillMd, Path.Combine(dest, "SKILL.md"), overwrite: true);
                count++;
            }

            if (count > 0)
            {
                return count;
            }
        }

        // Fallback: legacy flat markdown → SKILL.md wrappers
        var names = new[]
        {
            "orbit-ignition",
            "orbit-learn-project",
            "pulse-refresh",
            "channel-to-orbit",
            "duty-scan",
            "chase-waiting",
        };
        foreach (var name in names)
        {
            var body = docsRoot is null ? null : TryRead(docsRoot, Path.Combine("skills", name + ".md"));
            body ??= name == "chase-waiting" ? EmbeddedChaseWaiting : null;
            if (body is null)
            {
                continue;
            }

            var dest = Path.Combine(destRoot, name);
            Directory.CreateDirectory(dest);
            var skill = WrapSkill(name, body);
            File.WriteAllText(Path.Combine(dest, "SKILL.md"), Normalize(skill));
            count++;
        }

        return count;
    }

    /// <summary>
    /// Hermes refuses ambiguous skill names when both <c>skills/&lt;name&gt;.md</c> and
    /// <c>skills/orbit/&lt;name&gt;/SKILL.md</c> exist (cron then skips the job). Orbit owns
    /// the nested SKILL.md tree — move flat duplicates aside on Connect (ADR 0028 / plan 023 U0).
    /// </summary>
    private static int QuarantineFlatOrbitSkillCollisions(string home)
    {
        var orbitRoot = Path.Combine(home, "skills", "orbit");
        if (!Directory.Exists(orbitRoot))
        {
            return 0;
        }

        var skillsRoot = Path.Combine(home, "skills");
        var quarantine = Path.Combine(skillsRoot, "_orbit_quarantine");
        var moved = 0;

        foreach (var dir in Directory.EnumerateDirectories(orbitRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name) || !File.Exists(Path.Combine(dir, "SKILL.md")))
            {
                continue;
            }

            var flat = Path.Combine(skillsRoot, name + ".md");
            if (!File.Exists(flat))
            {
                continue;
            }

            Directory.CreateDirectory(quarantine);
            var dest = Path.Combine(quarantine, name + ".md");
            if (File.Exists(dest))
            {
                dest = Path.Combine(quarantine, name + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".md");
            }

            File.Move(flat, dest, overwrite: false);
            moved++;
        }

        return moved;
    }

    private static string WrapSkill(string name, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: \"Orbit Work Jarvis skill: {name}\"");
        sb.AppendLine("version: 0.1.0");
        sb.AppendLine("author: Orbit");
        sb.AppendLine("platforms: [windows, linux, macos]");
        sb.AppendLine("metadata:");
        sb.AppendLine("  hermes:");
        sb.AppendLine("    tags: [Orbit, Work-Jarvis]");
        sb.AppendLine("    category: orbit");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(body.TrimStart());
        return sb.ToString();
    }

    /// <summary>
    /// Copies portable pre-check/filter scripts (docs/hermes/portable/scripts/*.py)
    /// into $HERMES_HOME/scripts/, where Hermes cron `script=` and webhook route
    /// `script=` resolve them by relative name (ADR 0028 unit U4).
    /// </summary>
    private static int CopyPortableScripts(string home, string? docsRoot)
    {
        var destRoot = Path.Combine(home, "scripts");
        Directory.CreateDirectory(destRoot);

        var srcRoot = docsRoot is null ? null : Path.Combine(docsRoot, "portable", "scripts");
        if (srcRoot is null || !Directory.Exists(srcRoot))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.py"))
        {
            File.Copy(file, Path.Combine(destRoot, Path.GetFileName(file)), overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Copies the environment-neutral cron/webhook manifests into
    /// $HERMES_HOME/orbit/ for reference/debugging. jobs.json itself is
    /// generated separately by <see cref="ApplyCronJobsFromManifest"/>.
    /// </summary>
    private static bool CopyManifests(string home, string? docsRoot)
    {
        var destDir = Path.Combine(home, "orbit");
        Directory.CreateDirectory(destDir);
        var wrote = false;

        var jobsManifestSrc = docsRoot is null
            ? null
            : Path.Combine(docsRoot, "portable", "cron", "jobs.manifest.json");
        if (jobsManifestSrc is not null && File.Exists(jobsManifestSrc))
        {
            File.Copy(jobsManifestSrc, Path.Combine(destDir, "jobs.manifest.json"), overwrite: true);
            wrote = true;
        }

        var webhooksManifestSrc = docsRoot is null
            ? null
            : Path.Combine(docsRoot, "portable", "webhooks.manifest.json");
        if (webhooksManifestSrc is not null && File.Exists(webhooksManifestSrc))
        {
            File.Copy(webhooksManifestSrc, Path.Combine(destDir, "webhooks.manifest.json"), overwrite: true);
            wrote = true;
        }

        return wrote;
    }

    private sealed record ManifestJob(
        string Name,
        string Schedule,
        IReadOnlyList<string> Skills,
        string Prompt,
        string? MonitorScript,
        string Deliver);

    /// <summary>
    /// Materializes docs/hermes/portable/cron/jobs.manifest.json into
    /// $HERMES_HOME/cron/jobs.json, matching the upstream Hermes job schema
    /// (top-level <c>{"jobs":[...],"updated_at":...}</c>; per-job id/name/prompt/
    /// schedule.kind+expr/skills/deliver/repeat/state/enabled/next_run_at/
    /// last_run_at/last_status/created_at/model/provider/script — see
    /// https://hermes-agent.nousresearch.com/docs/developer-guide/cron-internals).
    /// Upserts by logical <c>name</c> so re-provisioning never duplicates a job
    /// and never resets its runtime-owned fields (id, state, run history, pins).
    /// </summary>
    private static int ApplyCronJobsFromManifest(string home, string? docsRoot)
    {
        var manifestPath = docsRoot is null
            ? null
            : Path.Combine(docsRoot, "portable", "cron", "jobs.manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            return 0;
        }

        List<ManifestJob> manifestJobs;
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            manifestJobs = ParseManifestJobs(manifest?["jobs"] as JsonArray);
        }
        catch (JsonException)
        {
            return 0;
        }

        if (manifestJobs.Count == 0)
        {
            return 0;
        }

        var cronDir = Path.Combine(home, "cron");
        Directory.CreateDirectory(cronDir);
        var jobsPath = Path.Combine(cronDir, "jobs.json");

        JsonObject root;
        try
        {
            root = File.Exists(jobsPath)
                ? (JsonNode.Parse(File.ReadAllText(jobsPath)) as JsonObject) ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var jobs = root["jobs"] as JsonArray;
        if (jobs is null)
        {
            jobs = new JsonArray();
            root["jobs"] = jobs;
        }

        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in jobs)
        {
            if (node is JsonObject obj && obj["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                byName[name] = obj;
            }
        }

        var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var appliedCount = 0;

        foreach (var manifestJob in manifestJobs)
        {
            if (byName.TryGetValue(manifestJob.Name, out var existingJob))
            {
                UpdateJobFromManifest(existingJob, manifestJob);
            }
            else
            {
                var newJob = BuildJobFromManifest(manifestJob, nowIso);
                jobs.Add(newJob);
                byName[manifestJob.Name] = newJob;
            }

            appliedCount++;
        }

        root["updated_at"] = nowIso;

        var tmpPath = jobsPath + ".tmp";
        File.WriteAllText(tmpPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, jobsPath, overwrite: true);

        return appliedCount;
    }

    private static List<ManifestJob> ParseManifestJobs(JsonArray? jobsArray)
    {
        var result = new List<ManifestJob>();
        if (jobsArray is null)
        {
            return result;
        }

        foreach (var node in jobsArray)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var name = obj["name"]?.GetValue<string>();
            var schedule = obj["schedule"]?.GetValue<string>();
            var prompt = obj["prompt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(schedule) || string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            var skills = new List<string>();
            if (obj["skills"] is JsonArray skillsArray)
            {
                foreach (var skillNode in skillsArray)
                {
                    var skillName = skillNode?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(skillName))
                    {
                        skills.Add(skillName);
                    }
                }
            }

            var monitorScript = obj["monitor_script"]?.GetValue<string>();
            var deliver = obj["deliver"]?.GetValue<string>();

            result.Add(new ManifestJob(
                name,
                schedule,
                skills,
                prompt,
                string.IsNullOrWhiteSpace(monitorScript) ? null : monitorScript,
                string.IsNullOrWhiteSpace(deliver) ? "local" : deliver));
        }

        return result;
    }

    private static JsonObject BuildJobFromManifest(ManifestJob job, string nowIso) => new()
    {
        ["id"] = GenerateJobId(job.Name),
        ["name"] = job.Name,
        ["prompt"] = job.Prompt,
        ["schedule"] = BuildScheduleNode(job.Schedule),
        ["skills"] = BuildSkillsNode(job.Skills),
        ["deliver"] = job.Deliver,
        ["repeat"] = new JsonObject { ["times"] = null, ["completed"] = 0 },
        ["state"] = "scheduled",
        ["enabled"] = true,
        ["next_run_at"] = null,
        ["last_run_at"] = null,
        ["last_status"] = null,
        ["created_at"] = nowIso,
        ["model"] = null,
        ["provider"] = null,
        ["script"] = job.MonitorScript,
    };

    private static void UpdateJobFromManifest(JsonObject existing, ManifestJob job)
    {
        // Manifest-owned fields refresh every provision.
        existing["prompt"] = job.Prompt;
        existing["schedule"] = BuildScheduleNode(job.Schedule);
        existing["skills"] = BuildSkillsNode(job.Skills);
        existing["deliver"] = job.Deliver;
        existing["script"] = job.MonitorScript;

        // Runtime/user-owned fields (id, state, enabled, next_run_at, last_run_at,
        // last_status, created_at, repeat, model, provider) are left untouched —
        // re-provisioning must never reset run history or a user's model pin.
    }

    private static JsonArray BuildSkillsNode(IReadOnlyList<string> skills)
    {
        var array = new JsonArray();
        foreach (var skill in skills)
        {
            array.Add(skill);
        }

        return array;
    }

    private static JsonObject BuildScheduleNode(string cronExpr) => new()
    {
        ["kind"] = "cron",
        ["expr"] = cronExpr,
        ["display"] = cronExpr,
    };

    /// <summary>
    /// Deterministic 12-hex-char id derived from the logical job name, so the
    /// same manifest produces the same id on any machine (idempotent by name,
    /// stable across re-provisions) without needing to read the id back out.
    /// </summary>
    private static string GenerateJobId(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private static bool WriteBoot(string home, string? docsRoot)
    {
        var path = Path.Combine(home, "BOOT.md");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return false;
        }

        var content = TryRead(docsRoot, Path.Combine("portable", "BOOT.md.template")) ?? EmbeddedFallbackBoot;
        File.WriteAllText(path, Normalize(content));
        return true;
    }

    private static bool MergeOrbitMcp(
        string home,
        string? orbitMcpCommand,
        string? orbitCoreUrl,
        string? orbitApiKey)
    {
        var configPath = Path.Combine(home, "config.yaml");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var mcpPath = ResolveOrbitMcpPath(orbitMcpCommand);
        var block = BuildOrbitMcpYaml(mcpPath);

        if (Regex.IsMatch(existing, @"^\s*orbit\s*:", RegexOptions.Multiline)
            && existing.Contains("mcp_servers", StringComparison.Ordinal))
        {
            // Repair broken/stale orbit MCP command paths (common after first pair before publish).
            var repaired = RepairOrbitMcpBlock(existing, block);
            if (!string.Equals(repaired, existing, StringComparison.Ordinal))
            {
                File.WriteAllText(configPath, Normalize(repaired));
                _ = orbitCoreUrl;
                _ = orbitApiKey;
                return true;
            }

            EnsureSkillsExternalHint(configPath, existing);
            return false;
        }

        string next;
        if (string.IsNullOrWhiteSpace(existing))
        {
            next = "mcp_servers:\n" + Indent(block, 2) + "\n" + SkillsExternalSnippet();
        }
        else if (existing.Contains("mcp_servers:", StringComparison.Ordinal))
        {
            // Insert orbit server under existing mcp_servers.
            next = Regex.Replace(
                existing,
                @"mcp_servers:\s*\n",
                "mcp_servers:\n" + Indent(block, 2) + "\n",
                RegexOptions.Multiline);
            if (ReferenceEquals(next, existing) || next == existing)
            {
                next = existing.TrimEnd() + "\n" + Indent(block, 2) + "\n";
            }

            if (!existing.Contains("external_dirs", StringComparison.Ordinal))
            {
                next = next.TrimEnd() + "\n" + SkillsExternalSnippet();
            }
        }
        else
        {
            next = existing.TrimEnd() + "\n\nmcp_servers:\n" + Indent(block, 2) + "\n" + SkillsExternalSnippet();
        }

        File.WriteAllText(configPath, Normalize(next));
        _ = orbitCoreUrl;
        _ = orbitApiKey;
        return true;
    }

    /// <summary>
    /// Replaces the existing <c>orbit:</c> MCP server block under mcp_servers with a fresh one.
    /// </summary>
    private static string RepairOrbitMcpBlock(string existing, string freshOrbitBlock)
    {
        // Match from "  orbit:" (or "orbit:") through the next top-level mcp server or end of mcp section.
        var replaced = Regex.Replace(
            existing,
            @"(?ms)^([ \t]*)orbit:\s*\n(?:^[ \t]+.*\n?)*",
            Indent(freshOrbitBlock, 2) + "\n",
            RegexOptions.Multiline);
        return replaced;
    }

    private static void EnsureSkillsExternalHint(string configPath, string existing)
    {
        if (existing.Contains("external_dirs", StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(configPath, Normalize(existing.TrimEnd() + "\n" + SkillsExternalSnippet()));
    }

    private static string SkillsExternalSnippet() =>
        "\n# Orbit skills also live under skills/orbit (copied by provisioner).\nskills:\n  external_dirs: []\n";

    private static string BuildOrbitMcpYaml(string mcpCommand)
    {
        // Hermes expands ${VAR} from its .env — keep dollar-brace literal for YAML.
        var envUrl = "\"$" + "{ORBIT_CORE_URL}\"";
        var envKey = "\"$" + "{ORBIT_API_KEY}\"";
        var path = EscapeYaml(mcpCommand);
        var isDll = mcpCommand.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (isDll)
        {
            return string.Join(
                "\n",
                "orbit:",
                "  command: \"dotnet\"",
                "  args: [\"" + path + "\"]",
                "  env:",
                "    ORBIT_CORE_URL: " + envUrl,
                "    ORBIT_API_KEY: " + envKey,
                "  enabled: true",
                "  timeout: 120",
                "  connect_timeout: 60");
        }

        return string.Join(
            "\n",
            "orbit:",
            "  command: \"" + path + "\"",
            "  args: []",
            "  env:",
            "    ORBIT_CORE_URL: " + envUrl,
            "    ORBIT_API_KEY: " + envKey,
            "  enabled: true",
            "  timeout: 120",
            "  connect_timeout: 60");
    }

    private static string ResolveOrbitMcpPath(string? explicitPath)
    {
        // Always publish a LocalAppData copy Hermes can launch after Orbit rebuilds.
        return OrbitMcpPublisher.EnsurePublished(explicitPath);
    }

    private static void EnsureEnvHints(string home, string? orbitCoreUrl, string? orbitApiKey)
    {
        var path = Path.Combine(home, ".env.orbit.example");
        var core = string.IsNullOrWhiteSpace(orbitCoreUrl) ? "http://127.0.0.1:8741" : orbitCoreUrl.Trim();
        var key = string.IsNullOrWhiteSpace(orbitApiKey) ? "replace-with-orbit-core-api-key" : orbitApiKey.Trim();
        var header =
            "# Merge these into Hermes .env (MCP expands $" + "{ORBIT_CORE_URL} / $" + "{ORBIT_API_KEY})\n";
        File.WriteAllText(
            path,
            Normalize(
                header
                + "ORBIT_CORE_URL=" + core + "\n"
                + "ORBIT_API_KEY=" + key + "\n"
                + "WEBHOOK_ENABLED=true\n"
                + "WEBHOOK_PORT=8644\n"
                + "WEBHOOK_SECRET=replace-with-shared-hermes-webhook-secret\n"
                + "ORBIT_HERMES_WEBHOOK_SECRET=replace-with-shared-hermes-webhook-secret\n"));
    }

    /// <summary>
    /// Ensures a shared HMAC secret for Orbit→Hermes webhooks, writes Hermes
    /// <c>platforms.webhook</c> routes from the portable manifest, and mirrors the
    /// secret to Orbit LocalAppData so Core Host can sign POSTs (plan 023 U1).
    /// </summary>
    private static bool ApplyWebhooksFromManifest(string home, string? docsRoot)
    {
        var manifestPath = docsRoot is null
            ? null
            : Path.Combine(docsRoot, "portable", "webhooks.manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            // Still ensure env-ready secret for Connect even without a docs tree.
            _ = EnsureSharedWebhookSecret(home);
            return false;
        }

        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        var routes = manifest?["routes"] as JsonArray;
        if (routes is null || routes.Count == 0)
        {
            return false;
        }

        var secret = EnsureSharedWebhookSecret(home);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        // Hermes .env keys (gateway reads WEBHOOK_*); also alias for manifest secret_env.
        HermesEnvFile.Upsert(
            Path.Combine(home, ".env"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WEBHOOK_ENABLED"] = "true",
                ["WEBHOOK_PORT"] = "8644",
                ["WEBHOOK_SECRET"] = secret,
                ["ORBIT_HERMES_WEBHOOK_SECRET"] = secret,
            });

        var yamlRoutes = new StringBuilder();
        foreach (var node in routes)
        {
            if (node is not JsonObject route)
            {
                continue;
            }

            var name = route["name"]?.GetValue<string>();
            var prompt = route["prompt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            var script = route["script"]?.GetValue<string>();
            var deliver = route["deliver"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(deliver) || string.Equals(deliver, "local", StringComparison.OrdinalIgnoreCase))
            {
                deliver = "log";
            }

            yamlRoutes.AppendLine("        " + name + ":");
            if (route["events"] is JsonArray events && events.Count > 0)
            {
                var eventList = string.Join(
                    ", ",
                    events
                        .Select(e => e?.GetValue<string>())
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .Select(e => "\"" + e!.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
                if (eventList.Length > 0)
                {
                    yamlRoutes.AppendLine("          events: [" + eventList + "]");
                }
            }

            yamlRoutes.AppendLine("          secret: \"" + EscapeYamlDoubleQuoted(secret) + "\"");
            if (!string.IsNullOrWhiteSpace(script))
            {
                yamlRoutes.AppendLine("          script: \"" + EscapeYamlDoubleQuoted(script) + "\"");
            }

            yamlRoutes.AppendLine("          prompt: |");
            foreach (var line in prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                yamlRoutes.AppendLine("            " + line);
            }

            yamlRoutes.AppendLine("          deliver: \"" + EscapeYamlDoubleQuoted(deliver) + "\"");
        }

        if (yamlRoutes.Length == 0)
        {
            return false;
        }

        var configPath = Path.Combine(home, "config.yaml");
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var marker = "# orbit:webhooks";
        var block =
            marker + "\n"
            + "platforms:\n"
            + "  webhook:\n"
            + "    enabled: true\n"
            + "    extra:\n"
            + "      port: 8644\n"
            + "      secret: \"" + EscapeYamlDoubleQuoted(secret) + "\"\n"
            + "      routes:\n"
            + yamlRoutes;

        string next;
        if (existing.Contains(marker, StringComparison.Ordinal))
        {
            var idx = existing.IndexOf(marker, StringComparison.Ordinal);
            next = existing[..idx].TrimEnd() + "\n\n" + block;
        }
        else
        {
            next = string.IsNullOrWhiteSpace(existing)
                ? block
                : existing.TrimEnd() + "\n\n" + block;
        }

        if (string.Equals(Normalize(next), Normalize(existing), StringComparison.Ordinal))
        {
            return existing.Contains("orbit-email-ingested", StringComparison.Ordinal);
        }

        File.WriteAllText(configPath, Normalize(next));
        return true;
    }

    /// <summary>
    /// Returns the shared webhook HMAC, creating Hermes .env + Orbit sidecar when missing.
    /// </summary>
    public static string EnsureSharedWebhookSecret(string? hermesHome = null)
    {
        var home = string.IsNullOrWhiteSpace(hermesHome) ? DefaultHermesHome : hermesHome.Trim();
        Directory.CreateDirectory(home);
        var envPath = Path.Combine(home, ".env");
        var existing = HermesEnvFile.Read(envPath);
        var secret = FirstNonEmpty(
            GetMap(existing, "ORBIT_HERMES_WEBHOOK_SECRET"),
            GetMap(existing, "WEBHOOK_SECRET"),
            TryReadOrbitWebhookSidecar(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant())!;

        HermesEnvFile.Upsert(
            envPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WEBHOOK_ENABLED"] = "true",
                ["WEBHOOK_PORT"] = "8644",
                ["WEBHOOK_SECRET"] = secret,
                ["ORBIT_HERMES_WEBHOOK_SECRET"] = secret,
            });

        var isDefaultHome = string.Equals(
            Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(DefaultHermesHome).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        WriteOrbitWebhookSidecar(secret, writeSidecar: isDefaultHome);
        return secret;
    }

    private static string? TryReadOrbitWebhookSidecar()
    {
        try
        {
            var path = OrbitWebhookSecretPath();
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteOrbitWebhookSidecar(string secret, bool writeSidecar)
    {
        // Unit tests provision into temp HERMES_HOME — never clobber the live Orbit sidecar.
        if (!writeSidecar)
        {
            return;
        }

        var path = OrbitWebhookSecretPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, secret.Trim() + "\n");
    }

    private static string OrbitWebhookSecretPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "hermes-webhook-secret.txt");

    private static string EscapeYamlDoubleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string? GetMap(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").TrimEnd().Split('\n');
        return string.Join("\n", lines.Select(l => string.IsNullOrWhiteSpace(l) ? l : pad + l));
    }

    private static string EscapeYaml(string path) => path.Replace("\\", "/", StringComparison.Ordinal);

    private static string? TryRead(string? docsRoot, string relative)
    {
        if (docsRoot is null)
        {
            return null;
        }

        var path = Path.Combine(docsRoot, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ResolveDocsHermesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "hermes");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "SOUL.md")))
            {
                return candidate;
            }
        }

        // Dev fallback: walk from cwd (Settings / CLI runners).
        try
        {
            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "docs", "hermes");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "SOUL.md")))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private const string EmbeddedFallbackSoul =
        "<!-- orbit:soul -->\n"
        + "# Personality — Orbit Work Jarvis\n\n"
        + "You are Hermes, Orbit's Work Jarvis. Prefer mcp_orbit_* tools. Hierarchy: project → workstreams → tasks. "
        + "Use orbit_create_project for new projects (in orbit by default), orbit_create_workstream for sub-areas, "
        + "orbit_create_task with workstreamId when nesting. Never computer-use the Orbit GUI for mutations.\n"
        + "<!-- /orbit:soul -->\n";

    private const string EmbeddedFallbackAgents =
        "# Orbit conventions\n\n"
        + "Use Orbit MCP tools for projects, tasks, briefs, and memory. Never chat-only mutations.\n";

    private const string EmbeddedChaseWaiting =
        "# Chase waiting\n\n"
        + "Find stalled waiting tasks in Orbit; update nextAction and brief with a concrete chase step via tools.\n";

    private const string EmbeddedFallbackBoot =
        "# BOOT — gateway-start recovery\n\n"
        + "1. Call orbit_get_workbench once. If it fails, say so once and stop retrying.\n"
        + "2. One bounded catch-up via orbit_search / orbit_get_related_context if MCP is healthy; skip if nothing stale.\n"
        + "3. Stay quiet otherwise — no persona restatement, no memory dump.\n"
        + "4. Never create/edit cron jobs from a BOOT session; cron is manifest-driven.\n";
}
