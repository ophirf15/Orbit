namespace Orbit.Core.Data;

/// <summary>Workbench cell kinds returned in snapshot payloads.</summary>
public static class WorkbenchCellKinds
{
    public const string Project = "project";
    public const string Task = "task";
    public const string Limbo = "limbo";

    /// <summary>Stable sentinel id for the root Limbo cell (not a projects row).</summary>
    public const string LimboEntityId = "limbo";
}
