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

    public static string Build(string triggerKind, string? triggerPayloadJson, string? emailSnapshotJson = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Orbit Core wake (slim). Identity and standing rules live in SOUL.md / Hermes memory.");
        sb.AppendLine("Use Orbit MCP tools (mcp_orbit_*) for truth. Prefer skills: duty-scan, pulse-refresh, channel-to-orbit.");
        sb.AppendLine("Mutate when the match is clear; keep living brief (body) + nextAction on active tasks.");
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

        if (string.Equals(triggerKind, OperatorTriggers.EmailIngested, StringComparison.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine("Skill hint: attach to existing task when possible; set nextAction + body; one question if ambiguous.");
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
