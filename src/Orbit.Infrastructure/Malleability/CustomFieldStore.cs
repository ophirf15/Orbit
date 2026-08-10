using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Malleability;

public sealed class CustomFieldStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteConnectionFactory _factory;

    public CustomFieldStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public IReadOnlyList<CustomFieldDefinition> ListDefinitions(string? entityType = null)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(entityType))
        {
            cmd.CommandText =
                """
                SELECT id, entity_type, field_key, field_type, label, validation_json, display_json, created_at, updated_at
                FROM custom_fields
                WHERE archived_at IS NULL
                ORDER BY entity_type, field_key;
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT id, entity_type, field_key, field_type, label, validation_json, display_json, created_at, updated_at
                FROM custom_fields
                WHERE archived_at IS NULL AND lower(entity_type) = lower($entity)
                ORDER BY field_key;
                """;
            cmd.Parameters.AddWithValue("$entity", entityType.Trim());
        }

        var list = new List<CustomFieldDefinition>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadDefinition(reader));
        }

        return list;
    }

    public CustomFieldDefinition AddField(
        string entityType,
        string key,
        string fieldType,
        string? validationJson = null,
        string? displayJson = null,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        if (!CustomFieldEntityTypes.IsSupported(entityType))
        {
            throw new ArgumentException(
                $"Unsupported entity_type '{entityType}'. Allowed: {string.Join(", ", CustomFieldEntityTypes.Supported)}.",
                nameof(entityType));
        }

        var normalizedKey = NormalizeKey(key);
        var normalizedType = fieldType.Trim().ToLowerInvariant();
        if (!CustomFieldTypes.IsKnown(normalizedType))
        {
            throw new ArgumentException(
                $"Unsupported field_type '{fieldType}'. Allowed: text, number, bool, date, choice.",
                nameof(fieldType));
        }

        ValidateValidationJson(normalizedType, validationJson);
        ValidateOptionalJson(displayJson, nameof(displayJson));

        var label = ExtractLabel(displayJson) ?? normalizedKey;
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        var entity = entityType.Trim().ToLowerInvariant();

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO custom_fields (
                  id, dynamic_schema_id, entity_type, field_key, field_type, label,
                  validation_json, display_json, created_at, updated_at, archived_at)
                VALUES (
                  $id, NULL, $entity, $key, $type, $label,
                  $validation, $display, $t, $t, NULL);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$entity", entity);
            insert.Parameters.AddWithValue("$key", normalizedKey);
            insert.Parameters.AddWithValue("$type", normalizedType);
            insert.Parameters.AddWithValue("$label", label);
            insert.Parameters.AddWithValue("$validation", (object?)NullIfWhite(validationJson) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$display", (object?)NullIfWhite(displayJson) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$t", now);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new ArgumentException(
                    $"Custom field '{normalizedKey}' already exists on entity_type '{entityType}'.",
                    nameof(key));
            }
        }

        WriteAudit(
            connection,
            tx,
            "custom_field.added",
            actor ?? "agent",
            new
            {
                fieldId = id,
                entityType = entity,
                key = normalizedKey,
                fieldType = normalizedType,
            },
            provenance);

        tx.Commit();

        return new CustomFieldDefinition
        {
            Id = id,
            EntityType = entity,
            Key = normalizedKey,
            FieldType = normalizedType,
            Label = label,
            ValidationJson = NullIfWhite(validationJson),
            DisplayJson = NullIfWhite(displayJson),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Updates the human-facing label for a field definition (key stays stable).</summary>
    public CustomFieldDefinition UpdateLabel(string entityType, string key, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var trimmedLabel = string.IsNullOrWhiteSpace(label) ? NormalizeKey(key) : label.Trim();
        if (trimmedLabel.Length > 120)
        {
            throw new ArgumentException("Label is too long.", nameof(label));
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE custom_fields
            SET label = $label, updated_at = $t
            WHERE archived_at IS NULL
              AND lower(entity_type) = lower($entity)
              AND field_key = $key;
            """;
        cmd.Parameters.AddWithValue("$label", trimmedLabel);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$entity", entityType.Trim());
        cmd.Parameters.AddWithValue("$key", NormalizeKey(key));
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new ArgumentException(
                $"No custom field '{key}' on entity_type '{entityType}'.",
                nameof(key));
        }

        return GetDefinition(connection, entityType, NormalizeKey(key))
               ?? throw new InvalidOperationException("Field disappeared after label update.");
    }

    public CustomFieldValue SetValue(
        string entityType,
        string entityId,
        string fieldKey,
        JsonElement value,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("entityId is required.", nameof(entityId));
        }

        var normalizedKey = NormalizeKey(fieldKey);
        using var connection = _factory.CreateConnection();

        var definition = GetDefinition(connection, entityType, normalizedKey)
            ?? throw new ArgumentException(
                $"No custom field '{normalizedKey}' on entity_type '{entityType}'.",
                nameof(fieldKey));

        var valueJson = ValidateAndSerializeValue(definition, value);
        var (valueText, valueNumber) = SplitTypedColumns(definition.FieldType, valueJson);
        var now = DateTime.UtcNow.ToString("O");
        var entityIdTrim = entityId.Trim();

        using var tx = connection.BeginTransaction();

        string? existingId = null;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                """
                SELECT id FROM custom_field_values
                WHERE entity_type = $entity AND entity_id = $entityId AND custom_field_id = $fieldId;
                """;
            find.Parameters.AddWithValue("$entity", definition.EntityType);
            find.Parameters.AddWithValue("$entityId", entityIdTrim);
            find.Parameters.AddWithValue("$fieldId", definition.Id);
            existingId = find.ExecuteScalar() as string;
        }

        string storedId;
        string createdAt;
        if (existingId is null)
        {
            storedId = Guid.NewGuid().ToString("D");
            createdAt = now;
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO custom_field_values (
                  id, custom_field_id, entity_type, entity_id,
                  value_text, value_number, value_json, created_at, updated_at)
                VALUES (
                  $id, $fieldId, $entity, $entityId,
                  $text, $number, $json, $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", storedId);
            insert.Parameters.AddWithValue("$fieldId", definition.Id);
            insert.Parameters.AddWithValue("$entity", definition.EntityType);
            insert.Parameters.AddWithValue("$entityId", entityIdTrim);
            insert.Parameters.AddWithValue("$text", (object?)valueText ?? DBNull.Value);
            insert.Parameters.AddWithValue("$number", (object?)valueNumber ?? DBNull.Value);
            insert.Parameters.AddWithValue("$json", valueJson);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }
        else
        {
            storedId = existingId;
            createdAt = now;
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE custom_field_values
                SET value_text = $text, value_number = $number, value_json = $json, updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$text", (object?)valueText ?? DBNull.Value);
            update.Parameters.AddWithValue("$number", (object?)valueNumber ?? DBNull.Value);
            update.Parameters.AddWithValue("$json", valueJson);
            update.Parameters.AddWithValue("$t", now);
            update.Parameters.AddWithValue("$id", storedId);
            update.ExecuteNonQuery();

            using var createdCmd = connection.CreateCommand();
            createdCmd.Transaction = tx;
            createdCmd.CommandText = "SELECT created_at FROM custom_field_values WHERE id = $id;";
            createdCmd.Parameters.AddWithValue("$id", storedId);
            createdAt = createdCmd.ExecuteScalar() as string ?? now;
        }

        WriteAudit(
            connection,
            tx,
            "custom_field.value_set",
            actor ?? "agent",
            new
            {
                fieldId = definition.Id,
                entityType = definition.EntityType,
                entityId = entityIdTrim,
                fieldKey = normalizedKey,
                valueJson,
            },
            provenance);

        tx.Commit();

        return new CustomFieldValue
        {
            Id = storedId,
            EntityType = definition.EntityType,
            EntityId = entityIdTrim,
            FieldKey = normalizedKey,
            ValueJson = valueJson,
            CreatedAt = createdAt,
            UpdatedAt = now,
        };
    }

    public IReadOnlyList<CustomFieldValue> ListValues(string entityType, string entityId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT v.id, v.entity_type, v.entity_id, f.field_key, v.value_json, v.created_at, v.updated_at
            FROM custom_field_values v
            INNER JOIN custom_fields f ON f.id = v.custom_field_id
            WHERE lower(v.entity_type) = lower($entity) AND v.entity_id = $entityId
            ORDER BY f.field_key;
            """;
        cmd.Parameters.AddWithValue("$entity", entityType.Trim());
        cmd.Parameters.AddWithValue("$entityId", entityId.Trim());

        var list = new List<CustomFieldValue>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CustomFieldValue
            {
                Id = reader.GetString(0),
                EntityType = reader.GetString(1),
                EntityId = reader.GetString(2),
                FieldKey = reader.GetString(3),
                ValueJson = reader.IsDBNull(4) ? "null" : reader.GetString(4),
                CreatedAt = reader.GetString(5),
                UpdatedAt = reader.GetString(6),
            });
        }

        return list;
    }

    private static CustomFieldDefinition? GetDefinition(SqliteConnection connection, string entityType, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, entity_type, field_key, field_type, label, validation_json, display_json, created_at, updated_at
            FROM custom_fields
            WHERE archived_at IS NULL AND lower(entity_type) = lower($entity) AND field_key = $key;
            """;
        cmd.Parameters.AddWithValue("$entity", entityType.Trim());
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDefinition(reader) : null;
    }

    private static CustomFieldDefinition ReadDefinition(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            EntityType = reader.GetString(1),
            Key = reader.GetString(2),
            FieldType = reader.GetString(3),
            Label = reader.IsDBNull(4) ? reader.GetString(2) : reader.GetString(4),
            ValidationJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            DisplayJson = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = reader.GetString(7),
            UpdatedAt = reader.GetString(8),
        };

    private static (string? Text, double? Number) SplitTypedColumns(string fieldType, string valueJson)
    {
        using var doc = JsonDocument.Parse(valueJson);
        var el = doc.RootElement;
        return fieldType.ToLowerInvariant() switch
        {
            CustomFieldTypes.Number when el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n)
                => (n.ToString(CultureInfo.InvariantCulture), n),
            CustomFieldTypes.Bool => (el.GetBoolean() ? "true" : "false", null),
            CustomFieldTypes.Text or CustomFieldTypes.Date or CustomFieldTypes.Choice
                when el.ValueKind == JsonValueKind.String
                => (el.GetString(), null),
            _ => (valueJson, null),
        };
    }

    private static string ValidateAndSerializeValue(CustomFieldDefinition definition, JsonElement value)
    {
        var rules = ParseValidation(definition.ValidationJson);
        return definition.FieldType.ToLowerInvariant() switch
        {
            CustomFieldTypes.Text => ValidateText(value, rules),
            CustomFieldTypes.Number => ValidateNumber(value, rules),
            CustomFieldTypes.Bool => ValidateBool(value),
            CustomFieldTypes.Date => ValidateDate(value),
            CustomFieldTypes.Choice => ValidateChoice(value, rules),
            _ => throw new ArgumentException($"Unsupported field type '{definition.FieldType}'."),
        };
    }

    private static string ValidateText(JsonElement value, CustomFieldValidation rules)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => throw new ArgumentException("text fields require a JSON string value."),
        };

        if (rules.Required && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Value is required.");
        }

        if (rules.MinLength is int min && text.Length < min)
        {
            throw new ArgumentException($"Value length must be at least {min}.");
        }

        if (rules.MaxLength is int max && text.Length > max)
        {
            throw new ArgumentException($"Value length must be at most {max}.");
        }

        return JsonSerializer.Serialize(text, JsonOptions);
    }

    private static string ValidateNumber(JsonElement value, CustomFieldValidation rules)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
        {
            // ok
        }
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            // ok
        }
        else if (value.ValueKind == JsonValueKind.Null && !rules.Required)
        {
            return "null";
        }
        else
        {
            throw new ArgumentException("number fields require a numeric JSON value.");
        }

        if (rules.Min is double min && number < min)
        {
            throw new ArgumentException($"Value must be >= {min}.");
        }

        if (rules.Max is double max && number > max)
        {
            throw new ArgumentException($"Value must be <= {max}.");
        }

        return JsonSerializer.Serialize(number, JsonOptions);
    }

    private static string ValidateBool(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean() ? "true" : "false";
        }

        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed ? "true" : "false";
        }

        throw new ArgumentException("bool fields require a JSON boolean value.");
    }

    private static string ValidateDate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("date fields require an ISO-8601 date/datetime string.");
        }

        var raw = value.GetString() ?? string.Empty;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            throw new ArgumentException("date value must be a parseable ISO-8601 date/datetime.");
        }

        return JsonSerializer.Serialize(raw, JsonOptions);
    }

    private static string ValidateChoice(JsonElement value, CustomFieldValidation rules)
    {
        if (rules.Choices is null || rules.Choices.Count == 0)
        {
            throw new ArgumentException("choice fields require validation.choices.");
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw new ArgumentException("choice fields require a JSON string value."),
        };

        if (!rules.Choices.Contains(text, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Value must be one of: {string.Join(", ", rules.Choices)}.");
        }

        return JsonSerializer.Serialize(text, JsonOptions);
    }

    private static void ValidateValidationJson(string fieldType, string? validationJson)
    {
        if (string.IsNullOrWhiteSpace(validationJson))
        {
            if (string.Equals(fieldType, CustomFieldTypes.Choice, StringComparison.Ordinal))
            {
                throw new ArgumentException("choice fields require validation_json with choices.", nameof(validationJson));
            }

            return;
        }

        var rules = ParseValidation(validationJson);
        if (string.Equals(fieldType, CustomFieldTypes.Choice, StringComparison.Ordinal)
            && (rules.Choices is null || rules.Choices.Count == 0))
        {
            throw new ArgumentException("choice fields require validation.choices.", nameof(validationJson));
        }
    }

    private static CustomFieldValidation ParseValidation(string? validationJson)
    {
        if (string.IsNullOrWhiteSpace(validationJson))
        {
            return new CustomFieldValidation();
        }

        try
        {
            return JsonSerializer.Deserialize<CustomFieldValidation>(validationJson, JsonOptions)
                   ?? new CustomFieldValidation();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("validation_json must be valid JSON object.", ex);
        }
    }

    private static void ValidateOptionalJson(string? json, string paramName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{paramName} must be valid JSON.", paramName, ex);
        }
    }

    private static string? ExtractLabel(string? displayJson)
    {
        if (string.IsNullOrWhiteSpace(displayJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(displayJson);
            if (doc.RootElement.TryGetProperty("label", out var label)
                && label.ValueKind == JsonValueKind.String)
            {
                return label.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("key is required.", nameof(key));
        }

        var trimmed = key.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentException("key must be 64 characters or fewer.", nameof(key));
        }

        foreach (var ch in trimmed)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            {
                throw new ArgumentException("key may only contain letters, digits, '_' or '-'.", nameof(key));
            }
        }

        return trimmed;
    }

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventType,
        string actor,
        object detail,
        MutationProvenance? provenance)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO audit_events (id, event_type, entity_type, entity_id, actor, detail_json, created_at)
            VALUES ($id, $eventType, $entityType, $entityId, $actor, $detail, $t);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$eventType", eventType);
        cmd.Parameters.AddWithValue("$entityType", "custom_field");
        cmd.Parameters.AddWithValue("$entityId", DBNull.Value);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$detail", AuditDetailJson.Serialize(detail, provenance));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
