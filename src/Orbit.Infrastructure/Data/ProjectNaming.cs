namespace Orbit.Infrastructure.Data;

/// <summary>Helpers for deriving project display names.</summary>
public static class ProjectNaming
{
    /// <summary>Uses the last path segment of a folder path; falls back to "Untitled project".</summary>
    public static string FromFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Untitled project";
        }

        var trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "Untitled project" : name.Trim();
    }
}
