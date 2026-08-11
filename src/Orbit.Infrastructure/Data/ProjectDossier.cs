using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Infrastructure.Data;

/// <summary>
/// Structured project context Hermes and the operator keep current.
/// Free-text <c>projects.summary</c> stays separate; this blob holds call-ready fields.
/// </summary>
public sealed class ProjectDossier
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Property / site address.</summary>
    public string? Address { get; set; }

    /// <summary>Owner or client name (free text; not a product enum).</summary>
    public string? OwnerClient { get; set; }

    public string? Phase { get; set; }

    public string? Portfolio { get; set; }

    public List<ProjectDossierContact> CriticalContacts { get; set; } = [];

    /// <summary>Operator-facing folder path hint (home folder may also live in project_folders).</summary>
    public string? LinkedFolder { get; set; }

    /// <summary>Mailbox / account labels the operator associates with this project.</summary>
    public List<string> MailboxSources { get; set; } = [];

    /// <summary>Calendar source ids or display names the operator associates with this project.</summary>
    public List<string> CalendarSources { get; set; } = [];

    public List<string> CurrentPriorities { get; set; } = [];

    public bool IsStructurallyEmpty =>
        string.IsNullOrWhiteSpace(Address)
        && string.IsNullOrWhiteSpace(OwnerClient)
        && string.IsNullOrWhiteSpace(Phase)
        && string.IsNullOrWhiteSpace(Portfolio)
        && string.IsNullOrWhiteSpace(LinkedFolder)
        && CriticalContacts.Count == 0
        && MailboxSources.Count == 0
        && CalendarSources.Count == 0
        && CurrentPriorities.Count == 0;

    public static ProjectDossier Empty() => new() { Version = CurrentVersion };

    public static ProjectDossier Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ProjectDossier>(json, JsonOptions);
            if (parsed is null)
            {
                return Empty();
            }

            parsed.Version = parsed.Version <= 0 ? CurrentVersion : parsed.Version;
            parsed.CriticalContacts ??= [];
            parsed.MailboxSources ??= [];
            parsed.CalendarSources ??= [];
            parsed.CurrentPriorities ??= [];
            return parsed;
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    public static bool IsJsonStructurallyEmpty(string? json) => Parse(json).IsStructurallyEmpty;

    public string ToJson()
    {
        var copy = Normalize();
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    public ProjectDossier Normalize()
    {
        Version = Version <= 0 ? CurrentVersion : Version;
        Address = TrimOrNull(Address);
        OwnerClient = TrimOrNull(OwnerClient);
        Phase = TrimOrNull(Phase);
        Portfolio = TrimOrNull(Portfolio);
        LinkedFolder = TrimOrNull(LinkedFolder);
        CriticalContacts = (CriticalContacts ?? [])
            .Select(c => new ProjectDossierContact
            {
                Name = TrimOrNull(c.Name) ?? string.Empty,
                Role = TrimOrNull(c.Role),
                PersonId = TrimOrNull(c.PersonId),
                Contact = TrimOrNull(c.Contact),
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.PersonId))
            .Take(40)
            .ToList();
        MailboxSources = NormalizeStringList(MailboxSources);
        CalendarSources = NormalizeStringList(CalendarSources);
        CurrentPriorities = NormalizeStringList(CurrentPriorities);
        return this;
    }

    /// <summary>Merge non-null patch fields into a base dossier (null patch fields leave base unchanged).</summary>
    public static ProjectDossier Merge(ProjectDossier? current, ProjectDossierPatch? patch)
    {
        var result = current is null ? Empty() : Parse(current.ToJson());
        if (patch is null)
        {
            return result.Normalize();
        }

        if (patch.Address is not null)
        {
            result.Address = patch.Address;
        }

        if (patch.OwnerClient is not null)
        {
            result.OwnerClient = patch.OwnerClient;
        }

        if (patch.Phase is not null)
        {
            result.Phase = patch.Phase;
        }

        if (patch.Portfolio is not null)
        {
            result.Portfolio = patch.Portfolio;
        }

        if (patch.LinkedFolder is not null)
        {
            result.LinkedFolder = patch.LinkedFolder;
        }

        if (patch.CriticalContacts is not null)
        {
            result.CriticalContacts = patch.CriticalContacts;
        }

        if (patch.MailboxSources is not null)
        {
            result.MailboxSources = patch.MailboxSources;
        }

        if (patch.CalendarSources is not null)
        {
            result.CalendarSources = patch.CalendarSources;
        }

        if (patch.CurrentPriorities is not null)
        {
            result.CurrentPriorities = patch.CurrentPriorities;
        }

        return result.Normalize();
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProjectDossierContact
{
    public string Name { get; set; } = string.Empty;

    public string? Role { get; set; }

    public string? PersonId { get; set; }

    /// <summary>Phone / email free text.</summary>
    public string? Contact { get; set; }
}

/// <summary>Partial update payload for Host/MCP (null = leave unchanged; empty string clears scalars).</summary>
public sealed class ProjectDossierPatch
{
    public string? Address { get; set; }

    public string? OwnerClient { get; set; }

    public string? Phase { get; set; }

    public string? Portfolio { get; set; }

    public string? LinkedFolder { get; set; }

    public List<ProjectDossierContact>? CriticalContacts { get; set; }

    public List<string>? MailboxSources { get; set; }

    public List<string>? CalendarSources { get; set; }

    public List<string>? CurrentPriorities { get; set; }

    public bool HasAnyField =>
        Address is not null
        || OwnerClient is not null
        || Phase is not null
        || Portfolio is not null
        || LinkedFolder is not null
        || CriticalContacts is not null
        || MailboxSources is not null
        || CalendarSources is not null
        || CurrentPriorities is not null;
}
