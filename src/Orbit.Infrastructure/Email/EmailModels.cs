namespace Orbit.Infrastructure.Email;

public sealed class ParsedEmailAttachment
{
    public required string FileName { get; init; }

    public required byte[] Data { get; init; }

    public string? ContentType { get; init; }
}

public sealed class ParsedEmailParticipant
{
    public required string Role { get; init; }

    public required string Address { get; init; }

    public string? DisplayName { get; init; }
}

public sealed class ParsedEmailMessage
{
    public string? Subject { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    public string? InternetMessageId { get; init; }

    public string? ConversationId { get; init; }

    public string? BodyText { get; init; }

    public string? BodyHtml { get; init; }

    public IReadOnlyList<ParsedEmailParticipant> Participants { get; init; } = [];

    public IReadOnlyList<ParsedEmailAttachment> Attachments { get; init; } = [];
}

public sealed class EmailAttachmentRecord
{
    public required string FileName { get; init; }

    public required string Path { get; init; }

    public long SizeBytes { get; init; }
}

public sealed class EmailParticipantRecord
{
    public required string Id { get; init; }

    public required string Role { get; init; }

    public required string Address { get; init; }

    public string? DisplayName { get; init; }
}

public sealed record EmailArtifactRecord
{
    public required string Id { get; init; }

    public string? Subject { get; init; }

    public string? SentAt { get; init; }

    public string? ReceivedAt { get; init; }

    public string? InternetMessageId { get; init; }

    public string? ConversationId { get; init; }

    public string? BodyPreview { get; init; }

    public string? RawPath { get; init; }

    public string? BodyTextPath { get; init; }

    public string? BodyHtmlPath { get; init; }

    public string? ContentHash { get; init; }

    public IReadOnlyList<EmailParticipantRecord> Participants { get; init; } = [];

    public IReadOnlyList<string> ProjectIds { get; init; } = [];

    public IReadOnlyList<EmailAttachmentRecord> Attachments { get; init; } = [];

    public bool WasExisting { get; init; }

    public IReadOnlyList<string> EnrichedPersonIds { get; init; } = [];

    public int EnrichmentSuggestionCount { get; init; }

    public int ClaimExtractionCount { get; init; }

    public string? ClaimSuggestionId { get; init; }
}
