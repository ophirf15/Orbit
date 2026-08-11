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

        public string? Code { get; init; }

        public required string Status { get; init; }

        public required string CreatedAt { get; init; }
    }

    public sealed class ProjectAliasRecord
    {
        public required string Id { get; init; }

        public required string ProjectId { get; init; }

        public required string Alias { get; init; }

        public required string NormalizedAlias { get; init; }

        public required string CreatedAt { get; init; }
    }

    /// <summary>Creates an active project. Name defaults to "Untitled project" when blank.</summary>
    public ProjectCreateResult Create(string? name, string? summary = null, bool inOrbit = false, string? code = null)
    {
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Untitled project" : name.Trim();
        if (trimmedName.Length > 200)
        {
            throw new ArgumentException("Project name is too long.", nameof(name));
        }

        var trimmedSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        var trimmedCode = NormalizeCode(code);
        var id = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO projects (id, name, code, summary, status, created_at, updated_at, sort_order, in_orbit)
            VALUES ($id, $name, $code, $summary, $status, $t, $t, 0, $orbit);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", trimmedName);
        cmd.Parameters.AddWithValue("$code", (object?)trimmedCode ?? DBNull.Value);
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
            Code = trimmedCode,
            Status = "active",
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Near-duplicate candidates for a proposed project name (name / code / alias).
    /// Empty when safe to create.
    /// </summary>
    public IReadOnlyList<ProjectMatchCandidate> FindCreateConflicts(string? name, double threshold = ProjectIdentityMatcher.NearDupeThreshold)
    {
        return ProjectIdentityMatcher.FindNearDuplicates(_factory, name)
            .Where(c => c.Score >= threshold)
            .ToList();
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

    /// <summary>Updates project name, summary, and/or code. Pass null to leave a field unchanged.</summary>
    public (string Name, string? Summary, string? Code) Update(
        string projectId,
        string? name,
        string? summary,
        string? code = null,
        bool touchCode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (name is null && summary is null && !touchCode)
        {
            throw new ArgumentException("At least one of name, summary, or code is required.");
        }

        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project name cannot be empty.", nameof(name));
        }

        using var connection = _factory.CreateConnection();
        using var find = connection.CreateCommand();
        find.CommandText =
            "SELECT name, summary, code FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        find.Parameters.AddWithValue("$id", projectId.Trim());
        using var reader = find.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }

        var currentName = reader.GetString(0);
        var currentSummary = reader.IsDBNull(1) ? null : reader.GetString(1);
        var currentCode = reader.IsDBNull(2) ? null : reader.GetString(2);
        reader.Close();

        var newName = name?.Trim() ?? currentName;
        var newSummary = summary is null ? currentSummary : (string.IsNullOrWhiteSpace(summary) ? null : summary.Trim());
        var newCode = touchCode ? NormalizeCode(code) : currentCode;

        using var upd = connection.CreateCommand();
        upd.CommandText =
            """
            UPDATE projects
            SET name = $name,
                summary = $summary,
                code = $code,
                updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        upd.Parameters.AddWithValue("$id", projectId.Trim());
        upd.Parameters.AddWithValue("$name", newName);
        upd.Parameters.AddWithValue("$summary", (object?)newSummary ?? DBNull.Value);
        upd.Parameters.AddWithValue("$code", (object?)newCode ?? DBNull.Value);
        upd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        upd.ExecuteNonQuery();
        return (newName, newSummary, newCode);
    }

    public ProjectDossier GetDossier(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT dossier_json FROM projects
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", projectId.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }

        return ProjectDossier.Parse(reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    public ProjectDossier UpdateDossier(string projectId, ProjectDossierPatch patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.HasAnyField)
        {
            throw new ArgumentException("Provide at least one dossier field.", nameof(patch));
        }

        var current = GetDossier(projectId);
        var merged = ProjectDossier.Merge(current, patch);
        PersistDossier(projectId, merged);
        return merged;
    }

    public ProjectDossier ReplaceDossier(string projectId, ProjectDossier dossier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(dossier);
        EnsureProjectExists(projectId);
        var normalized = dossier.Normalize();
        PersistDossier(projectId, normalized);
        return normalized;
    }

    private void PersistDossier(string projectId, ProjectDossier dossier)
    {
        var json = dossier.IsStructurallyEmpty ? null : dossier.ToJson();
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE projects
            SET dossier_json = $json,
                updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", projectId.Trim());
        cmd.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }
    }

    public IReadOnlyList<ProjectAliasRecord> ListAliases(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        EnsureProjectExists(projectId);

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, alias, normalized_alias, created_at
            FROM project_aliases
            WHERE project_id = $p
            ORDER BY alias COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$p", projectId.Trim());

        var list = new List<ProjectAliasRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadAlias(reader));
        }

        return list;
    }

    public ProjectAliasRecord AddAlias(string projectId, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var trimmed = alias.Trim();
        if (trimmed.Length > 120)
        {
            throw new ArgumentException("Alias is too long.", nameof(alias));
        }

        var normalized = ProjectIdentityMatcher.Normalize(trimmed);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Alias must contain letters or digits.", nameof(alias));
        }

        EnsureProjectExists(projectId);

        using var connection = _factory.CreateConnection();
        using (var clash = connection.CreateCommand())
        {
            clash.CommandText =
                """
                SELECT project_id FROM project_aliases
                WHERE normalized_alias = $n
                LIMIT 1;
                """;
            clash.Parameters.AddWithValue("$n", normalized);
            var existingProject = clash.ExecuteScalar() as string;
            if (existingProject is not null)
            {
                if (string.Equals(existingProject, projectId.Trim(), StringComparison.Ordinal))
                {
                    throw new ArgumentException("That alias is already on this project.", nameof(alias));
                }

                throw new InvalidOperationException(
                    "That alias is already used by another project. Choose a different nickname or remove it from the other project first.");
            }
        }

        var id = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO project_aliases (id, project_id, alias, normalized_alias, created_at)
            VALUES ($id, $p, $alias, $n, $t);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$p", projectId.Trim());
        insert.Parameters.AddWithValue("$alias", trimmed);
        insert.Parameters.AddWithValue("$n", normalized);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();

        TouchProject(connection, projectId.Trim(), now);

        return new ProjectAliasRecord
        {
            Id = id,
            ProjectId = projectId.Trim(),
            Alias = trimmed,
            NormalizedAlias = normalized,
            CreatedAt = now,
        };
    }

    /// <summary>Removes an alias by id or by alias text (case-insensitive / normalized).</summary>
    public bool RemoveAlias(string projectId, string aliasIdOrText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasIdOrText);
        EnsureProjectExists(projectId);

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM project_aliases
            WHERE project_id = $p
              AND (id = $key OR normalized_alias = $n OR lower(alias) = lower($raw))
            ;
            """;
        cmd.Parameters.AddWithValue("$p", projectId.Trim());
        cmd.Parameters.AddWithValue("$key", aliasIdOrText.Trim());
        cmd.Parameters.AddWithValue("$n", ProjectIdentityMatcher.Normalize(aliasIdOrText));
        cmd.Parameters.AddWithValue("$raw", aliasIdOrText.Trim());
        var rows = cmd.ExecuteNonQuery();
        if (rows > 0)
        {
            TouchProject(connection, projectId.Trim(), DateTimeOffset.UtcNow.ToString("O"));
        }

        return rows > 0;
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

    private void EnsureProjectExists(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId.Trim());
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }
    }

    private static void TouchProject(SqliteConnection connection, string projectId, string now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE projects SET updated_at = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$id", projectId);
        cmd.ExecuteNonQuery();
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentException("Project code is too long.", nameof(code));
        }

        return trimmed;
    }

    private static ProjectAliasRecord ReadAlias(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            ProjectId = reader.GetString(1),
            Alias = reader.GetString(2),
            NormalizedAlias = reader.GetString(3),
            CreatedAt = reader.GetString(4),
        };

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
