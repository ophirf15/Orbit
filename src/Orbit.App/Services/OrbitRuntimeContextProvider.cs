using Orbit.Core.Agent;

namespace Orbit_App.Services;

public sealed class OrbitRuntimeContextProvider
{
    public static OrbitRuntimeContextProvider Instance { get; } = new();

    private readonly object _gate = new();
    private string _route = "nav.workbench";
    private string? _projectId;
    private string? _projectName;
    private string? _taskId;
    private string? _selectedEntityType;
    private string? _selectedEntityId;
    private IReadOnlyList<string> _workbenchNames = [];

    public void SetRoute(string route)
    {
        lock (_gate)
        {
            _route = string.IsNullOrWhiteSpace(route) ? "unknown" : route.Trim();
        }
    }

    public void SetWorkbenchProjects(IEnumerable<string> names)
    {
        lock (_gate)
        {
            _workbenchNames = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(24).ToList();
        }
    }

    public void SetFocus(string? projectId, string? projectName, string? taskId = null)
    {
        lock (_gate)
        {
            _projectId = projectId;
            _projectName = projectName;
            _taskId = taskId;
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                _selectedEntityType = "task";
                _selectedEntityId = taskId;
            }
            else if (!string.IsNullOrWhiteSpace(projectId))
            {
                _selectedEntityType = "project";
                _selectedEntityId = projectId;
            }
        }
    }

    public void SetSelectedEntity(string? entityType, string? entityId)
    {
        lock (_gate)
        {
            _selectedEntityType = entityType;
            _selectedEntityId = entityId;
        }
    }

    public OrbitRuntimeContext Capture()
    {
        lock (_gate)
        {
            return new OrbitRuntimeContext
            {
                Route = _route,
                ProjectId = _projectId,
                ProjectName = _projectName,
                TaskId = _taskId,
                SelectedEntityType = _selectedEntityType,
                SelectedEntityId = _selectedEntityId,
                LocalDataRoot = App.Settings.LocalDataRoot,
                CoreHostUrl = App.Settings.CoreHostBaseUrl,
                WorkbenchProjectNames = _workbenchNames,
            };
        }
    }
}
