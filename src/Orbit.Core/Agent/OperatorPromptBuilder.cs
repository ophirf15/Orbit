using System.Text;
using Orbit.Core.Operator;

namespace Orbit.Core.Agent;

/// <summary>
/// Slim Hermes wake prompt (ADR 0028). Identity lives in SOUL.md; playbooks live in Orbit skills.
/// Host only forwards trigger kind + compact payload (+ email snapshot for ingest).
/// </summary>
public static class OperatorPromptBuilder
{
    private const int MaxPayloadChars = 4000;
    private const int MaxEmailSnapshotChars = 6000;
    private const int MaxRelationMemoryChars = 1500;

    public static string Build(
        string triggerKind,
        string? triggerPayloadJson,
        string? emailSnapshotJson = null,
        IReadOnlyList<string>? emailRelationMemory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Orbit Core wake (slim). Identity and standing rules live in SOUL.md / Hermes memory.");
        sb.AppendLine("Use Orbit MCP tools (mcp_orbit_*) for truth. Prefer skills: duty-scan, pulse-refresh, channel-to-orbit, briefing-distill.");
        sb.AppendLine("Mutate when the match is clear; keep living brief (body) + nextAction on active tasks.");
        sb.AppendLine("After a material briefing (not [SILENT]), distill standing truths via briefing-distill → orbit_remember.");
        sb.AppendLine("If nothing actionable, reply with only [SILENT].");

        sb.AppendLine();
        sb.AppendLine($"Trigger: {triggerKind}");
        if (!string.IsNullOrWhiteSpace(triggerPayloadJson))
        {
            sb.AppendLine("Trigger payload:");
            sb.AppendLine(Truncate(triggerPayloadJson.Trim(), MaxPayloadChars));
        }

        if (!string.IsNullOrWhiteSpace(emailSnapshotJson))
        {
            sb.AppendLine();
            sb.AppendLine("Email snapshot (authoritative):");
            sb.AppendLine(Truncate(emailSnapshotJson.Trim(), MaxEmailSnapshotChars));
        }

        if (emailRelationMemory is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Learned email↔task relations (operator Accept/Reject):");
            var joined = string.Join(
                "\n",
                emailRelationMemory
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Take(12)
                    .Select(l => "- " + l.Trim()));
            sb.AppendLine(Truncate(joined, MaxRelationMemoryChars));
        }

        if (string.Equals(triggerKind, OperatorTriggers.EmailIngested, StringComparison.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine("Skill hint: attach to existing task when possible; set nextAction + body; one question if ambiguous.");
            sb.AppendLine("Do not propose weak token merges across unrelated vendor topics in the same property.");
            sb.AppendLine("If you learn a standing preference or project fact, orbit_remember it (briefing-distill).");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + "…(truncated)";
    }
}
