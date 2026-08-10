using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Infrastructure.Data;

/// <summary>
/// Platform/session provenance for Hermes-driven mutations (desktop or telegram).
/// Persisted under <c>audit_events.detail_json.provenance</c> (or <c>platformProvenance</c>
/// when <c>provenance</c> is already a contact fact string).
/// </summary>
public sealed class MutationProvenance
{
    public string? Actor { get; init; }

    public string? Channel { get; init; }

    public string? HermesSessionId { get; init; }

    public string? ExternalUserId { get; init; }

    /// <summary>Optional Telegram user id; stored as <see cref="ExternalUserId"/> when that is empty.</summary>
    public string? TelegramUserId { get; init; }

    public bool HasValues =>
        !string.IsNullOrWhiteSpace(Actor)
        || !string.IsNullOrWhiteSpace(Channel)
        || !string.IsNullOrWhiteSpace(HermesSessionId)
        || !string.IsNullOrWhiteSpace(ExternalUserId)
        || !string.IsNullOrWhiteSpace(TelegramUserId);

    public string? ResolveExternalUserId() =>
        string.IsNullOrWhiteSpace(ExternalUserId) ? NullIfWhite(TelegramUserId) : ExternalUserId.Trim();

    public string? ResolveActor(string? fallback) =>
        string.IsNullOrWhiteSpace(Actor) ? fallback : Actor.Trim();

    public MutationProvenance Normalized() =>
        new()
        {
            Actor = NullIfWhite(Actor),
            Channel = NullIfWhite(Channel)?.ToLowerInvariant(),
            HermesSessionId = NullIfWhite(HermesSessionId),
            ExternalUserId = ResolveExternalUserId(),
            TelegramUserId = NullIfWhite(TelegramUserId),
        };

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class AuditDetailJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object detail, MutationProvenance? provenance)
    {
        var node = JsonSerializer.SerializeToNode(detail, Options)?.AsObject()
            ?? new JsonObject();

        if (provenance is not null && provenance.HasValues)
        {
            var normalized = provenance.Normalized();
            var provNode = JsonSerializer.SerializeToNode(
                new
                {
                    actor = normalized.Actor,
                    channel = normalized.Channel,
                    hermesSessionId = normalized.HermesSessionId,
                    externalUserId = normalized.ExternalUserId,
                },
                Options);

            if (node.TryGetPropertyValue("provenance", out var existing)
                && existing is not null
                && existing.GetValueKind() != JsonValueKind.Object)
            {
                node["platformProvenance"] = provNode;
            }
            else
            {
                node["provenance"] = provNode;
            }
        }

        return node.ToJsonString(Options);
    }
}
