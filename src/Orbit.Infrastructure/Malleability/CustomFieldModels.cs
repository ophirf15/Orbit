using System.Text.Json;

namespace Orbit.Infrastructure.Malleability;

public static class CustomFieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Date = "date";
    public const string Choice = "choice";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Text,
        Number,
        Bool,
        Date,
        Choice,
    };

    public static bool IsKnown(string? fieldType) =>
        !string.IsNullOrWhiteSpace(fieldType) && All.Contains(fieldType.Trim());
}

public static class CustomFieldEntityTypes
{
    public static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "project",
        "workstream",
        "task",
        "note",
        "person",
        "organization",
    };

    public static bool IsSupported(string? entityType) =>
        !string.IsNullOrWhiteSpace(entityType) && Supported.Contains(entityType.Trim());
}

public sealed class CustomFieldDefinition
{
    public required string Id { get; init; }

    public required string EntityType { get; init; }

    public required string Key { get; init; }

    public required string FieldType { get; init; }

    public string Label { get; init; } = string.Empty;

    public string? ValidationJson { get; init; }

    public string? DisplayJson { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }
}

public sealed class CustomFieldValue
{
    public required string Id { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string FieldKey { get; init; }

    public required string ValueJson { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }

    public JsonElement? ParsedValue
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(ValueJson);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

public sealed class CustomFieldValidation
{
    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    public bool Required { get; init; }
}
