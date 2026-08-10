using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

public sealed class WorkbenchCellLayout
{
    public required string EntityKind { get; init; }

    public required string EntityId { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public int SortOrder { get; init; }
}

/// <summary>Persists workbench cell geometry and order for projects, tasks, and synthetic cells.</summary>
public sealed class WorkbenchLayoutStore
{
    public const double MinWidth = 200;
    public const double MinHeight = 180;
    public const double MaxWidth = 960;
    public const double MaxHeight = 800;

    private readonly SqliteConnectionFactory _factory;

    public WorkbenchLayoutStore(SqliteConnectionFactory factory) => _factory = factory;

    public WorkbenchCellLayout? TryGetSyntheticLayout(string cellId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT cell_kind, board_x, board_y, board_w, board_h, sort_order
            FROM workbench_synthetic_layouts
            WHERE cell_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", cellId.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new WorkbenchCellLayout
        {
            EntityKind = reader.GetString(0),
            EntityId = cellId.Trim(),
            X = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
            Y = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
            Width = reader.IsDBNull(3) ? MinWidth : reader.GetDouble(3),
            Height = reader.IsDBNull(4) ? MinHeight : reader.GetDouble(4),
            SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
        };
    }

    public WorkbenchCellLayout SetLayout(
        string entityKind,
        string entityId,
        double x,
        double y,
        double width,
        double height,
        int sortOrder = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var kind = entityKind.Trim().ToLowerInvariant();
        if (kind is not ("project" or "task" or "limbo"))
        {
            throw new ArgumentException("Entity kind must be project, task, or limbo.", nameof(entityKind));
        }

        var layout = new WorkbenchCellLayout
        {
            EntityKind = kind,
            EntityId = entityId.Trim(),
            X = Snap(Math.Max(0, x)),
            Y = Snap(Math.Max(0, y)),
            Width = Snap(Math.Clamp(width, MinWidth, MaxWidth)),
            Height = Snap(Math.Clamp(height, MinHeight, MaxHeight)),
            SortOrder = sortOrder,
        };

        using var connection = _factory.CreateConnection();
        if (kind == "limbo")
        {
            if (!string.Equals(layout.EntityId, WorkbenchCellKinds.LimboEntityId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Limbo layout id must be the limbo sentinel.", nameof(entityId));
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO workbench_synthetic_layouts
                  (cell_id, cell_kind, board_x, board_y, board_w, board_h, sort_order, updated_at)
                VALUES ($id, $kind, $x, $y, $w, $h, $o, $t)
                ON CONFLICT(cell_id) DO UPDATE SET
                  cell_kind = excluded.cell_kind,
                  board_x = excluded.board_x,
                  board_y = excluded.board_y,
                  board_w = excluded.board_w,
                  board_h = excluded.board_h,
                  sort_order = excluded.sort_order,
                  updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", layout.EntityId);
            cmd.Parameters.AddWithValue("$kind", layout.EntityKind);
            cmd.Parameters.AddWithValue("$x", layout.X);
            cmd.Parameters.AddWithValue("$y", layout.Y);
            cmd.Parameters.AddWithValue("$w", layout.Width);
            cmd.Parameters.AddWithValue("$h", layout.Height);
            cmd.Parameters.AddWithValue("$o", layout.SortOrder);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return layout;
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = kind == "project"
                ? """
                  UPDATE projects
                  SET board_x = $x, board_y = $y, board_w = $w, board_h = $h, sort_order = $o, updated_at = $t
                  WHERE id = $id AND archived_at IS NULL;
                  """
                : """
                  UPDATE tasks
                  SET board_x = $x, board_y = $y, board_w = $w, board_h = $h, sort_order = $o, updated_at = $t
                  WHERE id = $id AND archived_at IS NULL;
                  """;
            cmd.Parameters.AddWithValue("$id", layout.EntityId);
            cmd.Parameters.AddWithValue("$x", layout.X);
            cmd.Parameters.AddWithValue("$y", layout.Y);
            cmd.Parameters.AddWithValue("$w", layout.Width);
            cmd.Parameters.AddWithValue("$h", layout.Height);
            cmd.Parameters.AddWithValue("$o", layout.SortOrder);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException($"{kind} was not found.", nameof(entityId));
            }
        }

        return layout;
    }

    private static double Snap(double value) => Math.Round(value / 16.0) * 16.0;
}
