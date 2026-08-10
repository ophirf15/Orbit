using System.Text.Json;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Files;

namespace Orbit.Infrastructure.Pulse;

public sealed record OrbitIgnitionProjectResult
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool Created { get; init; }

    public string? HomeFolderPath { get; init; }

    public string? Error { get; init; }
}

public sealed class OrbitIgnitionService
{
    private readonly PulseReadStore _pulse;
    private readonly ProjectWriteStore _projects;
    private readonly ProjectFolderStore? _folders;

    public OrbitIgnitionService(
        PulseReadStore pulse,
        ProjectWriteStore projects,
        ProjectFolderStore? folders = null)
    {
        _pulse = pulse;
        _projects = projects;
        _folders = folders;
    }

    public IReadOnlyList<OrbitIgnitionProjectResult> FromList(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var results = new List<OrbitIgnitionProjectResult>(names.Count);
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            results.Add(EnsureInOrbitByName(raw.Trim()));
        }

        return results;
    }

    public IReadOnlyList<OrbitIgnitionProjectResult> FromProjectsRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullRoot = Path.GetFullPath(rootPath.Trim());
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Projects root was not found: {fullRoot}");
        }

        var results = new List<OrbitIgnitionProjectResult>();
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(fullRoot);
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not enumerate projects root: {fullRoot}", ex);
        }

        foreach (var dir in dirs)
        {
            var folderName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            if (folderName.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var result = EnsureInOrbitByName(ProjectNaming.FromFolderPath(dir), dir);
                if (_folders is not null)
                {
                    try
                    {
                        _folders.SetHome(result.Id, dir);
                        result = result with { HomeFolderPath = dir };
                    }
                    catch (Exception ex)
                    {
                        result = result with { Error = ex.Message };
                    }
                }

                results.Add(result);
            }
            catch (Exception ex)
            {
                results.Add(new OrbitIgnitionProjectResult
                {
                    Id = string.Empty,
                    Name = folderName,
                    Created = false,
                    Error = ex.Message,
                });
            }
        }

        return results;
    }

    public PulseSnapshotRecord Confirm()
    {
        _pulse.SetIgnitionCompleted(true);
        var projects = _pulse.ListOrbitProjects();
        var count = projects.Count;
        var dayBrief = count == 1
            ? "Orbit ignition complete. 1 project in orbit."
            : $"Orbit ignition complete. {count} projects in orbit.";
        return _pulse.SaveSnapshot(dayBrief, PulseReadStore.BuildConfirmPayloadJson(projects));
    }

    private OrbitIgnitionProjectResult EnsureInOrbitByName(string name, string? homeFolderPath = null)
    {
        var existingId = _pulse.FindProjectIdByName(name);
        if (existingId is not null)
        {
            _pulse.SetInOrbit(existingId, true);
            return new OrbitIgnitionProjectResult
            {
                Id = existingId,
                Name = name,
                Created = false,
                HomeFolderPath = homeFolderPath,
            };
        }

        var created = _projects.Create(name);
        _pulse.SetInOrbit(created.Id, true);
        return new OrbitIgnitionProjectResult
        {
            Id = created.Id,
            Name = created.Name,
            Created = true,
            HomeFolderPath = homeFolderPath,
        };
    }
}
