namespace Orbit.Infrastructure.Files;

/// <summary>
/// Read-only capability surface for external/project folders.
/// Intentionally omits Write, Delete, Rename, Move, and Overwrite.
/// </summary>
public interface IExternalFileCapability
{
    IReadOnlyList<ExternalFileStat> List(string rootPath, string? relativeDirectory = null);

    ExternalFileStat? Stat(string fullPath);

    Stream OpenRead(string fullPath);

    string? ReadTextPreview(string fullPath, int maxChars = 65_536);

    void OpenExternally(string fullPath);
}
