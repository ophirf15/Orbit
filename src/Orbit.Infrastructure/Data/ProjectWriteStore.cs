using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

public sealed class ProjectWriteStore
{
    private static readonly Regex HexColor = new(
        "^#[0-9A-Fa-f]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SqliteConnectionFactory _factory;

    public ProjectWriteStore(SqliteConnectionFactory factory) => _factory = factory;

    public sealed class ProjectCreateResult
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public string? Summary { get; init; }

        public required string Status { get; init; }

        public required string CreatedAt { get; init; }
    }

    /// <summary>Creates an active project. Name defaults to "Untitled project" when blank.</summary>
    public ProjectCreateResult Create(string? name, string? summary = null, bool inOrbit = false)
    {
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Untitled project" : name.Trim();
        if (trimmedName.Length > 200)
        {
            throw new ArgumentException("Project name is too long.", nameof(name));
        }

        var trimmedSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        var id = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO projects (id, name, summary, status, created_at, updated_at, sort_order, in_orbit)
            VALUES ($id, $name, $summary, $status, $t, $t, 0, $orbit);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", trimmedName);
        cmd.Parameters.AddWithValue("$summary", (object?)trimmedSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", "active");
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$orbit", inOrbit ? 1 : 0);
        cmd.ExecuteNonQuery();

        return new ProjectCreateResult
        {
            Id = id,
            Name = trimmedName,
            Summary = trimmedSummary,
            Status = "active",
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Sets or clears the workbench stripe color. Pass null/empty to restore the theme default.
    /// </summary>
    public string? SetAccentColor(string projectId, string? accentColor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var normalized = NormalizeAccent(accentColor);

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE projects
            SET accent_color = $color,
                updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", projectId.Trim());
        cmd.Parameters.AddWithValue("$color", (object?)normalized ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }

        return normalized;
    }

    /// <summary>Updates project name and/or summary. Pass null to leave a field unchanged.</summary>
    public (string Name, string? Summary) Update(string projectId, string? name, string? summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (name is null && summary is null)
        {
            throw new ArgumentException("At least one of name or summary is required.");
        }

        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project name cannot be empty.", nameof(name));
        }

        using var connection = _factory.CreateConnection();
        using var find = connection.CreateCommand();
        find.CommandText =
            "SELECT name, summary FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        find.Parameters.AddWithValue("$id", projectId.Trim());
        using var reader = find.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }

        var currentName = reader.GetString(0);
        var currentSummary = reader.IsDBNull(1) ? null : reader.GetString(1);
        reader.Close();

        var newName = name?.Trim() ?? currentName;
        var newSummary = summary is null ? currentSummary : (string.IsNullOrWhiteSpace(summary) ? null : summary.Trim());

        using var upd = connection.CreateCommand();
        upd.CommandText =
            """
            UPDATE projects
            SET name = $name,
                summary = $summary,
                updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        upd.Parameters.AddWithValue("$id", projectId.Trim());
        upd.Parameters.AddWithValue("$name", newName);
        upd.Parameters.AddWithValue("$summary", (object?)newSummary ?? DBNull.Value);
        upd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        upd.ExecuteNonQuery();
        return (newName, newSummary);
    }

    public static string? NormalizeAccent(string? accentColor)
    {
        if (string.IsNullOrWhiteSpace(accentColor))
        {
            return null;
        }

        var value = accentColor.Trim();
        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (NamedAccents.TryGetValue(value, out var named))
        {
            return named;
        }

        if (!value.StartsWith('#') && value.Length == 6 && HexColor.IsMatch("#" + value))
        {
            value = "#" + value;
        }

        if (!HexColor.IsMatch(value))
        {
            throw new ArgumentException(
                "Accent color must be #RRGGBB or a preset name (blue, sky, teal, green, amber, rose, violet, slate, default).",
                nameof(accentColor));
        }

        return value.ToUpperInvariant();
    }

    /// <summary>Workbench stripe presets (same labels as the cell color menu).</summary>
    private static readonly Dictionary<string, string> NamedAccents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blue"] = "#0F6CBD",
        ["sky"] = "#0284C7",
        ["teal"] = "#0D9488",
        ["green"] = "#16A34A",
        ["amber"] = "#D97706",
        ["rose"] = "#E11D48",
        ["violet"] = "#7C3AED",
        ["slate"] = "#64748B",
    };
}
