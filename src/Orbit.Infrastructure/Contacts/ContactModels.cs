namespace Orbit.Infrastructure.Contacts;

public sealed class ContactListItem
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Title { get; init; }

    public string? OrganizationName { get; init; }

    public string? PrimaryEmail { get; init; }

    public string? PrimaryPhone { get; init; }

    /// <summary>company | client | vendor | null (pending).</summary>
    public string? Category { get; init; }

    /// <summary>active | flagged_resident | excluded_resident.</summary>
    public required string Disposition { get; init; }
}

public sealed class ContactMethodItem
{
    public required string Id { get; init; }

    public required string MethodType { get; init; }

    public required string Value { get; init; }

    public string? Label { get; init; }

    public bool IsPrimary { get; init; }
}

public sealed class ContactProvenanceItem
{
    public required string Id { get; init; }

    public required string Field { get; init; }

    public required string Value { get; init; }

    public string? SourceEmailId { get; init; }

    public required string SourceKind { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class ContactProjectItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }
}

public sealed class ContactEmailSnippet
{
    public required string Id { get; init; }

    public string? Subject { get; init; }

    public string? SentAt { get; init; }

    public string? BodyPreview { get; init; }

    public string? Role { get; init; }
}

public sealed record ContactDetail
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? FamilyName { get; init; }

    public string? Notes { get; init; }

    public string? Title { get; init; }

    public string? OrganizationId { get; init; }

    public string? OrganizationName { get; init; }

    public string? Category { get; init; }

    public required string Disposition { get; init; }

    public string? ReportsToPersonId { get; init; }

    public string? ReportsToDisplayName { get; init; }

    public IReadOnlyList<ContactMethodItem> Methods { get; init; } = [];

    public IReadOnlyList<ContactProjectItem> Projects { get; init; } = [];

    public IReadOnlyList<ContactEmailSnippet> RecentEmails { get; init; } = [];

    public IReadOnlyList<ContactProvenanceItem> Provenance { get; init; } = [];
}

public sealed class OrganizationListItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Kind { get; init; }

    public string? Domain { get; init; }
}

public sealed class ContactPatch
{
    public string? Mobile { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Title { get; init; }

    public string? DisplayName { get; init; }

    public string? OrganizationId { get; init; }

    /// <summary>company | client | vendor | empty string clears to pending.</summary>
    public string? Category { get; init; }

    /// <summary>active | flagged_resident | excluded_resident.</summary>
    public string? Disposition { get; init; }

    /// <summary>Resolve or create org by display name when organizationId is omitted.</summary>
    public string? OrganizationName { get; init; }

    public string? ReportsToPersonId { get; init; }

    /// <summary>When true with archive flow, prefer excluded_resident disposition.</summary>
    public bool? ExcludeAsResident { get; init; }
}

public sealed class UpdateContactRequest
{
    public ContactPatch? Patch { get; init; }

    public string? Provenance { get; init; }

    public string? RequestedBy { get; init; }
}

public sealed class ContactEnrichmentResult
{
    public required string EmailId { get; init; }

    public IReadOnlyList<string> PersonIds { get; init; } = [];

    public int SuggestionCount { get; init; }
}

public static class ContactMethodTypes
{
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Mobile = "mobile";
    public const string Domain = "domain";
}

public static class ContactSourceKinds
{
    public const string EmailParticipant = "email_participant";
    public const string SignatureHeuristic = "signature_heuristic";
    public const string UserUpdate = "user_update";
    public const string DomainInference = "domain_inference";
    public const string HermesEnrich = "hermes_enrich";
}

public static class ContactSuggestionTypes
{
    public const string ContactMerge = "contact_merge";
}
