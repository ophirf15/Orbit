using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orbit_App.Services;
using Orbit_App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Orbit_App.Controls;

public sealed partial class ProjectWorkspacePanel : UserControl
{
    public event EventHandler? CloseRequested;

    public event EventHandler? ContentChanged;

    public event EventHandler<string>? TaskOpenRequested;

    public event EventHandler<string>? TaskCompleteRequested;

    public event EventHandler<string>? TaskArchiveRequested;

    private string? _projectId;
    private ProjectContextVm? _context;
    private static readonly TimeSpan DueUrgentWindow = TimeSpan.FromDays(7);

    public ProjectWorkspacePanel()
    {
        InitializeComponent();
    }

    public string? ProjectId => _projectId;

    public async Task LoadProjectAsync(string projectId)
    {
        _projectId = projectId;
        FooterHint.Text = "Loading…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            _context = await client.GetProjectContextAsync(projectId);
            if (_context is null)
            {
                FooterHint.Text = "Could not load project.";
                return;
            }

            TitleBox.Text = _context.Name;
            SummaryBox.Text = _context.Summary ?? string.Empty;
            SummaryBox.Tag = SummaryBox.Text;
            TitleBox.Tag = TitleBox.Text;
            CodeBox.Text = _context.Code ?? string.Empty;
            CodeBox.Tag = CodeBox.Text;
            BindDossierFields();
            BuildAliases();

            BuildDueStrip();
            await BuildDepsAsync(client);
            BuildMatrix();
            BuildNotes();
            await BuildFieldsAsync(client);
            FooterHint.Text = "Project workspace";
        }
        catch (Exception)
        {
            FooterHint.Text = "Load failed.";
        }
    }

    private IEnumerable<CellLineVm> OpenTasks()
    {
        if (_context is null)
        {
            return [];
        }

        return _context.Tasks.Where(t =>
            !string.Equals(t.Status, "complete", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t.Status, "archived", StringComparison.OrdinalIgnoreCase));
    }

    private void BuildDueStrip()
    {
        DueStripHost.Children.Clear();
        var dueTasks = OpenTasks()
            .Where(t => !string.IsNullOrWhiteSpace(t.DueAt))
            .Select(t => (Task: t, Due: DateTimeOffset.TryParse(t.DueAt, out var d) ? d : (DateTimeOffset?)null))
            .Where(x => x.Due is not null)
            .OrderBy(x => x.Due)
            .Take(12)
            .ToList();

        if (dueTasks.Count == 0)
        {
            DueStripHost.Children.Add(new TextBlock
            {
                Text = "No due dates set.",
                Opacity = 0.6,
            });
            return;
        }

        foreach (var (task, due) in dueTasks)
        {
            var chip = MakeChip(task.Title, FormatDue(due!.Value), task.TaskId);
            DueStripHost.Children.Add(chip);
        }
    }

    private async Task BuildDepsAsync(CoreHostClient client)
    {
        DepsHost.Children.Clear();
        var edges = new List<string>();
        foreach (var task in OpenTasks().Take(12))
        {
            var links = await client.GetTaskDependenciesAsync(task.TaskId);
            foreach (var w in links.WaitingOn.Take(3))
            {
                edges.Add($"{task.Title} waits on {w.Title}");
            }

            foreach (var f in links.Feeds.Take(2))
            {
                edges.Add($"{task.Title} feeds {f.Title}");
            }

            if (edges.Count >= 8)
            {
                break;
            }
        }

        if (edges.Count == 0)
        {
            DepsHost.Children.Add(new TextBlock
            {
                Text = "No task dependencies yet.",
                Opacity = 0.55,
            });
            return;
        }

        foreach (var line in edges.Distinct(StringComparer.Ordinal).Take(8))
        {
            DepsHost.Children.Add(new TextBlock
            {
                Text = line,
                Opacity = 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
    }

    private void BuildMatrix()
    {
        ClearQuadrant(QuadDoHost, "do");
        ClearQuadrant(QuadScheduleHost, "schedule");
        ClearQuadrant(QuadDelegateHost, "delegate");
        ClearQuadrant(QuadDeferHost, "defer");

        var now = DateTimeOffset.UtcNow;
        foreach (var task in OpenTasks())
        {
            var important = task.IsImportant;
            var urgent = task.IsUrgentEffective(now, DueUrgentWindow);
            var host = (important, urgent) switch
            {
                (true, true) => QuadDoHost,
                (true, false) => QuadScheduleHost,
                (false, true) => QuadDelegateHost,
                _ => QuadDeferHost,
            };
            host.Children.Insert(host.Children.Count - 1, MakeTaskCard(task));
        }
    }

    private void ClearQuadrant(StackPanel host, string tag)
    {
        host.Children.Clear();
        host.Tag = tag;
        host.AllowDrop = true;
        host.DragOver -= Quadrant_DragOver;
        host.Drop -= Quadrant_Drop;
        host.DragOver += Quadrant_DragOver;
        host.Drop += Quadrant_Drop;

        var add = new Button
        {
            Content = "+ New",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = tag,
            Margin = new Thickness(0, 4, 0, 0),
            AllowDrop = true,
        };
        add.DragOver += Quadrant_DragOver;
        add.Drop += Quadrant_Drop;
        add.Click += AddInQuadrant_Click;
        host.Children.Add(add);
    }

    private FrameworkElement MakeTaskCard(CellLineVm task)
    {
        var subtitle = string.IsNullOrWhiteSpace(task.NextAction)
            ? task.StatusLabel
            : $"{task.StatusLabel} · {task.NextAction}";
        if (!string.IsNullOrWhiteSpace(task.DueAt) && DateTimeOffset.TryParse(task.DueAt, out var due))
        {
            subtitle = $"{subtitle} · due {FormatDue(due)}";
        }

        // Border (not Button) so click does not steal the drag gesture.
        var card = new Border
        {
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Tag = task.TaskId,
            CanDrag = true,
            Background = TryBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = TryBrush("CardStrokeColorDefaultBrush"),
        };
        card.Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = task.Title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = subtitle,
                    Opacity = 0.7,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        card.Tapped += (_, _) => TaskOpenRequested?.Invoke(this, task.TaskId);
        card.DragStarting += TaskCard_DragStarting;
        card.ContextRequested += (s, args) => ShowCardMenu(task, s as UIElement ?? card, args);
        return card;
    }

    private Button MakeChip(string title, string subtitle, string taskId)
    {
        var chip = new Button
        {
            MinWidth = 140,
            MaxWidth = 220,
            Padding = new Thickness(10, 8, 10, 8),
            Tag = taskId,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        chip.Content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = subtitle,
                    Opacity = 0.7,
                },
            },
        };
        chip.Click += (_, _) =>
        {
            if (chip.Tag is string id)
            {
                TaskOpenRequested?.Invoke(this, id);
            }
        };
        var stub = new CellLineVm { TaskId = taskId, Title = title };
        chip.ContextRequested += (s, args) => ShowCardMenu(stub, s as UIElement ?? chip, args);
        return chip;
    }

    private void ShowCardMenu(CellLineVm task, UIElement target, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        var menu = new MenuFlyout();
        void Add(string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Open", () => TaskOpenRequested?.Invoke(this, task.TaskId));
        Add("Mark complete", () => TaskCompleteRequested?.Invoke(this, task.TaskId));
        var move = new MenuFlyoutSubItem { Text = "Move to…" };
        void Quad(string name, string tag)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += async (_, _) => await MoveToQuadrantAsync(task.TaskId, tag);
            move.Items.Add(item);
        }

        Quad("Do first", "do");
        Quad("Schedule", "schedule");
        Quad("Delegate", "delegate");
        Quad("Defer", "defer");
        menu.Items.Add(move);
        Add("Archive", () => TaskArchiveRequested?.Invoke(this, task.TaskId));

        if (args.TryGetPosition(target, out var point))
        {
            menu.ShowAt(target, point);
        }
        else if (target is FrameworkElement fe)
        {
            menu.ShowAt(fe);
        }

        args.Handled = true;
    }

    private async Task MoveToQuadrantAsync(string taskId, string quad)
    {
        if (_projectId is null)
        {
            return;
        }

        var (priority, urgency) = quad switch
        {
            "do" => (1, 1),
            "schedule" => (1, 0),
            "delegate" => (0, 1),
            _ => (0, 0),
        };
        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateTaskAsync(taskId, priority: priority, urgency: urgency))
        {
            FooterHint.Text = "Moved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
            await LoadProjectAsync(_projectId);
        }
    }

    private void TaskCard_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string taskId } || string.IsNullOrWhiteSpace(taskId))
        {
            args.Cancel = true;
            return;
        }

        args.AllowedOperations = DataPackageOperation.Move;
        args.Data.SetText(taskId);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Quadrant_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = "Move here";
        e.Handled = true;
    }

    private async void Quadrant_Drop(object sender, DragEventArgs e)
    {
        var quad = ResolveQuadrantTag(sender);
        if (quad is null)
        {
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        e.Handled = true;
        var taskId = await e.DataView.GetTextAsync();
        if (string.IsNullOrWhiteSpace(taskId) || _projectId is null)
        {
            return;
        }

        await MoveToQuadrantAsync(taskId, quad);
    }

    private static string? ResolveQuadrantTag(object sender)
    {
        for (var d = sender as DependencyObject; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { Tag: string tag }
                && tag is "do" or "schedule" or "delegate" or "defer")
            {
                return tag;
            }
        }

        return null;
    }

    private async void AddInQuadrant_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is null || sender is not Button { Tag: string quad } || XamlRoot is null)
        {
            return;
        }

        var result = await TaskCapturePrompt.ShowAsync(
            XamlRoot,
            defaultProjectId: _projectId,
            dialogTitle: "Capture preview",
            showProjectPicker: false,
            allowLimbo: false);
        if (result is null || string.IsNullOrWhiteSpace(result.Title))
        {
            return;
        }

        var (priority, urgency) = quad switch
        {
            "do" => (1, 1),
            "schedule" => (1, 0),
            "delegate" => (0, 1),
            _ => (0, 0),
        };

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        var captureText = TaskCapturePrompt.CaptureTextForUpdateMatch(result);
        var choice = await CaptureNoteOrUpdatePrompt.ResolveAsync(
            XamlRoot,
            client,
            captureText,
            _projectId);
        if (choice.Cancelled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(choice.UpdateTaskId))
        {
            var ok = await CaptureNoteOrUpdatePrompt.AppendCaptureUpdateAsync(
                client,
                choice.UpdateTaskId!,
                captureText,
                currentBody: null);
            FooterHint.Text = ok
                ? $"Updated “{choice.UpdateTaskTitle ?? "task"}”."
                : "Could not update task.";
            if (ok)
            {
                ContentChanged?.Invoke(this, EventArgs.Empty);
                await LoadProjectAsync(_projectId);
            }

            return;
        }

        var created = await client.CreateTaskAsync(
            result.Title,
            _projectId,
            nextAction: string.IsNullOrWhiteSpace(result.NextAction) ? null : result.NextAction,
            body: result.Brief,
            status: "not_started",
            sourceKind: result.Source,
            sourceMatchReason: result.ProjectMatchReason);
        if (created is null)
        {
            FooterHint.Text = "Could not create task.";
            return;
        }

        await client.UpdateTaskAsync(
            created.Value.Id,
            priority: priority,
            urgency: urgency,
            dueAt: IsIsoDue(result.DueAt) ? result.DueAt : null);
        FooterHint.Text = "Task added.";
        ContentChanged?.Invoke(this, EventArgs.Empty);
        await LoadProjectAsync(_projectId);
    }

    private static bool IsIsoDue(string? dueAt) =>
        !string.IsNullOrWhiteSpace(dueAt)
        && dueAt.Length >= 8
        && dueAt.Contains('-', StringComparison.Ordinal)
        && !dueAt.StartsWith("by ", StringComparison.OrdinalIgnoreCase);

    private void BuildNotes()
    {
        NotesHost.Children.Clear();
        if (_context is null || _context.Notes.Count == 0)
        {
            NotesHost.Children.Add(new TextBlock
            {
                Text = "No notes yet.",
                Opacity = 0.55,
            });
        }
        else
        {
            foreach (var note in _context.Notes.Take(6))
            {
                NotesHost.Children.Add(new TextBlock
                {
                    Text = note.Text,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Opacity = 0.9,
                });
            }
        }

        var addBox = new TextBox
        {
            PlaceholderText = "Add a note…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            CornerRadius = new CornerRadius(10),
        };
        var addBtn = new Button { Content = "Add note", Margin = new Thickness(0, 4, 0, 0) };
        addBtn.Click += async (_, _) =>
        {
            var text = addBox.Text?.Trim() ?? string.Empty;
            if (text.Length == 0 || _projectId is null)
            {
                return;
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (await client.CreateNoteAsync(text, _projectId) is not null)
            {
                addBox.Text = string.Empty;
                FooterHint.Text = "Note added.";
                ContentChanged?.Invoke(this, EventArgs.Empty);
                await LoadProjectAsync(_projectId);
            }
            else
            {
                FooterHint.Text = "Note failed.";
            }
        };
        NotesHost.Children.Add(addBox);
        NotesHost.Children.Add(addBtn);
    }

    private async Task BuildFieldsAsync(CoreHostClient client)
    {
        if (_projectId is null)
        {
            FieldsHost.Children.Clear();
            return;
        }

        await CustomFieldsEditor.BuildIntoAsync(
            FieldsHost,
            client,
            "project",
            _projectId,
            hint => FooterHint.Text = hint,
            onChanged: () =>
            {
                ContentChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            });
    }

    private async void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_projectId is null)
        {
            return;
        }

        var name = TitleBox.Text?.Trim() ?? string.Empty;
        var baseline = TitleBox.Tag as string ?? string.Empty;
        if (name.Length == 0 || string.Equals(name, baseline, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateProjectAsync(_projectId, name: name))
        {
            TitleBox.Tag = name;
            if (_context is not null)
            {
                _context.Name = name;
            }

            FooterHint.Text = "Name saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void SummaryBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_projectId is null)
        {
            return;
        }

        var summary = SummaryBox.Text?.Trim() ?? string.Empty;
        var baseline = SummaryBox.Tag as string ?? string.Empty;
        if (string.Equals(summary, baseline, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateProjectAsync(_projectId, summary: summary))
        {
            SummaryBox.Tag = summary;
            if (_context is not null)
            {
                _context.Summary = summary;
            }

            FooterHint.Text = "Summary saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void CodeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_projectId is null)
        {
            return;
        }

        var code = CodeBox.Text?.Trim() ?? string.Empty;
        var baseline = CodeBox.Tag as string ?? string.Empty;
        if (string.Equals(code, baseline, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        var ok = string.IsNullOrEmpty(code)
            ? await client.UpdateProjectAsync(_projectId, clearCode: true)
            : await client.UpdateProjectAsync(_projectId, code: code);
        if (ok)
        {
            CodeBox.Tag = code;
            if (_context is not null)
            {
                _context.Code = string.IsNullOrEmpty(code) ? null : code;
            }

            FooterHint.Text = "Code saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BindDossierFields()
    {
        var d = _context?.Dossier;
        DossierEmptyHint.Visibility = _context?.DossierEmpty == true ? Visibility.Visible : Visibility.Collapsed;
        SetTaggedBox(DossierAddressBox, d?.Address);
        SetTaggedBox(DossierOwnerBox, d?.OwnerClient);
        SetTaggedBox(DossierPhaseBox, d?.Phase);
        SetTaggedBox(DossierPortfolioBox, d?.Portfolio);
        SetTaggedBox(DossierPrioritiesBox, d?.CurrentPriorities is { Count: > 0 }
            ? string.Join(", ", d.CurrentPriorities)
            : null);
    }

    private static void SetTaggedBox(TextBox box, string? value)
    {
        box.Text = value ?? string.Empty;
        box.Tag = box.Text;
    }

    private async void DossierField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_projectId is null || sender is not TextBox box)
        {
            return;
        }

        var value = box.Text?.Trim() ?? string.Empty;
        var baseline = box.Tag as string ?? string.Empty;
        if (string.Equals(value, baseline, StringComparison.Ordinal))
        {
            return;
        }

        var dossier = new Dictionary<string, object?>();
        if (ReferenceEquals(box, DossierAddressBox))
        {
            dossier["address"] = value;
        }
        else if (ReferenceEquals(box, DossierOwnerBox))
        {
            dossier["ownerClient"] = value;
        }
        else if (ReferenceEquals(box, DossierPhaseBox))
        {
            dossier["phase"] = value;
        }
        else if (ReferenceEquals(box, DossierPortfolioBox))
        {
            dossier["portfolio"] = value;
        }
        else if (ReferenceEquals(box, DossierPrioritiesBox))
        {
            dossier["currentPriorities"] = value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }
        else
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateProjectAsync(_projectId, dossier: dossier))
        {
            box.Tag = value;
            if (_context is not null)
            {
                _context.Dossier ??= new ProjectDossierVm();
                if (ReferenceEquals(box, DossierAddressBox))
                {
                    _context.Dossier.Address = string.IsNullOrEmpty(value) ? null : value;
                }
                else if (ReferenceEquals(box, DossierOwnerBox))
                {
                    _context.Dossier.OwnerClient = string.IsNullOrEmpty(value) ? null : value;
                }
                else if (ReferenceEquals(box, DossierPhaseBox))
                {
                    _context.Dossier.Phase = string.IsNullOrEmpty(value) ? null : value;
                }
                else if (ReferenceEquals(box, DossierPortfolioBox))
                {
                    _context.Dossier.Portfolio = string.IsNullOrEmpty(value) ? null : value;
                }
                else if (ReferenceEquals(box, DossierPrioritiesBox))
                {
                    _context.Dossier.CurrentPriorities = value
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .ToList();
                }

                _context.DossierEmpty = string.IsNullOrWhiteSpace(_context.Dossier.Address)
                    && string.IsNullOrWhiteSpace(_context.Dossier.OwnerClient)
                    && string.IsNullOrWhiteSpace(_context.Dossier.Phase)
                    && string.IsNullOrWhiteSpace(_context.Dossier.Portfolio)
                    && _context.Dossier.CurrentPriorities.Count == 0;
                DossierEmptyHint.Visibility = _context.DossierEmpty ? Visibility.Visible : Visibility.Collapsed;
            }

            FooterHint.Text = "Dossier saved.";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BuildAliases()
    {
        AliasesHost.Children.Clear();
        if (_context is null || _context.Aliases.Count == 0)
        {
            AliasesHost.Children.Add(new TextBlock
            {
                Text = "No aliases yet.",
                Opacity = 0.55,
            });
            return;
        }

        foreach (var alias in _context.Aliases)
        {
            var chip = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 4, 4),
                Tag = alias.Id,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = alias.Alias, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button
            {
                Content = "✕",
                Tag = alias.Id,
                Padding = new Thickness(6, 2, 6, 2),
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            };
            remove.Click += RemoveAlias_Click;
            row.Children.Add(remove);
            chip.Child = row;
            AliasesHost.Children.Add(chip);
        }
    }

    private async void AddAlias_Click(object sender, RoutedEventArgs e) => await AddAliasFromInputAsync();

    private async void AliasInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await AddAliasFromInputAsync();
        }
    }

    private async Task AddAliasFromInputAsync()
    {
        if (_projectId is null)
        {
            return;
        }

        var text = AliasInputBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        var created = await client.AddProjectAliasAsync(_projectId, text);
        if (created is null)
        {
            FooterHint.Text = "Could not add alias (duplicate or invalid).";
            return;
        }

        AliasInputBox.Text = string.Empty;
        _context ??= new ProjectContextVm { Id = _projectId };
        _context.Aliases.Add(created);
        BuildAliases();
        FooterHint.Text = "Alias added.";
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void RemoveAlias_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is null || sender is not FrameworkElement { Tag: string aliasId })
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (!await client.RemoveProjectAliasAsync(_projectId, aliasId))
        {
            FooterHint.Text = "Could not remove alias.";
            return;
        }

        if (_context is not null)
        {
            for (var i = _context.Aliases.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_context.Aliases[i].Id, aliasId, StringComparison.Ordinal))
                {
                    _context.Aliases.RemoveAt(i);
                }
            }
        }

        BuildAliases();
        FooterHint.Text = "Alias removed.";
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Archive project?",
            Content = "This archives the project. Tasks stay in history.",
            PrimaryButtonText = "Archive",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.ArchiveEntityAsync("project", _projectId))
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            FooterHint.Text = "Archive failed.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private static Brush? TryBrush(string key)
    {
        try
        {
            return Application.Current.Resources[key] as Brush;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatDue(DateTimeOffset due)
    {
        var local = due.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date < today)
        {
            return "overdue " + local.ToString("MMM d");
        }

        if (local.Date == today)
        {
            return "today";
        }

        if (local.Date == today.AddDays(1))
        {
            return "tomorrow";
        }

        return local.ToString("MMM d");
    }
}
