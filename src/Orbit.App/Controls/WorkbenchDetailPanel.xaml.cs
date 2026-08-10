using System.Collections.Concurrent;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Core.Agent;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Hermes;
using Orbit_App.Services;
using Orbit_App.ViewModels;
using Windows.System;
using Windows.UI;

namespace Orbit_App.Controls;

public sealed partial class WorkbenchDetailPanel : UserControl
{
    private static readonly ConcurrentDictionary<string, AgentSessionState> Sessions = new(StringComparer.Ordinal);

    private static readonly HashSet<string> RelationshipSuggestionTypes = new(StringComparer.Ordinal)
    {
        "link_tasks",
        "merge_into_task",
        "dependency_ready",
        "reporting_relationship",
    };

    public event EventHandler? CloseRequested;

    public event EventHandler? ContentChanged;

    /// <summary>Raised in inline mode when Back should restore the project workspace.</summary>
    public event EventHandler<string>? BackToProjectRequested;

    private bool _inlineMode;

    public void SetInlineMode(bool inline)
    {
        _inlineMode = inline;
        // Close button still dismisses selection in inline mode.
    }

    private string? _projectId;
    private string? _projectName;
    private string? _taskId;
    private string? _sessionKey;
    private string? _primaryEmailId;
    private CellLineVm? _task;
    private LimboNoteVm? _limboNote;
    private ProjectContextVm? _context;
    private TaskLinksVm? _links;
    private List<PendingSuggestionVm> _pendingRelationshipSuggestions = [];
    private List<HermesChatMessage> _agentHistory = [];
    private string _agentTranscriptText = string.Empty;
    private bool _agentBusy;

    private sealed class AgentSessionState
    {
        public List<HermesChatMessage> History { get; set; } = [];

        public string Transcript { get; set; } = string.Empty;
    }

    public WorkbenchDetailPanel()
    {
        InitializeComponent();
        KeyDown += WorkbenchDetailPanel_KeyDown;
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (TryCancelFocusedEntry())
            {
                args.Handled = true;
                return;
            }

            PersistCurrentSession();
            CloseRequested?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
    }

