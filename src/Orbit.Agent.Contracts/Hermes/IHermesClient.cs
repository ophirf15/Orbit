namespace Orbit.Agent.Contracts.Hermes;

/// <summary>
/// Typed Hermes API client. Prefer capability discovery; degrade when optional routes 404.
/// </summary>
public interface IHermesClient : IDisposable
{
    Uri BaseAddress { get; }

    Task<HermesHealthResult> HealthAsync(CancellationToken cancellationToken = default);

    Task<HermesCapabilitiesResult> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or resumes a Hermes session via /api/sessions when available;
    /// otherwise mints a local session id for X-Hermes-Session-Id chat headers.
    /// </summary>
    Task<HermesSession> EnsureSessionAsync(
        string? existingSessionId = null,
        string? existingSessionKey = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<HermesChatDelta> StreamChatAsync(
        HermesChatRequest request,
        CancellationToken cancellationToken = default);

    Task<HermesConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a Hermes structured run when <c>/v1/runs</c> is available.
    /// Returns null when the endpoint is missing (404) so callers can fall back to chat.
    /// </summary>
    Task<HermesRunResult?> TryStartRunAsync(
        HermesRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-streaming chat completion that concatenates content deltas (operator briefings).
    /// </summary>
    Task<HermesOperatorChatResult> CompleteOperatorChatAsync(
        HermesChatRequest request,
        CancellationToken cancellationToken = default);
}
