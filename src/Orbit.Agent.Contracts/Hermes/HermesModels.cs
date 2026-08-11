namespace Orbit.Agent.Contracts.Hermes;

public sealed class HermesHealthResult
{
    public required bool Ok { get; init; }

    public string? RawBody { get; init; }

    public int StatusCode { get; init; }
}

public sealed class HermesCapabilitiesResult
{
    public required bool Available { get; init; }

    public int StatusCode { get; init; }

    public string? RawBody { get; init; }

    /// <summary>True when the endpoint returned 404 (capability probe degraded).</summary>
    public bool NotFound { get; init; }
}

public sealed class HermesSession
{
    public required string SessionId { get; init; }

    public string? SessionKey { get; init; }

    /// <summary>True when the id came from Hermes /api/sessions; false when locally minted for headers.</summary>
    public bool PersistedRemotely { get; init; }
}

public sealed class HermesChatMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

public sealed class HermesChatRequest
{
    public required IReadOnlyList<HermesChatMessage> Messages { get; init; }

    public string? SessionId { get; init; }

    public string? SessionKey { get; init; }

    public string? Model { get; init; }

    public bool Stream { get; init; } = true;
}

public enum HermesChatDeltaKind
{
    Content,
    Progress,
    Done,
    Error,
}

public sealed class HermesChatDelta
{
    public required HermesChatDeltaKind Kind { get; init; }

    public string? Text { get; init; }

    /// <summary>Tool / skill name when <see cref="Kind"/> is Progress.</summary>
    public string? ToolName { get; init; }

    /// <summary>running | completed | thinking, when known.</summary>
    public string? Status { get; init; }
}

public sealed class HermesConnectionTestResult
{
    public required bool Success { get; init; }

    public string? HealthSummary { get; init; }

    public string? CapabilitiesSummary { get; init; }

    public string? Error { get; init; }

    public string? SecurityWarning { get; init; }
}

public sealed class HermesRunRequest
{
    public required string Prompt { get; init; }

    public string? SessionId { get; init; }

    public string? SessionKey { get; init; }

    public string? Model { get; init; }
}

public sealed class HermesRunResult
{
    public required string RunId { get; init; }

    public string? SessionId { get; init; }

    public string? Status { get; init; }

    public string? SummaryText { get; init; }

    public bool NotFound { get; init; }
}

public sealed class HermesOperatorChatResult
{
    public required bool Ok { get; init; }

    public string? Text { get; init; }

    public string? Error { get; init; }

    public string? SessionId { get; init; }
}
