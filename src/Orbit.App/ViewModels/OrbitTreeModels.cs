using System.Collections.ObjectModel;

namespace Orbit_App.ViewModels;

public enum OrbitTreeNodeKind
{
    Project,
    Task,
    Subtask,
    Limbo,
    Completed,
}

public sealed class OrbitTreeNodeVm
{
    public OrbitTreeNodeKind Kind { get; init; }

    public string Id { get; init; } = string.Empty;

    public string? ProjectId { get; init; }

    public string? ParentTaskId { get; init; }

    public string Title { get; set; } = string.Empty;

    public string? Status { get; set; }

    public string? NextAction { get; set; }

    public ObservableCollection<OrbitTreeNodeVm> Children { get; } = [];

    public string Glyph => Kind switch
    {
        OrbitTreeNodeKind.Project => "\uE8B7",
        OrbitTreeNodeKind.Task => "\uE8FD",
        OrbitTreeNodeKind.Subtask => "\uE8AB",
        OrbitTreeNodeKind.Limbo => "\uE946",
        OrbitTreeNodeKind.Completed => "\uE73E",
        _ => "\uE8A5",
    };

    public string Subtitle
    {
        get
        {
            if (Kind == OrbitTreeNodeKind.Project)
            {
                var open = Children.Count(c => c.Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask);
                var completed = Children.FirstOrDefault(c => c.Kind == OrbitTreeNodeKind.Completed);
                var done = completed?.Children.Count ?? 0;
                if (open == 0 && done == 0)
                {
                    return "No open tasks";
                }

                return done == 0 ? $"{open} open" : $"{open} open · {done} done";
            }

            if (Kind == OrbitTreeNodeKind.Completed)
            {
                return Children.Count == 0 ? "None" : $"{Children.Count} done";
            }

            if (!string.IsNullOrWhiteSpace(NextAction))
            {
                return NextAction!;
            }

            return StatusLabel;
        }
    }

    public string StatusLabel => Status switch
    {
        "blocked" => "Blocked",
        "waiting" => "Waiting",
        "active" => "Active",
        "not_started" => "New",
        "complete" => "Done",
        _ => Status ?? string.Empty,
    };
}