    private void WorkbenchDetailPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled)
        {
            return;
        }

        if (TryCancelFocusedEntry())
        {
            e.Handled = true;
            return;
        }

        PersistCurrentSession();
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private bool TryCancelFocusedEntry()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox box)
        {
            return false;
        }

        if (ReferenceEquals(box, TitleBox))
        {
            var committed = _task?.Title ?? _projectName ?? string.Empty;
            if (!string.Equals(box.Text ?? string.Empty, committed, StringComparison.Ordinal))
            {
                box.Text = committed;
                return true;
            }

            return false;
        }

        if (ReferenceEquals(box, AgentInput))
        {
            if (!string.IsNullOrEmpty(box.Text))
            {
                box.Text = string.Empty;
                return true;
            }

            return false;
        }

        // Soft fields stash the last-committed value in Tag (string).
        if (box.Tag is string baseline
            && !string.Equals(box.Text ?? string.Empty, baseline, StringComparison.Ordinal))
        {
            box.Text = baseline;
            return true;
        }

        return false;
    }

    public async Task LoadProjectAsync(string projectId)
    {
        await LoadCoreAsync(projectId, taskId: null);
    }

    public async Task LoadTaskAsync(string projectId, string taskId)
    {
        await LoadCoreAsync(projectId, taskId);
    }

    public async Task LoadLimboNoteAsync(string noteId)
    {
        PersistCurrentSession();
        _projectId = null;
        _projectName = "Limbo";
        _taskId = null;
        _task = null;
        _context = null;
        _links = null;
        _primaryEmailId = null;
        _sessionKey = $"limbo:{noteId}";
        FooterHint.Text = "Loading…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            _limboNote = await client.GetLimboNoteAsync(noteId);
            if (_limboNote is null)
            {
                FooterHint.Text = "Limbo note not found.";
                return;
            }

            ProjectLabel.Text = "Limbo";
            TitleBox.Text = TruncateTitle(_limboNote.OriginalText);
            DeleteTaskButton.Visibility = Visibility.Collapsed;
            ArchiveButton.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            BackButton.SetValue(ToolTipService.ToolTipProperty, "Close");
            DetailTabs.SelectedIndex = 0;
            BuildOverview();
            BuildNotes();
            LinksHost.Children.Clear();
            ContactsHost.Children.Clear();
            FilesHost.Children.Clear();
            FieldsHost.Children.Clear();
            RestoreOrSeedAgentSession();
            FooterHint.Text = "Limbo brief";
        }
        catch (Exception)
        {
            FooterHint.Text = "Load failed.";
        }
    }

    private async Task LoadCoreAsync(string projectId, string? taskId)
    {
        PersistCurrentSession();
        _projectId = projectId;
        _taskId = taskId;
        _limboNote = null;
        _primaryEmailId = null;
        _sessionKey = string.IsNullOrWhiteSpace(taskId) ? $"project:{projectId}" : $"task:{taskId}";
        FooterHint.Text = "Loading…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                _task = await client.GetTaskAsync(taskId);
                if (_task is null)
                {
                    FooterHint.Text = "Task not found.";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_task.ProjectId)
                    && !string.Equals(_task.ProjectId, projectId, StringComparison.Ordinal))
                {
                    projectId = _task.ProjectId!;
                    _projectId = projectId;
                }
            }
            else
            {
                _task = null;
            }

            _context = await client.GetProjectContextAsync(projectId);
            if (_context is null)
            {
                FooterHint.Text = "Could not load context.";
                return;
            }

            _projectName = _context.Name;
            ProjectLabel.Text = _context.Name;
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                // Prefer authoritative by-id payload; fall back to context line only if by-id somehow empty.
                if (_task is null)
                {
                    _task = _context.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                }

                if (_task is null)
                {
                    FooterHint.Text = "Task not found.";
                    return;
                }

                TitleBox.Text = _task.Title;
                DeleteTaskButton.Visibility = Visibility.Visible;
                ArchiveButton.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Visible;
                BackButton.SetValue(ToolTipService.ToolTipProperty, $"Back to {_context.Name}");
                OrbitRuntimeContextProvider.Instance.SetFocus(projectId, _context.Name, taskId);

                var threads = await client.GetTaskEmailThreadsAsync(taskId);
                _primaryEmailId = threads
                    .Select(t => t.AnchorEmailId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            }
            else
            {
                TitleBox.Text = _context.Name;
                DeleteTaskButton.Visibility = Visibility.Collapsed;
                ArchiveButton.Visibility = Visibility.Collapsed;
                BackButton.Visibility = Visibility.Visible;
                BackButton.SetValue(ToolTipService.ToolTipProperty, "Close");
                OrbitRuntimeContextProvider.Instance.SetFocus(projectId, _context.Name);
            }

            DetailTabs.SelectedIndex = 0;
            BuildOverview();
            BuildNotes();
            await BuildLinksAsync(client);
            BuildContacts();
            BuildFiles();
            await BuildFieldsAsync(client);
            RestoreOrSeedAgentSession();
            FooterHint.Text = string.IsNullOrWhiteSpace(taskId) ? "Project detail" : "Task brief";
        }
        catch (Exception)
        {
            FooterHint.Text = "Load failed.";
        }
    }

    private static string TruncateTitle(string text)
    {
        var t = (text ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ");
        return t.Length <= 80 ? t : t[..77] + "…";
    }

    private void PersistCurrentSession()
    {
        if (string.IsNullOrWhiteSpace(_sessionKey))
        {
            return;
        }

        Sessions[_sessionKey] = new AgentSessionState
        {
            History = _agentHistory.Select(m => new HermesChatMessage { Role = m.Role, Content = m.Content }).ToList(),
            Transcript = AgentTranscript.Text ?? _agentTranscriptText,
        };
    }

    private void RestoreOrSeedAgentSession()
    {
        if (!string.IsNullOrWhiteSpace(_sessionKey) && Sessions.TryGetValue(_sessionKey, out var existing)
            && existing.History.Count > 0)
        {
            _agentHistory = existing.History
                .Select(m => new HermesChatMessage { Role = m.Role, Content = m.Content })
                .ToList();
            _agentTranscriptText = existing.Transcript;
            AgentTranscript.Text = existing.Transcript;
            return;
        }

        SeedAgentTranscript();
    }

    private void BuildOverview()
    {
        OverviewHost.Children.Clear();

        if (_limboNote is not null)
        {
            OverviewHost.Children.Add(Label("What this is"));
            OverviewHost.Children.Add(BodyText(
                string.IsNullOrWhiteSpace(_limboNote.OriginalText)
                    ? "(empty capture)"
                    : _limboNote.OriginalText));

            if (_limboNote.HasSuggestion)
            {
                OverviewHost.Children.Add(Label("Pending suggestion"));
                OverviewHost.Children.Add(BodyText(_limboNote.SuggestionSummary!));
            }

            OverviewHost.Children.Add(BodyText(
                "Assign this to a project when you know where it belongs — Hermes should attach matches automatically."));
            return;
        }

        if (_task is not null)
        {
            OverviewHost.Children.Add(Label("Brief"));
            var briefBox = SoftTextBox("Living brief — what this is about…", minHeight: 140, acceptsReturn: true);
            briefBox.Text = _task.Body ?? string.Empty;
            briefBox.Tag = briefBox.Text;
            briefBox.LostFocus += async (_, _) =>
            {
                await SaveTaskBodyAsync(briefBox.Text);
                briefBox.Tag = briefBox.Text ?? string.Empty;
            };
            OverviewHost.Children.Add(briefBox);

            OverviewHost.Children.Add(Label("Next move"));
            var nextBox = SoftTextBox("What should happen next?", minHeight: 0, acceptsReturn: false);
            nextBox.Text = _task.NextAction ?? string.Empty;
            nextBox.Tag = nextBox.Text;
            nextBox.LostFocus += async (_, _) => await SaveTaskNextActionAsync(nextBox.Text);
            nextBox.KeyDown += async (_, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    await SaveTaskNextActionAsync(nextBox.Text);
                    nextBox.Tag = nextBox.Text?.Trim() ?? string.Empty;
                }
            };
            OverviewHost.Children.Add(nextBox);

            if (!string.IsNullOrWhiteSpace(_primaryEmailId))
            {
                var openMail = SoftAccentButton("Open original email");
                openMail.HorizontalAlignment = HorizontalAlignment.Stretch;
                var emailId = _primaryEmailId!;
                openMail.Click += async (_, _) =>
                {
                    using var openClient = new CoreHostClient(App.Settings, App.SettingsStore);
                    FooterHint.Text = await openClient.OpenEmailInOutlookAsync(emailId)
                        ? "Opened in Outlook."
                        : "Could not open email.";
                };
                OverviewHost.Children.Add(openMail);
            }

            OverviewHost.Children.Add(Label("Status"));
            var combo = SoftCombo();
            var statuses = new (string Id, string Label)[]
            {
                ("not_started", "New"),
                ("active", "Active"),
                ("waiting", "Waiting"),
                ("blocked", "Blocked"),
                ("complete", "Complete"),
            };
            foreach (var (id, label) in statuses)
            {
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            }

            combo.SelectedIndex = Math.Max(0, Array.FindIndex(statuses, s => s.Id == _task.Status));
            combo.SelectionChanged += async (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string status } && _taskId is not null)
                {
                    using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                    if (await client.UpdateTaskAsync(_taskId, status: status))
                    {
                        _task.Status = status;
                        ContentChanged?.Invoke(this, EventArgs.Empty);
                        FooterHint.Text = "Status saved.";
                    }
                }
            };
            OverviewHost.Children.Add(combo);

            OverviewHost.Children.Add(Label("Due"));
            var dueBox = SoftTextBox("YYYY-MM-DD or ISO datetime…");
            dueBox.Text = _task.DueAt ?? string.Empty;
            dueBox.Tag = dueBox.Text;
            dueBox.LostFocus += async (_, _) =>
            {
                if (_taskId is null)
                {
                    return;
                }

                var next = dueBox.Text?.Trim() ?? string.Empty;
                var baseline = dueBox.Tag as string ?? string.Empty;
                if (string.Equals(next, baseline, StringComparison.Ordinal))
                {
                    return;
                }

                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                if (await client.UpdateTaskAsync(_taskId, dueAt: next))
                {
                    _task.DueAt = string.IsNullOrWhiteSpace(next) ? null : next;
                    dueBox.Tag = next;
                    FooterHint.Text = "Due date saved.";
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }
            };
            OverviewHost.Children.Add(dueBox);

            OverviewHost.Children.Add(Label("Blockers"));
            if (_context?.Blockers.Count > 0)
            {
                foreach (var b in _context.Blockers)
                {
                    OverviewHost.Children.Add(BodyText(b));
                }
            }
            else
            {
                OverviewHost.Children.Add(BodyText("None open."));
            }

            var blockerBox = SoftTextBox("Add blocker summary…");
            var addBlocker = SoftSubtleButton("Set blocker");
            addBlocker.Click += async (_, _) =>
            {
                var summary = blockerBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(summary) || _taskId is null)
                {
                    return;
                }

                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                if (await client.SetBlockerAsync(summary, projectId: _projectId, taskId: _taskId))
                {
                    await client.UpdateTaskAsync(_taskId, status: "blocked");
                    blockerBox.Text = string.Empty;
                    FooterHint.Text = "Blocker set.";
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    if (_projectId is not null)
                    {
                        await LoadTaskAsync(_projectId, _taskId);
                    }
                }
            };
            OverviewHost.Children.Add(blockerBox);
            OverviewHost.Children.Add(addBlocker);

            var openAgent = SoftSubtleButton("Open Agent");
            openAgent.HorizontalAlignment = HorizontalAlignment.Stretch;
            openAgent.Click += (_, _) =>
            {
                DetailTabs.SelectedIndex = 1;
                AgentInput.Focus(FocusState.Programmatic);
            };
            OverviewHost.Children.Add(openAgent);
        }
        else if (_context is not null)
        {
            OverviewHost.Children.Add(Label("Summary"));
            var summaryBox = SoftTextBox("Project summary…", minHeight: 72, acceptsReturn: true);
            summaryBox.Text = _context.Summary ?? string.Empty;
            summaryBox.Tag = summaryBox.Text;
            summaryBox.LostFocus += async (_, _) =>
            {
                await SaveProjectSummaryAsync(summaryBox.Text);
                summaryBox.Tag = summaryBox.Text?.Trim() ?? string.Empty;
            };
            OverviewHost.Children.Add(summaryBox);

            OverviewHost.Children.Add(Label("Tasks"));
            foreach (var task in _context.Tasks.Take(12))
            {
                var btn = SoftSubtleButton(task.DisplayLine);
                btn.HorizontalAlignment = HorizontalAlignment.Stretch;
                btn.HorizontalContentAlignment = HorizontalAlignment.Left;
                btn.Margin = new Thickness(0, 0, 0, 4);
                btn.Tag = task.TaskId;
                btn.Click += async (_, _) =>
                {
                    if (btn.Tag is string id && _projectId is not null)
                    {
                        await LoadTaskAsync(_projectId, id);
                    }
                };
                OverviewHost.Children.Add(btn);
            }

            OverviewHost.Children.Add(Label("Blockers"));
            if (_context.Blockers.Count > 0)
            {
                foreach (var b in _context.Blockers)
                {
                    OverviewHost.Children.Add(BodyText(b));
                }
            }
            else
            {
                OverviewHost.Children.Add(BodyText("None open."));
            }

            var projectBlockerBox = SoftTextBox("Add project blocker…");
            var addProjectBlocker = SoftSubtleButton("Set blocker");
            addProjectBlocker.Click += async (_, _) =>
            {
                var summary = projectBlockerBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(summary) || _projectId is null)
                {
                    return;
                }

                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                if (await client.SetBlockerAsync(summary, projectId: _projectId))
                {
                    projectBlockerBox.Text = string.Empty;
                    FooterHint.Text = "Blocker set.";
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    await LoadProjectAsync(_projectId);
                }
            };
            OverviewHost.Children.Add(projectBlockerBox);
            OverviewHost.Children.Add(addProjectBlocker);

            var openAgent = SoftSubtleButton("Open Agent");
            openAgent.HorizontalAlignment = HorizontalAlignment.Stretch;
            openAgent.Click += (_, _) =>
            {
                DetailTabs.SelectedIndex = 1;
                AgentInput.Focus(FocusState.Programmatic);
            };
            OverviewHost.Children.Add(openAgent);
        }
    }

    private void BuildNotes()
    {
        NotesHost.Children.Clear();
        NotesHost.Children.Add(Label("Project notes"));
        if (_context is null || _context.Notes.Count == 0)
        {
            NotesHost.Children.Add(BodyText("No capture notes yet."));
        }
        else
        {
            foreach (var note in _context.Notes)
            {
                var noteId = note.Id;
                var box = SoftTextBox("Note…", minHeight: 56, acceptsReturn: true);
                box.Text = note.Text;
                box.Tag = note.Text;
                box.LostFocus += async (_, _) =>
                {
                    var text = box.Text?.Trim() ?? string.Empty;
                    var baseline = box.Tag as string ?? note.Text;
                    if (string.Equals(text, baseline, StringComparison.Ordinal))
                    {
                        return;
                    }

                    using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                    if (text.Length == 0)
                    {
                        if (await client.ArchiveEntityAsync("note", noteId))
                        {
                            FooterHint.Text = "Note deleted.";
                            if (_projectId is not null)
                            {
                                await LoadProjectAsync(_projectId);
                            }

                            ContentChanged?.Invoke(this, EventArgs.Empty);
                        }

                        return;
                    }

                    if (await client.UpdateNoteAsync(noteId, text))
                    {
                        note.Text = text;
                        box.Tag = text;
                        FooterHint.Text = "Note saved.";
                        ContentChanged?.Invoke(this, EventArgs.Empty);
                    }
                };
                NotesHost.Children.Add(box);
            }
        }

        NotesHost.Children.Add(Label("Add freeform note"));
        var addBox = SoftTextBox("Note on this project…", minHeight: 72, acceptsReturn: true);
        var add = SoftAccentButton("Add note");
        add.Click += async (_, _) =>
        {
            var text = addBox.Text?.Trim() ?? string.Empty;
            if (text.Length == 0 || _projectId is null)
            {
                return;
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (_taskId is not null)
            {
                var existing = _task?.Body ?? string.Empty;
                var merged = string.IsNullOrWhiteSpace(existing) ? text : existing.TrimEnd() + "\n\n" + text;
                if (await client.UpdateTaskAsync(_taskId, body: merged))
                {
                    if (_task is not null)
                    {
                        _task.Body = merged;
                    }

                    addBox.Text = string.Empty;
                    FooterHint.Text = "Added to task notes.";
                    BuildOverview();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            if (await client.CreateNoteAsync(text, _projectId) is not null)
            {
                addBox.Text = string.Empty;
                await LoadProjectAsync(_projectId);
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        NotesHost.Children.Add(addBox);
        NotesHost.Children.Add(add);
    }

    private async void TitleBox_LostFocus(object sender, RoutedEventArgs e) => await SaveTitleAsync();

    private async void TitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            var committed = _task?.Title ?? _projectName ?? string.Empty;
            if (!string.Equals(TitleBox.Text ?? string.Empty, committed, StringComparison.Ordinal))
            {
                TitleBox.Text = committed;
            }

            e.Handled = true;
            DetailTabs.Focus(FocusState.Programmatic);
            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SaveTitleAsync();
        // Commit feels done — leave the title box so Enter doesn't feel like a no-op.
        DetailTabs.Focus(FocusState.Programmatic);
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_taskId is not null && _projectId is not null)
        {
            if (_inlineMode)
            {
                PersistCurrentSession();
                BackToProjectRequested?.Invoke(this, _projectId);
                return;
            }

            await LoadProjectAsync(_projectId);
            return;
        }

        Close_Click(sender, e);
    }

    private async Task SaveTitleAsync()
    {
        var title = TitleBox.Text?.Trim() ?? string.Empty;
        if (title.Length == 0 || _projectId is null)
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (_taskId is not null && _task is not null)
            {
                if (string.Equals(title, _task.Title, StringComparison.Ordinal))
                {
                    return;
                }

                if (await client.UpdateTaskAsync(_taskId, title: title))
                {
                    _task.Title = title;
                    FooterHint.Text = "Title saved.";
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            if (string.Equals(title, _context?.Name, StringComparison.Ordinal))
            {
                return;
            }

            if (await client.UpdateProjectAsync(_projectId, name: title))
            {
                if (_context is not null)
                {
                    _context.Name = title;
                }

                _projectName = title;
                ProjectLabel.Text = title;
                FooterHint.Text = "Project renamed.";
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception)
        {
            FooterHint.Text = "Could not save title.";
        }
    }

    private async Task SaveTaskNextActionAsync(string? value)
    {
        if (_taskId is null || _task is null)
        {
            return;
        }

        var next = value?.Trim() ?? string.Empty;
        if (string.Equals(next, _task.NextAction ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateTaskAsync(_taskId, nextAction: next))
        {
            _task.NextAction = string.IsNullOrWhiteSpace(next) ? null : next;
            FooterHint.Text = "Next action saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SaveTaskBodyAsync(string? value)
    {
        if (_taskId is null || _task is null)
        {
            return;
        }

        var body = value ?? string.Empty;
        if (string.Equals(body, _task.Body ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateTaskAsync(_taskId, body: body))
        {
            _task.Body = body;
            FooterHint.Text = "Notes saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SaveProjectSummaryAsync(string? value)
    {
        if (_projectId is null || _context is null)
        {
            return;
        }

        var summary = value?.Trim() ?? string.Empty;
        if (string.Equals(summary, _context.Summary ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateProjectAsync(_projectId, summary: summary))
        {
            _context.Summary = string.IsNullOrWhiteSpace(summary) ? null : summary;
            FooterHint.Text = "Summary saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task BuildLinksAsync(CoreHostClient client)
    {
        LinksHost.Children.Clear();
        _links = null;
        _pendingRelationshipSuggestions = [];

        if (_taskId is null)
        {
            LinksBadge.Visibility = Visibility.Collapsed;
            LinksHost.Children.Add(Label("Task links"));
            LinksHost.Children.Add(BodyText(
                "Open a task to see what it is waiting on and what it feeds into."));
            return;
        }

        _links = await client.GetTaskDependenciesAsync(_taskId);
        var suggestions = await client.GetPendingSuggestionsAsync();
        _pendingRelationshipSuggestions = suggestions
            .Where(s => RelationshipSuggestionTypes.Contains(s.SuggestionType))
            .Where(s => s.TaskId == _taskId)
            .ToList();

        var badgeCount = _pendingRelationshipSuggestions.Count;
        LinksBadge.Value = badgeCount;
        LinksBadge.Visibility = badgeCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_pendingRelationshipSuggestions.Count > 0)
        {
            LinksHost.Children.Add(Label("Needs your confirmation"));
            foreach (var suggestion in _pendingRelationshipSuggestions)
            {
                LinksHost.Children.Add(BuildSuggestionCard(suggestion));
            }
        }

        LinksHost.Children.Add(Label("Waiting on"));
        if (_links.WaitingOn.Count == 0)
        {
            LinksHost.Children.Add(BodyText("Nothing upstream. This task isn't blocked by another task."));
        }
        else
        {
            foreach (var link in _links.WaitingOn)
            {
                LinksHost.Children.Add(BuildLinkCard(link, anchorIsWaiting: true));
            }
        }

        LinksHost.Children.Add(Label("Feeds into"));
        if (_links.Feeds.Count == 0)
        {
            LinksHost.Children.Add(BodyText("No other task is waiting on this one."));
        }
        else
        {
            foreach (var link in _links.Feeds)
            {
                LinksHost.Children.Add(BuildLinkCard(link, anchorIsWaiting: false));
            }
        }

        LinksHost.Children.Add(BuildLinkComposer());

        var scan = SoftSubtleButton("Scan for related tasks");
        scan.Margin = new Thickness(0, 8, 0, 0);
        scan.Click += async (_, _) =>
        {
            if (_taskId is null)
            {
                return;
            }

            FooterHint.Text = "Looking for related tasks…";
            using var scanClient = new CoreHostClient(App.Settings, App.SettingsStore);
            var found = await scanClient.SuggestTaskLinksAsync(_taskId);
            FooterHint.Text = found > 0
                ? $"{found} link suggestion(s) to review."
                : "No new related tasks found.";
            await RefreshLinksAsync();
        };
        LinksHost.Children.Add(scan);

        LinksHost.Children.Add(Label("Email conversations"));
        var threads = await client.GetTaskEmailThreadsAsync(_taskId);
        if (threads.Count == 0)
        {
            LinksHost.Children.Add(BodyText(
                "No tracked email threads yet. Ingest a .msg, then link it here — or accept a merge suggestion."));
        }
        else
        {
            foreach (var thread in threads)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock
                {
                    Text = thread.DisplayLine,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                if (!string.IsNullOrWhiteSpace(thread.AnchorEmailId))
                {
                    var open = SoftSubtleButton("Open in Outlook");
                    var emailId = thread.AnchorEmailId!;
                    open.Click += async (_, _) =>
                    {
                        using var openClient = new CoreHostClient(App.Settings, App.SettingsStore);
                        FooterHint.Text = await openClient.OpenEmailInOutlookAsync(emailId)
                            ? "Opened in Outlook."
                            : "Could not open email.";
                    };
                    row.Children.Add(open);
                }

                LinksHost.Children.Add(new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    Child = row,
                });
            }
        }
    }

    private Border BuildSuggestionCard(PendingSuggestionVm suggestion)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = suggestion.Summary,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });

        var detail = DescribeSuggestion(suggestion);
        if (detail is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.7,
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var confirm = SoftAccentButton(ConfirmLabelFor(suggestion.SuggestionType));
        confirm.Click += async (_, _) => await DecideRelationshipSuggestionAsync(suggestion, accept: true);
        var dismiss = SoftSubtleButton("Dismiss");
        dismiss.Click += async (_, _) => await DecideRelationshipSuggestionAsync(suggestion, accept: false);
        actions.Children.Add(confirm);
        actions.Children.Add(dismiss);
        body.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["OrbitCellAccentBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = body,
        };
    }

    private static string ConfirmLabelFor(string suggestionType) => suggestionType switch
    {
        "link_tasks" => "Link them",
        "merge_into_task" => "Merge it in",
        "dependency_ready" => "Yes, ready",
        "reporting_relationship" => "Confirm reports-to",
        _ => "Accept",
    };

    private static string? DescribeSuggestion(PendingSuggestionVm suggestion)
    {
        var confidence = suggestion.Confidence is { } c
            ? $"confidence {c:P0}"
            : null;
        var kind = suggestion.SuggestionType switch
        {
            "link_tasks" => "Proposed dependency",
            "merge_into_task" => "Inbound info — merging appends to notes, nothing is overwritten",
            "dependency_ready" => "Upstream task finished",
            "reporting_relationship" => "Org chart — confirm reporting line",
            _ => null,
        };

        return (kind, confidence) switch
        {
            (null, null) => null,
            (null, _) => confidence,
            (_, null) => kind,
            _ => $"{kind} · {confidence}",
        };
    }

    private Border BuildLinkCard(TaskLinkVm link, bool anchorIsWaiting)
    {
        var body = new StackPanel { Spacing = 4 };

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new TextBlock
        {
            Text = anchorIsWaiting ? "◀" : "▶",
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        });
        heading.Children.Add(new TextBlock
        {
            Text = Truncate(link.Title, 46),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(heading);

        var descriptor = link.DependencyType switch
        {
            "blocks" => anchorIsWaiting ? "Blocked until that task completes" : "Blocks that task",
            "informs" => anchorIsWaiting ? "Waiting for info from that task" : "Supplies info to that task",
            _ => "Related",
        };
        if (!string.IsNullOrWhiteSpace(link.Expects))
        {
            descriptor += $" — needs {link.Expects}";
        }

        body.Children.Add(new TextBlock
        {
            Text = $"{descriptor} · {link.Status}",
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        if (link.Satisfied && anchorIsWaiting)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Upstream task is done — this may be unblocked.",
                FontSize = 11,
                Opacity = 0.9,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var open = SoftSubtleButton("Open");
        open.Click += async (_, _) =>
        {
            if (_projectId is not null && !string.IsNullOrWhiteSpace(link.TaskId))
            {
                await LoadTaskAsync(_projectId, link.TaskId);
            }
        };
        var unlink = SoftSubtleButton("Unlink");
        unlink.Click += async (_, _) => await UnlinkAsync(link);
        actions.Children.Add(open);
        actions.Children.Add(unlink);
        body.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = body,
        };
    }

    /// <summary>Manual link builder: pick a sibling task and a direction.</summary>
    private StackPanel BuildLinkComposer()
    {
        var host = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        host.Children.Add(Label("Link another task"));

        var candidates = (_context?.Tasks ?? [])
            .Where(t => t.TaskId != _taskId)
            .Where(t => _links is null
                || (_links.WaitingOn.All(l => l.TaskId != t.TaskId)
                    && _links.Feeds.All(l => l.TaskId != t.TaskId)))
            .Take(30)
            .ToList();

        if (candidates.Count == 0)
        {
            host.Children.Add(BodyText("No other unlinked tasks in this project."));
            return host;
        }

        var taskCombo = SoftCombo();
        foreach (var candidate in candidates)
        {
            taskCombo.Items.Add(new ComboBoxItem
            {
                Content = Truncate(candidate.Title, 48),
                Tag = candidate.TaskId,
            });
        }

        taskCombo.SelectedIndex = 0;
        host.Children.Add(taskCombo);

        var directionCombo = SoftCombo();
        directionCombo.Items.Add(new ComboBoxItem { Content = "This task waits for it", Tag = "waits" });
        directionCombo.Items.Add(new ComboBoxItem { Content = "It waits for this task", Tag = "feeds" });
        directionCombo.SelectedIndex = 0;
        host.Children.Add(directionCombo);

        var expectsBox = SoftTextBox("What is it waiting for? (optional, e.g. line count)");
        host.Children.Add(expectsBox);

        var link = SoftAccentButton("Create link");
        link.Click += async (_, _) =>
        {
            if (_taskId is null
                || taskCombo.SelectedItem is not ComboBoxItem { Tag: string otherId }
                || directionCombo.SelectedItem is not ComboBoxItem { Tag: string direction })
            {
                return;
            }

            var thisWaits = string.Equals(direction, "waits", StringComparison.Ordinal);
            var predecessor = thisWaits ? otherId : _taskId;
            var successor = thisWaits ? _taskId : otherId;
            var expects = expectsBox.Text?.Trim();
            var dependencyType = string.IsNullOrWhiteSpace(expects) ? "blocks" : "informs";

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (await client.LinkTasksAsync(predecessor, successor, dependencyType, expects))
            {
                FooterHint.Text = "Tasks linked.";
                await RefreshLinksAsync();
            }
            else
            {
                FooterHint.Text = "Could not link (circular or already linked).";
            }
        };
        host.Children.Add(link);
        return host;
    }

    private async Task UnlinkAsync(TaskLinkVm link)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove link?",
            Content = $"“{Truncate(link.Title, 60)}” will no longer be connected to this task. The task itself is kept.",
            PrimaryButtonText = "Remove link",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UnlinkTasksAsync(link.DependencyId))
        {
            FooterHint.Text = "Link removed.";
            await RefreshLinksAsync();
        }
        else
        {
            FooterHint.Text = "Could not remove link.";
        }
    }

    private async Task DecideRelationshipSuggestionAsync(PendingSuggestionVm suggestion, bool accept)
    {
        if (accept && suggestion.SuggestionType == "merge_into_task")
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Merge this info in?",
                Content = $"{suggestion.Summary}\n\nThis appends to the task notes. Nothing existing is replaced.",
                PrimaryButtonText = "Merge",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        var ok = accept
            ? await client.AcceptSuggestionAsync(suggestion.Id)
            : await client.RejectSuggestionAsync(suggestion.Id);

        FooterHint.Text = ok
            ? accept ? "Applied." : "Dismissed."
            : "Could not update suggestion.";

        if (ok && _projectId is not null)
        {
            // Merges and readiness updates change task content, so reload the whole panel.
            if (_taskId is not null)
            {
                await LoadTaskAsync(_projectId, _taskId);
            }
            else
            {
                BuildContacts();
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RefreshLinksAsync()
    {
        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        await BuildLinksAsync(client);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildContacts()
    {
        ContactsHost.Children.Clear();
        ContactsHost.Children.Add(Label("People on this project"));
        ContactsHost.Children.Add(BodyText(
            "Pulled from email participants and signatures (name, title, company, email, phone). Tap a card for details."));

        _ = BuildContactsAsync();
    }

    private async Task BuildContactsAsync()
    {
        var host = ContactsHost;
        if (host is null)
        {
            return;
        }

        var personIds = new HashSet<string>(
            (_context?.Contacts ?? []).Select(c => c.PersonId).Where(id => id.Length > 0),
            StringComparer.Ordinal);

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var reporting = (await client.GetPendingSuggestionsAsync())
                .Where(s => string.Equals(s.SuggestionType, "reporting_relationship", StringComparison.Ordinal))
                .Where(s => SuggestionTouchesPeople(s, personIds))
                .Take(8)
                .ToList();

            if (reporting.Count > 0)
            {
                host.Children.Add(Label("Org chart — needs confirmation"));
                foreach (var suggestion in reporting)
                {
                    host.Children.Add(BuildSuggestionCard(suggestion));
                }
            }
        }
        catch
        {
            // non-fatal — still show contact list
        }

        var contacts = _context?.Contacts ?? [];
        if (contacts.Count == 0)
        {
            host.Children.Add(BodyText("No linked contacts yet. Drop or ingest email — Orbit enriches people automatically."));
            return;
        }

        var list = new StackPanel { Spacing = 8 };
        foreach (var contact in contacts)
        {
            var personId = contact.PersonId;
            var name = contact.DisplayName;
            var subtitle = string.Join(
                " · ",
                new[] { contact.Title, contact.OrganizationName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (subtitle.Length == 0)
            {
                subtitle = "Tap for email · phone · details";
            }

            var card = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(12),
                Tag = personId,
            };
            card.Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = name,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    },
                },
            };
            card.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(personId))
                {
                    FooterHint.Text = "Contact id missing.";
                    return;
                }

                await ShowContactCardAsync(personId, name);
            };
            list.Children.Add(card);
        }

        host.Children.Add(list);
    }

    private static bool SuggestionTouchesPeople(PendingSuggestionVm suggestion, HashSet<string> personIds)
    {
        if (personIds.Count == 0 || string.IsNullOrWhiteSpace(suggestion.PayloadJson))
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(suggestion.PayloadJson);
            var root = doc.RootElement;
            foreach (var key in new[] { "personId", "reportsToPersonId" })
            {
                if (root.TryGetProperty(key, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && value.GetString() is { Length: > 0 } id
                    && personIds.Contains(id))
                {
                    return true;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        return false;
    }

    private async Task ShowContactCardAsync(string personId, string fallbackName)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var detail = await client.GetContactAsync(personId);
            if (detail is null)
            {
                FooterHint.Text = "Could not load contact.";
                return;
            }

            var email = detail.Methods.FirstOrDefault(m =>
                string.Equals(m.MethodType, "email", StringComparison.OrdinalIgnoreCase))?.Value;
            var phone = detail.Methods.FirstOrDefault(m =>
                    string.Equals(m.MethodType, "mobile", StringComparison.OrdinalIgnoreCase))?.Value
                ?? detail.Methods.FirstOrDefault(m =>
                    string.Equals(m.MethodType, "phone", StringComparison.OrdinalIgnoreCase))?.Value;

            var body = new StackPanel { Spacing = 10, MinWidth = 280 };
            body.Children.Add(new TextBlock
            {
                Text = string.Join(
                    " · ",
                    new[] { detail.Title, detail.OrganizationName }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Opacity = 0.85,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            body.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(email) ? "Email —" : $"Email · {email}",
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            body.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(phone) ? "Phone —" : $"Phone · {phone}",
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            if (detail.Projects.Count > 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Projects · " + string.Join(", ", detail.Projects.Select(p => p.Name)),
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    FontSize = 12,
                });
            }

            var dialog = new ContentDialog
            {
                Title = string.IsNullOrWhiteSpace(detail.DisplayName) ? fallbackName : detail.DisplayName,
                Content = body,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };
            if (!string.IsNullOrWhiteSpace(email))
            {
                dialog.PrimaryButtonText = "Email";
                dialog.PrimaryButtonClick += async (_, _) =>
                    await Launcher.LaunchUriAsync(new Uri($"mailto:{email}"));
            }

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            FooterHint.Text = $"Contact card failed: {ex.Message}";
        }
    }

    private void BuildFiles()
    {
        FilesHost.Children.Clear();
        FilesHost.Children.Add(Label(_taskId is null ? "Project files" : "Files for this task / project"));
        FilesHost.Children.Add(BodyText("Ask Agent for a file, or open indexed project files below."));

        var ask = SoftTextBox("Request a file… e.g. W-9, lease, MetroFiber bill");
        var askBtn = SoftAccentButton("Ask Agent for file");
        askBtn.Click += async (_, _) =>
        {
            var q = ask.Text?.Trim() ?? string.Empty;
            if (q.Length == 0)
            {
                return;
            }

            DetailTabs.SelectedIndex = 1;
            AgentInput.Text = $"Find or link the file for this work: {q}";
            await SendAgentAsync();
        };
        FilesHost.Children.Add(ask);
        FilesHost.Children.Add(askBtn);

        if (_context?.Files.Count > 0)
        {
            foreach (var file in _context.Files.Take(12))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
                row.Children.Add(new TextBlock
                {
                    Text = file.DisplayName,
                    Width = 200,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                });
                var open = SoftSubtleButton("Open");
                open.Tag = file.Id;
                open.Click += async (_, _) =>
                {
                    using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                    await client.OpenFileExternallyAsync(file.Id);
                };
                row.Children.Add(open);
                FilesHost.Children.Add(row);
            }
        }
        else
        {
            FilesHost.Children.Add(BodyText("No indexed files on this project yet."));
        }
    }

    private async Task BuildFieldsAsync(CoreHostClient client)
    {
        var entityType = _taskId is null ? "project" : "task";
        var entityId = _taskId ?? _projectId;
        if (entityId is null)
        {
            FieldsHost.Children.Clear();
            return;
        }

        await CustomFieldsEditor.BuildIntoAsync(
            FieldsHost,
            client,
            entityType,
            entityId,
            hint => FooterHint.Text = hint,
            focusAfterEdit: DetailTabs);
    }

    private void SeedAgentTranscript()
    {
        _agentHistory = [];
        var focus = _task is null
            ? $"Project {_projectName}"
            : $"Task '{_task.Title}' on {_projectName}";
        _agentTranscriptText =
            $"Hermes ready for {focus}.\nI can rewrite the title, update status/notes, request files, link people, or delete this task (delete asks for confirm).\nSay “apply that as the title” after a rewrite — I’ll set it.\nChat is kept if you close and reopen this panel.";
        AgentTranscript.Text = _agentTranscriptText;
    }

    private async void AgentInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(AgentInput.Text))
            {
                AgentInput.Text = string.Empty;
                e.Handled = true;
            }

            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SendAgentAsync();
    }

    private async void AgentSend_Click(object sender, RoutedEventArgs e) => await SendAgentAsync();

    private async Task SendAgentAsync()
    {
        if (_agentBusy)
        {
            return;
        }

        var text = AgentInput.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        AgentInput.Text = string.Empty;
        _agentBusy = true;
        AgentTranscript.Text += $"\n\nYou: {text}\nAgent: …";
        try
        {
            if (LooksLikeDeleteRequest(text) && _taskId is not null)
            {
                AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…')
                    + "I can delete this task from the workbench. Confirm in the dialog.";
                PersistCurrentSession();
                await ConfirmAndDeleteTaskAsync();
                return;
            }

            // Fast path: apply/set title from an explicit phrase or a recent agent proposal.
            var recentProposals = _agentHistory
                .Where(m => m.Role == "assistant")
                .Reverse()
                .Select(m => m.Content ?? string.Empty)
                .Where(c => c.Length > 0)
                .Take(6)
                .ToList();
            if (_taskId is not null
                && WorkbenchAgentActions.TryResolveApplyTitle(text, recentProposals, out var applyTitle))
            {
                var applied = await ApplyTaskMutationAsync(new WorkbenchAgentMutation { Title = applyTitle });
                var msg = applied
                    ? $"Done — set title to:\n{applyTitle}"
                    : "Couldn't update the title (Core Host may be down).";
                AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…') + msg;
                _agentHistory.Add(new HermesChatMessage { Role = "user", Content = text });
                _agentHistory.Add(new HermesChatMessage { Role = "assistant", Content = msg });
                PersistCurrentSession();
                return;
            }

            if (_taskId is not null
                && WorkbenchAgentActions.LooksLikeStatusUpdateRequest(text, out var status)
                && !string.IsNullOrWhiteSpace(status))
            {
                var applied = await ApplyTaskMutationAsync(new WorkbenchAgentMutation { Status = status });
                var msg = applied
                    ? $"Done — status is now {status}."
                    : "Couldn't update status.";
                AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…') + msg;
                _agentHistory.Add(new HermesChatMessage { Role = "user", Content = text });
                _agentHistory.Add(new HermesChatMessage { Role = "assistant", Content = msg });
                PersistCurrentSession();
                return;
            }

            var reply = await CaptureClarifyHermesContinueLooseAsync(text);
            if (WorkbenchAgentActions.TryParseReply(reply, out var mutation, out var display)
                && mutation is not null)
            {
                if (mutation.DeleteTask && _taskId is not null)
                {
                    AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…')
                        + (string.IsNullOrWhiteSpace(display) || display == "Done."
                            ? "I'll delete this task after you confirm."
                            : display);
                    PersistCurrentSession();
                    await ConfirmAndDeleteTaskAsync();
                    return;
                }

                if (mutation.HasLinkRequest && _taskId is not null)
                {
                    var confirm = await ApplyLinkMutationAsync(mutation);
                    var shown = string.IsNullOrWhiteSpace(display) || display == "Done."
                        ? confirm
                        : $"{display}\n{confirm}";
                    AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…') + shown;
                    PersistCurrentSession();
                    return;
                }

                if (mutation.HasTaskUpdate && _taskId is not null)
                {
                    var applied = await ApplyTaskMutationAsync(mutation);
                    var confirm = applied
                        ? BuildAppliedMessage(mutation)
                        : "Couldn't apply that update.";
                    var shown = string.IsNullOrWhiteSpace(display) || display == "Done."
                        ? confirm
                        : $"{display}\n{confirm}";
                    AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…') + shown;
                    PersistCurrentSession();
                    return;
                }
            }

            AgentTranscript.Text = AgentTranscript.Text.TrimEnd('…') + reply;
            PersistCurrentSession();
        }
        catch (Exception ex)
        {
            AgentTranscript.Text += $"\n(error: {ex.Message})";
            PersistCurrentSession();
        }
        finally
        {
            _agentBusy = false;
        }
    }

    private static string BuildAppliedMessage(WorkbenchAgentMutation mutation)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mutation.Title))
        {
            parts.Add($"title → {mutation.Title}");
        }

        if (!string.IsNullOrWhiteSpace(mutation.Status))
        {
            parts.Add($"status → {mutation.Status}");
        }

        if (!string.IsNullOrWhiteSpace(mutation.NextAction))
        {
            parts.Add($"subtitle → {mutation.NextAction}");
        }

        if (!string.IsNullOrWhiteSpace(mutation.Body))
        {
            parts.Add("notes updated");
        }

        return parts.Count == 0 ? "Updated." : "Applied: " + string.Join("; ", parts);
    }

    private async Task<string> ApplyLinkMutationAsync(WorkbenchAgentMutation mutation)
    {
        if (_taskId is null || _context is null || mutation.LinkTaskQuery is null)
        {
            return "Couldn't create that link.";
        }

        var candidates = _context.Tasks
            .Where(t => t.TaskId != _taskId)
            .Select(t => (t.TaskId, t.Title));

        if (!WorkbenchAgentActions.TryResolveLinkTarget(mutation.LinkTaskQuery, candidates, out var otherId))
        {
            return $"I couldn't find exactly one task matching “{mutation.LinkTaskQuery}” — which task did you mean?";
        }

        var thisWaits = string.Equals(mutation.LinkDirection, "waits_for", StringComparison.Ordinal);
        var predecessor = thisWaits ? otherId : _taskId;
        var successor = thisWaits ? _taskId : otherId;
        var dependencyType = string.IsNullOrWhiteSpace(mutation.LinkExpects) ? "blocks" : "informs";

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        var ok = await client.LinkTasksAsync(
            predecessor,
            successor,
            dependencyType,
            mutation.LinkExpects,
            reason: "Linked from task chat");

        if (!ok)
        {
            return "Couldn't create that link — it may already exist or would be circular.";
        }

        await RefreshLinksAsync();
        var otherTitle = _context.Tasks.FirstOrDefault(t => t.TaskId == otherId)?.Title ?? "that task";
        var needs = string.IsNullOrWhiteSpace(mutation.LinkExpects)
            ? string.Empty
            : $" (needs {mutation.LinkExpects})";
        return thisWaits
            ? $"Linked — this task now waits on “{Truncate(otherTitle, 50)}”{needs}. See the Links tab."
            : $"Linked — “{Truncate(otherTitle, 50)}” now waits on this task{needs}. See the Links tab.";
    }

    private async Task<bool> ApplyTaskMutationAsync(WorkbenchAgentMutation mutation)
    {
        if (_taskId is null || !mutation.HasTaskUpdate)
        {
            return false;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.UpdateTaskAsync(
                _taskId,
                title: mutation.Title,
                status: mutation.Status,
                nextAction: mutation.NextAction,
                body: mutation.Body);
            if (!ok)
            {
                return false;
            }

            if (_task is not null)
            {
                if (!string.IsNullOrWhiteSpace(mutation.Title))
                {
                    _task.Title = mutation.Title!;
                    TitleBox.Text = mutation.Title!;
                }

                if (!string.IsNullOrWhiteSpace(mutation.Status))
                {
                    _task.Status = mutation.Status!;
                }

                if (mutation.NextAction is not null)
                {
                    _task.NextAction = mutation.NextAction;
                }

                if (mutation.Body is not null)
                {
                    _task.Body = mutation.Body;
                }
            }
            else if (!string.IsNullOrWhiteSpace(mutation.Title))
            {
                TitleBox.Text = mutation.Title!;
            }

            BuildOverview();
            if (mutation.Body is not null)
            {
                BuildNotes();
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
            FooterHint.Text = "Updated.";
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool LooksLikeDeleteRequest(string text)
    {
        var lower = text.ToLowerInvariant();
        return (lower.Contains("delete") || lower.Contains("remove") || lower.Contains("trash"))
               && (lower.Contains("task") || lower.Contains("this") || lower.Contains("it") || lower.Contains("line"));
    }

    private async Task<string> CaptureClarifyHermesContinueLooseAsync(string userText)
    {
        if (!HermesUrlValidation.TryValidate(App.Settings.HermesBaseUrl, out var url, out _))
        {
            return LocalAgentFallback(userText);
        }

        var key = App.SettingsStore.ReadHermesApiKey(App.Settings);
        using var client = new HermesHttpClient(new Uri(url!), key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var session = await client.EnsureSessionAsync(cancellationToken: cts.Token);
        EnsureAgentSystemPrompt();
        _agentHistory.Add(new HermesChatMessage { Role = "user", Content = userText });
        var buffer = new System.Text.StringBuilder();
        await foreach (var delta in client.StreamChatAsync(
                           new HermesChatRequest
                           {
                               SessionId = session.SessionId,
                               SessionKey = session.SessionKey,
                               Stream = true,
                               Messages = _agentHistory.ToList(),
                           },
                           cts.Token))
        {
            if (delta.Kind == HermesChatDeltaKind.Error)
            {
                return LocalAgentFallback(userText);
            }

            if (delta.Kind == HermesChatDeltaKind.Content && !string.IsNullOrEmpty(delta.Text))
            {
                buffer.Append(delta.Text);
            }

            if (delta.Kind == HermesChatDeltaKind.Done)
            {
                break;
            }
        }

        var reply = buffer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(reply))
        {
            return LocalAgentFallback(userText);
        }

        _agentHistory.Add(new HermesChatMessage { Role = "assistant", Content = reply });
        return reply;
    }

    private void EnsureAgentSystemPrompt()
    {
        var prompt =
            $"""
            You are Orbit's workbench agent with FULL control of this task/project via app tokens.
            Project: {_projectName}
            ProjectId: {_projectId ?? "n/a"}
            Task: {_task?.Title ?? "(project overview)"}
            TaskId: {_taskId ?? "n/a"}
            Status: {_task?.Status ?? "n/a"}
            Next: {_task?.NextAction ?? "(none)"}

            When the user wants a change, DO IT — do not only suggest. Emit a short spoken line, then the token block.
            Update this task:
            ORBIT_UPDATE_TASK
            TITLE: <short title under 80 chars>
            STATUS: <active|blocked|waiting|not_started>   (optional)
            NEXT: <one-line subtitle>                     (optional)
            BODY: <notes/summary>                         (optional)

            Delete this task:
            ORBIT_DELETE_TASK

            Link this task to another task in the project:
            ORBIT_LINK_TASK
            DIRECTION: <waits_for|feeds>
            TASK: <exact title of the other task>
            EXPECTS: <what the waiting task needs, e.g. line count>   (optional)

            Other open tasks in this project:
            {SiblingTaskLines()}

            Current links: {LinkSummaryLine()}

            Rules:
            - If they say apply/set/use that as the title, emit ORBIT_UPDATE_TASK with TITLE set to your last proposal (or their explicit title). Never ask "what title?" when a proposal already exists.
            - TITLE is the work item name only — never paste the chat transcript into TITLE.
            - Two tasks are contingent when one produces information or a decision the other needs. Use DIRECTION: waits_for when THIS task needs something from the other; feeds when the other needs something from this one. Set EXPECTS to the specific thing being waited on.
            - Only use TASK: titles from the list above. If none match, say so instead of guessing.
            - No markdown fences. Be concise (2-5 short lines plus the token block).
            """;

        if (_agentHistory.Count > 0 && _agentHistory[0].Role == "system")
        {
            _agentHistory[0] = new HermesChatMessage { Role = "system", Content = prompt };
        }
        else
        {
            _agentHistory.Insert(0, new HermesChatMessage { Role = "system", Content = prompt });
        }
    }

    private string SiblingTaskLines()
    {
        var siblings = (_context?.Tasks ?? [])
            .Where(t => t.TaskId != _taskId)
            .Take(15)
            .Select(t => $"- {t.Title} [{t.Status}]")
            .ToList();
        return siblings.Count == 0 ? "(none)" : string.Join("\n", siblings);
    }

    private string LinkSummaryLine()
    {
        if (_links is null || _links.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        if (_links.WaitingOn.Count > 0)
        {
            parts.Add("waiting on " + string.Join(", ", _links.WaitingOn.Select(l => $"“{Truncate(l.Title, 40)}”")));
        }

        if (_links.Feeds.Count > 0)
        {
            parts.Add("feeds " + string.Join(", ", _links.Feeds.Select(l => $"“{Truncate(l.Title, 40)}”")));
        }

        return string.Join(" · ", parts);
    }

    private string LocalAgentFallback(string q)
    {
        if (q.Contains("file", StringComparison.OrdinalIgnoreCase))
        {
            return "Point me at the folder or say the document name — then use Files → Ask Agent after indexing.";
        }

        if (LooksLikeDeleteRequest(q))
        {
            return "I can delete this task — confirm in the dialog.\nORBIT_DELETE_TASK";
        }

        var recent = _agentHistory
            .Where(m => m.Role == "assistant")
            .Reverse()
            .Select(m => m.Content ?? string.Empty)
            .Where(c => c.Length > 0)
            .Take(6)
            .ToList();
        if (WorkbenchAgentActions.TryResolveApplyTitle(q, recent, out var title))
        {
            return $"Setting title.\nORBIT_UPDATE_TASK\nTITLE: {title}";
        }

        if (WorkbenchAgentActions.LooksLikeStatusUpdateRequest(q, out var status) && status is not null)
        {
            return $"Updating status.\nORBIT_UPDATE_TASK\nSTATUS: {status}";
        }

        if (WorkbenchAgentActions.LooksLikeTitleUpdateRequest(q))
        {
            return "Tell me the title to use, or ask me to rewrite first and then say “apply that as the title”.";
        }

        return CaptureAgentNudge.Format(CaptureAgentNudge.BuildLocal(q, "this task"));
    }

    private async void DeleteTask_Click(object sender, RoutedEventArgs e) => await ConfirmAndDeleteTaskAsync();

    private async Task ConfirmAndDeleteTaskAsync()
    {
        if (_taskId is null)
        {
            return;
        }

        var title = _task?.Title ?? "this task";
        var confirm = new ContentDialog
        {
            Title = "Delete task?",
            Content = $"Remove “{title}” from the workbench? This archives the task (soft delete).",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            FooterHint.Text = "Delete cancelled.";
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.ArchiveEntityAsync("task", _taskId))
        {
            if (!string.IsNullOrWhiteSpace(_sessionKey))
            {
                Sessions.TryRemove(_sessionKey, out _);
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            FooterHint.Text = "Delete failed.";
        }
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (_limboNote is not null)
        {
            await ConfirmAndArchiveLimboAsync();
            return;
        }

        await ConfirmAndDeleteTaskAsync();
    }

    private async Task ConfirmAndArchiveLimboAsync()
    {
        if (_limboNote is null)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Archive limbo note?",
            Content = "Remove this capture from Limbo?",
            PrimaryButtonText = "Archive",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.ArchiveEntityAsync("note", _limboNote.Id))
        {
            if (!string.IsNullOrWhiteSpace(_sessionKey))
            {
                Sessions.TryRemove(_sessionKey, out _);
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            FooterHint.Text = "Archive failed.";
        }
    }

    private async void ArchiveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is null)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Archive project?",
            Content = $"Archive {_projectName} and its open tasks/notes from the workbench?",
            PrimaryButtonText = "Archive project",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.ArchiveEntityAsync("project", _projectId))
        {
            foreach (var key in Sessions.Keys.Where(k =>
                         k == $"project:{_projectId}" || (_context?.Tasks.Any(t => k == $"task:{t.TaskId}") ?? false)).ToList())
            {
                Sessions.TryRemove(key, out _);
            }

            ContentChanged?.Invoke(this, EventArgs.Empty);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            FooterHint.Text = "Archive project failed.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        PersistCurrentSession();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static TextBlock BodyText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.9,
        TextWrapping = TextWrapping.WrapWholeWords,
        LineHeight = 20,
    };

    private static TextBox SoftTextBox(string placeholder, double minHeight = 0, bool acceptsReturn = false)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            AcceptsReturn = acceptsReturn,
            TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Tag = string.Empty,
        };
        if (minHeight > 0)
        {
            box.MinHeight = minHeight;
        }

        try
        {
            box.Background = (Brush)Application.Current.Resources["OrbitFieldFillBrush"];
        }
        catch (Exception)
        {
            // theme resource optional
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape)
            {
                return;
            }

            var baseline = box.Tag as string ?? string.Empty;
            if (string.Equals(box.Text ?? string.Empty, baseline, StringComparison.Ordinal))
            {
                return;
            }

            box.Text = baseline;
            e.Handled = true;
        };

        return box;
    }

    private static ComboBox SoftCombo()
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
        };
        try
        {
            combo.Background = (Brush)Application.Current.Resources["OrbitFieldFillBrush"];
        }
        catch (Exception)
        {
            // theme resource optional
        }

        return combo;
    }

    private static Button SoftAccentButton(string content) =>
        new()
        {
            Content = content,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 8, 16, 8),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };

    private static Button SoftSubtleButton(string content) =>
        new()
        {
            Content = content,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8, 14, 8),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
