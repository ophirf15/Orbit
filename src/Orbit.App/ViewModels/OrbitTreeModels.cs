using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Orbit.Core.Workbench;

namespace Orbit_App.ViewModels;

public enum OrbitTreeNodeKind
{
    Project,
    Task,
    Subtask,
    Limbo,
    Completed,
}

public sealed class OrbitTreeNodeVm : INotifyPropertyChanged
{
    private OrbitTreeNodeKind _kind;
    private string _title = string.Empty;
    private string? _status;
    private string? _nextAction;
    private string? _projectId;
    private string? _parentTaskId;

    public OrbitTreeNodeVm()
    {
        Children.CollectionChanged += OnChildrenChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public OrbitTreeNodeKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
            {
                return;
            }

            _kind = value;
            OnPropertyChanged();
            NotifyPresentationChanged();
        }
    }

    public string Id { get; init; } = string.Empty;

    public string? ProjectId
    {
        get => _projectId;
        set
        {
            if (_projectId == value)
            {
                return;
            }

            _projectId = value;
            OnPropertyChanged();
        }
    }

    public string? ParentTaskId
    {
        get => _parentTaskId;
        set
        {
            if (_parentTaskId == value)
            {
                return;
            }

            _parentTaskId = value;
            OnPropertyChanged();
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
        }
    }

    public string? Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            NotifyPresentationChanged();
        }
    }

    public string? NextAction
    {
        get => _nextAction;
        set
        {
            if (_nextAction == value)
            {
                return;
            }

            _nextAction = value;
            OnPropertyChanged();
            NotifyPresentationChanged();
        }
    }

    public ObservableCollection<OrbitTreeNodeVm> Children { get; } = [];

    public string Glyph => Kind switch
    {
        OrbitTreeNodeKind.Project => OrbitTreeOperationalIndicators.GlyphProject,
        OrbitTreeNodeKind.Limbo => OrbitTreeOperationalIndicators.GlyphLimbo,
        OrbitTreeNodeKind.Completed => OrbitTreeOperationalIndicators.GlyphCompletedGroup,
        OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask =>
            OrbitTreeOperationalIndicators.ForTaskStatus(Status, NextAction).Glyph,
        _ => OrbitTreeOperationalIndicators.GlyphDefault,
    };

    public string ToolTipText
    {
        get
        {
            if (Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask)
            {
                var indicator = OrbitTreeOperationalIndicators.ForTaskStatus(Status, NextAction);
                if (!string.IsNullOrWhiteSpace(NextAction)
                    && !string.Equals(NextAction, indicator.Tooltip, StringComparison.Ordinal))
                {
                    return $"{indicator.Tooltip} — {NextAction}";
                }

                return indicator.Tooltip;
            }

            if (Kind == OrbitTreeNodeKind.Project)
            {
                return Subtitle;
            }

            if (Kind == OrbitTreeNodeKind.Completed)
            {
                return Subtitle;
            }

            if (Kind == OrbitTreeNodeKind.Limbo)
            {
                return string.IsNullOrWhiteSpace(NextAction) ? Title : NextAction!;
            }

            return Title;
        }
    }

    public string Subtitle
    {
        get
        {
            if (Kind == OrbitTreeNodeKind.Project)
            {
                var openTasks = EnumerateOperationalTasks(this).ToList();
                var (open, blocked, waiting) = OrbitTreeOperationalIndicators.CountOpenTaskStatuses(
                    openTasks.Select(t => t.Status));
                var completed = Children.FirstOrDefault(c => c.Kind == OrbitTreeNodeKind.Completed);
                var done = completed?.Children.Count ?? 0;
                return OrbitTreeOperationalIndicators.FormatProjectSubtitle(open, blocked, waiting, done);
            }

            if (Kind == OrbitTreeNodeKind.Completed)
            {
                return Children.Count == 0 ? "None" : $"{Children.Count} done";
            }

            if (Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask)
            {
                var indicator = OrbitTreeOperationalIndicators.ForTaskStatus(Status, NextAction);
                if (!string.IsNullOrWhiteSpace(NextAction))
                {
                    // Short operational caption + next move (not color-only).
                    return $"{indicator.Label} · {NextAction}";
                }

                return indicator.Label;
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

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Child add/remove updates project open/blocked/waiting captions.
        // Status soft-updates rely on workbench reload (ContentChanged), not bubbling —
        // Reset/Clear does not supply OldItems, so per-child subscriptions would leak.
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ToolTipText));
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ToolTipText));
    }

    /// <summary>
    /// Open tasks/subtasks under this node, skipping the Completed group (and its children).
    /// </summary>
    private static IEnumerable<OrbitTreeNodeVm> EnumerateOperationalTasks(OrbitTreeNodeVm node)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == OrbitTreeNodeKind.Completed)
            {
                continue;
            }

            if (child.Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask)
            {
                yield return child;
            }

            foreach (var nested in EnumerateOperationalTasks(child))
            {
                yield return nested;
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
