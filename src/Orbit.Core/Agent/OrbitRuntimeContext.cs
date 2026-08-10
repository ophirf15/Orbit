using System.Text;
using System.Text.Json;

namespace Orbit.Core.Agent;

public sealed class OrbitRuntimeContext
{
    public string Route { get; init; } = "unknown";

    public string? ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public string? TaskId { get; init; }

    public string? SelectedEntityType { get; init; }

    public string? SelectedEntityId { get; init; }

    public string LocalDataRoot { get; init; } = string.Empty;

    public string CoreHostUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> WorkbenchProjectNames { get; init; } = [];

    public IReadOnlyList<string> CapabilityHints { get; init; } =
    [
        "orbit_get_related_context",
        "orbit_get_workbench",
        "orbit_search",
        "orbit_answer_with_evidence",
        "orbit_get_project",
        "orbit_update_project",
        "orbit_get_contact",
        "orbit_create_task",
        "orbit_update_task",
        "orbit_create_note",
        "orbit_archive_entity",
        "orbit_update_contact",
    ];

    public string ToSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Hermes operating inside Orbit, a Windows operational workbench.");
        sb.AppendLine("Use Orbit Core tools (via MCP/HTTP) for app data — never invent project state.");
        sb.AppendLine("Keep answers concise unless asked for detail.");
        sb.AppendLine();
        sb.AppendLine("Live Orbit runtime context (JSON):");
        sb.AppendLine(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
        sb.AppendLine("If the user asks what you can see, summarize this context and named Orbit tools.");
        return sb.ToString();
    }
}
