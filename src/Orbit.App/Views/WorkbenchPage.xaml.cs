using System.Collections;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Core.Agent;
using Orbit.Core.Data;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Hermes;
using Orbit_App.Controls;
using Orbit_App.Services;
using Orbit_App.Shell;
using Orbit_App.ViewModels;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Orbit_App.Views;

public sealed partial class WorkbenchPage : Page
{
    private readonly HashSet<ProjectCellControl> _wiredCells = [];
    private readonly Dictionary<string, CaptureClarifySession> _clarifySessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoreHostClient.EmailIngestResult> _limboEmailByNoteId =
        new(StringComparer.Ordinal);
    private readonly ObservableCollection<WorkbenchAgentBubbleVm> _agentBubbles = [];
    private readonly List<HermesChatMessage> _agentHermesHistory = [];
    private bool _agentBusy;
    private string _hermesStatusLabel = "Hermes: checking…";
    private string? _hermesStatusTooltip;
    private string? _drawerProjectId;
    private string? _drawerTaskId;
    private Flyout? _detailFlyout;
    private WorkbenchDetailPanel? _detailPanel;
    private ProjectWorkspacePanel? _workspacePanel;
    private CompletedTasksPanel? _completedPanel;
    private bool _allowDetailFlyoutClose;
    private string? _scopeProjectId;
    private string? _scopeProjectName;
    private bool _cellSizeComboReady;
    private DispatcherTimer? _pulsePollTimer;
    private string? _pendingConcernTaskId;
    private bool _pulseBusy;
    private OrbitEventListener? _eventListener;
    private DateTimeOffset _lastPulseReloadUtc = DateTimeOffset.MinValue;
    private readonly ObservableCollection<OrbitTreeNodeVm> _treeRoots = [];
    private OrbitTreeNodeVm? _selectedNode;
    private readonly Dictionary<string, OrbitTreeNodeVm> _nodesById = new(StringComparer.Ordinal);
    private OrbitTreeNodeVm? _treeDragNode;

    private sealed class CaptureClarifySession
    {
        public required string ProjectId { get; init; }

        public required string ProjectName { get; init; }

        public required string TaskId { get; init; }

        public required string OriginalCapture { get; init; }

        public List<string> UserReplies { get; } = [];

        public List<HermesChatMessage> HermesHistory { get; } = [];
    }

    private sealed record LimboAssignTarget(string NoteId, string ProjectId, string ProjectName);

    public WorkbenchPage()
    {
        InitializeComponent();
        AgentMessageList.ItemsSource = _agentBubbles;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += WorkbenchPage_KeyDown;
        CellScroller.SizeChanged += CellScroller_SizeChanged;
        DetailFrame.Navigated += DetailFrame_Navigated;
    }

    public void ShowHome()
    {
        DetailFrame.BackStack.Clear();
        DetailFrame.Content = null;
        DetailFrame.Visibility = Visibility.Collapsed;
        WorkbenchRoot.Visibility = Visibility.Visible;
    }

    public void OpenConcernBrief(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        // Prefer selecting in the tree + inline detail (no full-page hop).
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await SelectTaskInTreeAsync(taskId.Trim());
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string taskId && !string.IsNullOrWhiteSpace(taskId))
        {
            _pendingConcernTaskId = taskId.Trim();
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                if (!string.IsNullOrWhiteSpace(_pendingConcernTaskId))
                {
                    await SelectTaskInTreeAsync(_pendingConcernTaskId);
                    _pendingConcernTaskId = null;
                }
            });
        }
    }

    private void DetailFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (DetailFrame.Content is ConcernBriefPage)
        {
            WorkbenchRoot.Visibility = Visibility.Collapsed;
            DetailFrame.Visibility = Visibility.Visible;
        }
        else
        {
            ShowHome();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPulsePolling();
        _ = StopEventListenerAsync();
    }

    private async Task StopEventListenerAsync()
    {
        if (_eventListener is null)
        {
            return;
        }

        var listener = _eventListener;
        _eventListener = null;
        await listener.DisposeAsync();
    }

    public void FocusLimboCapture()
    {
        AgentInputBox.Focus(FocusState.Programmatic);
    }

    private double BoardViewportHeight =>
        CellScroller.ActualHeight > 100 ? CellScroller.ActualHeight : 600;

    private void WorkbenchRoot_DragOver(object sender, DragEventArgs e)
    {
        if (_scopeProjectId is null && FolderDropHelper.LooksLikeFolderDrop(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = "Create project from folder";
            e.DragUIOverride.IsGlyphVisible = true;
            return;
        }

        MsgDropHelper.AcceptMsgDrag(e);
        if (_scopeProjectId is null)
        {
            e.DragUIOverride.Caption = "Ingest email";
        }
    }

    private async void WorkbenchRoot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_scopeProjectId is null)
        {
            var folderPath = await FolderDropHelper.TryGetFolderPathAsync(e.DataView);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await CreateProjectFromFolderDropAsync(folderPath);
                return;
            }
        }

        await HandleEmailDropAsync(e);
    }

    private async Task CreateProjectFromFolderDropAsync(string folderPath)
    {
        try
        {
            WorkbenchHint.Text = $"Creating project from {Path.GetFileName(folderPath.TrimEnd('\\', '/'))}…";
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.CreateProjectFromFolderAsync(folderPath);
            if (result?.Project is null)
            {
                WorkbenchHint.Text = "Could not create project from folder.";
                return;
            }

            WorkbenchHint.Text = result.Home is null
                ? $"Created {result.Project.Name} (home folder not set)."
                : $"Created {result.Project.Name} · home set · indexed {result.Home.IndexedCount} files.";
            await ReloadWorkbenchAsync();
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Folder project failed: {ex.Message}";
        }
    }

    /// <summary>Ingest a dropped .msg, capture into the scoped project (or limbo), clarify via agent, and link the thread.</summary>
    public async Task HandleEmailDropAsync(DragEventArgs e)
    {
        var payload = await MsgDropHelper.TryGetMsgAsync(e.DataView);
        await HandleEmailPayloadAsync(payload);
    }

    public async Task HandleEmailPayloadAsync(MsgDropHelper.MsgDropPayload? payload)
    {
        try
        {
            if (payload is null)
            {
                WorkbenchHint.Text =
                    "Could not read that drop (Outlook OLE often blocks paths). Save As .msg, then drag from Desktop.";
                return;
            }

            WorkbenchHint.Text = "Ingesting email…";
            IReadOnlyList<string>? projectIds = string.IsNullOrWhiteSpace(_scopeProjectId)
                ? null
                : [_scopeProjectId];
            var (email, ingestError) = await EmailIngestUi.TryIngestAsync(
                App.Settings,
                App.SettingsStore,
                payload,
                projectIds);
            if (email is null || string.IsNullOrWhiteSpace(email.Id))
            {
                WorkbenchHint.Text = ingestError ?? "Email ingest failed. Is Core Host running?";
                return;
            }

            var captureText = EmailIngestUi.BuildCaptureText(email);
            var projectId = _scopeProjectId;
            var projectName = _scopeProjectName;
            if (string.IsNullOrWhiteSpace(projectId)
                && email.ProjectIds.Count > 0
                && EnumerateCellVms().Any(c => c.Id == email.ProjectIds[0]))
            {
                projectId = email.ProjectIds[0];
                projectName = EnumerateCellVms().First(c => c.Id == projectId).Name;
            }

            await CaptureAsync(captureText, projectId, projectName, sourceCell: null, linkEmail: email);
            WorkbenchHint.Text = email.WasExisting
                ? $"Updated email “{email.Subject ?? "(no subject)"}” — agent will clarify if a task was created."
                : $"Ingested “{email.Subject ?? "(no subject)"}” — agent will clarify if a task was created.";
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Email drop failed: {ex.Message}";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitCellSizeCombo();
        ApplyCellSize(App.Settings.WorkbenchCellSize);
        RefreshHostStatus();
        await RefreshHostStatusAsync();
        await ReloadWorkbenchAsync();
        StartPulsePolling();
        StartEventListener();
    }

    private void StartEventListener()
    {
        _ = StopEventListenerAsync();
        _eventListener = new OrbitEventListener(App.Settings, App.SettingsStore);
        _eventListener.OrbitEvent += OnHostOrbitEvent;
        _eventListener.Start();
    }

    private void OnHostOrbitEvent(string type)
    {
        if (type is not ("operator.briefing" or "task.updated" or "email.ingested" or "pulse.refresh"))
        {
            return;
        }

        // Debounce bursty hub traffic onto the UI thread.
        if (DateTimeOffset.UtcNow - _lastPulseReloadUtc < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (DetailFrame.Visibility == Visibility.Visible)
            {
                return;
            }

            await ReloadPulseAsync(refresh: false);
            if (type is "task.updated" or "email.ingested")
            {
                await ReloadWorkbenchAsync();
            }
        });
    }

    private async void HermesStrip_RefreshRequested(object? sender, EventArgs e) =>
        await ReloadPulseAsync(refresh: true);

    private void HermesStrip_ConcernClicked(object? sender, string taskId) =>
        _ = SelectTaskInTreeAsync(taskId);

    private void StartPulsePolling()
    {
        StopPulsePolling();
        _pulsePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _pulsePollTimer.Tick += async (_, _) =>
        {
            if (DetailFrame.Visibility == Visibility.Visible || _pulseBusy)
            {
                return;
            }

            await ReloadPulseAsync(refresh: false);
        };
        _pulsePollTimer.Start();
    }

    private void StopPulsePolling()
    {
        if (_pulsePollTimer is null)
        {
            return;
        }

        _pulsePollTimer.Stop();
        _pulsePollTimer = null;
    }

    private async Task ReloadPulseAsync(bool refresh)
    {
        if (_pulseBusy)
        {
            return;
        }

        _pulseBusy = true;
        HermesStrip.SetBusy(true);
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var pulse = refresh
                ? await client.RefreshPulseAsync()
                : await client.GetPulseAsync();
            HermesStrip.Bind(pulse);
            _lastPulseReloadUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception)
        {
            HermesStrip.Bind(null);
        }
        finally
        {
            HermesStrip.SetBusy(false);
            _pulseBusy = false;
        }
    }

    private void InitCellSizeCombo()
    {
        var size = Math.Clamp(App.Settings.WorkbenchCellSize, 0, 2);
        _cellSizeComboReady = false;
        CellSizeCombo.SelectedIndex = size;
        _cellSizeComboReady = true;
    }

    private void CellSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_cellSizeComboReady || CellSizeCombo.SelectedIndex < 0)
        {
            return;
        }

        var size = Math.Clamp(CellSizeCombo.SelectedIndex, 0, 2);
        ApplyCellSize(size);
        try
        {
            App.Settings.WorkbenchCellSize = size;
            App.SettingsStore.Save(App.Settings);
        }
        catch (Exception)
        {
            // Size still applies for this session.
        }
    }

    private void ApplyCellSize(int size)
    {
        // Defaults for newly auto-placed cells; also reflow existing board to the new default
        // only for cells that still match the previous default band — keep custom sizes.
        (_defaultCellW, _defaultCellH) = size switch
        {
            0 => (240.0, 220.0),
            2 => (420.0, 360.0),
            _ => (320.0, 280.0),
        };

        if (CellBoard.Children.Count > 0)
        {
            ReflowAllCells(persist: true);
        }
    }

    private (double Width, double Height) GetDefaultCellSize() => (_defaultCellW, _defaultCellH);

    private double _defaultCellW = 320;
    private double _defaultCellH = 280;

    private void WorkbenchPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        foreach (var cell in _wiredCells)
        {
            if (cell.TryDismissTransientUi())
            {
                e.Handled = true;
                return;
            }
        }

        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox focused)
        {
            if (ReferenceEquals(focused, AgentInputBox) && !string.IsNullOrEmpty(AgentInputBox.Text))
            {
                AgentInputBox.Text = string.Empty;
                e.Handled = true;
                return;
            }
        }

        if (_detailFlyout?.IsOpen == true)
        {
            CloseDetailFlyout();
            e.Handled = true;
            return;
        }

        if (DrawerHost.Visibility == Visibility.Visible)
        {
            CloseDrawer();
            e.Handled = true;
            return;
        }

        if (_scopeProjectId is not null)
        {
            _ = ExitProjectBoardAsync();
            e.Handled = true;
        }
    }

    private async Task EnterProjectBoardAsync(ProjectCellVm project)
    {
        if (project.IsTaskCell || project.IsLimboCell || string.IsNullOrWhiteSpace(project.Id))
        {
            return;
        }

        CloseDetailFlyout();
        CloseDrawer();
        await TransitionBoardAsync(project.Id, project.Name);
    }

    private async void BackToRoot_Click(object sender, RoutedEventArgs e) => await ExitProjectBoardAsync();

    private async Task ExitProjectBoardAsync()
    {
        if (_scopeProjectId is null)
        {
            return;
        }

        CloseDetailFlyout();
        CloseDrawer();
        await TransitionBoardAsync(projectId: null, projectName: null);
    }

    private async Task TransitionBoardAsync(string? projectId, string? projectName)
    {
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fadeOut, CellScroller);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var outBoard = new Storyboard();
        outBoard.Children.Add(fadeOut);
        outBoard.Begin();
        await Task.Delay(160);

        _scopeProjectId = projectId;
        _scopeProjectName = projectName;
        await ReloadWorkbenchAsync();

        CellScroller.Opacity = 0;
        var fadeIn = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fadeIn, CellScroller);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var inBoard = new Storyboard();
        inBoard.Children.Add(fadeIn);
        inBoard.Begin();
    }

    public async Task ReloadAfterExternalIngestAsync()
    {
        await ReloadWorkbenchAsync();
        WorkbenchHint.Text = "Hermes organized the pushed mail — project cells updated.";
    }

    private async void PushOutlook_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            var shell = FindShell(root);
            if (shell is not null)
            {
                await shell.PushOutlookSelectionAsync(promptForMemo: true);
                return;
            }
        }

        PushOutlookButton.IsEnabled = false;
        try
        {
            if (XamlRoot is null)
            {
                WorkbenchHint.Text = "Orbit UI is not ready.";
                return;
            }

            var prompt = await OutlookPushPrompt.ShowAsync(XamlRoot);
            if (prompt is null)
            {
                return;
            }

            IReadOnlyList<string>? projectIds = string.IsNullOrWhiteSpace(prompt.ProjectId)
                ? null
                : [prompt.ProjectId];
            var result = await OutlookPushCoordinator.PushSelectedAsync(
                App.Settings,
                App.SettingsStore,
                projectIds,
                prompt.Memo);
            WorkbenchHint.Text = result.StatusLine + " — " + TruncateHint(result.Detail, 160);
            if (result.Ok)
            {
                await ReloadWorkbenchAsync();
            }
        }
        finally
        {
            PushOutlookButton.IsEnabled = true;
        }
    }

    private static ShellPage? FindShell(DependencyObject root)
    {
        if (root is ShellPage shell)
        {
            return shell;
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindShell(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string TruncateHint(string text, int max)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private async Task ReloadWorkbenchAsync()
    {
        try
        {
            if (App.HostConnection is null)
            {
                WorkbenchHint.Text = "Core Host unavailable.";
                return;
            }

            await App.HostConnection.EnsureConnectedAsync();
            if (App.HostConnection.LastStatus.State != CoreHostConnectionState.Connected)
            {
                WorkbenchHint.Text = "Core Host degraded. Capture needs a running host.";
                return;
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            _scopeProjectId = null;
            _scopeProjectName = null;
            var snapshot = await client.GetWorkbenchAsync(null);
            if (snapshot is null)
            {
                WorkbenchHint.Text = "Could not load workbench.";
                return;
            }

            ApplyScopeChrome();
            await RebuildOrbitTreeAsync(client, snapshot);
            await ReloadPulseAsync(refresh: false);

            OrbitRuntimeContextProvider.Instance.SetWorkbenchProjects(
                snapshot.Cells.Where(c => !c.IsLimboCell).Select(c => c.Name));
            var limboCount = snapshot.Limbo.Count;
            WorkbenchHint.Text = limboCount == 0
                ? "Select a project or task · Hermes keeps briefs warm."
                : $"{limboCount} in Limbo · capture via the box below.";

            var meetings = await client.GetUpcomingMeetingLinesAsync();
            UpcomingMeetingsText.Text = FormatUpcomingMeetingsLine(meetings);
            SyncStatusSeparator();
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Workbench refresh failed.";
        }
        finally
        {
            RefreshHostStatus();
        }
    }

    private void ApplyScopeChrome()
    {
        WorkbenchTitle.Text = "Orbit";
        WorkbenchSubtitle.Text = string.Empty;
        BackToRootButton.Visibility = Visibility.Collapsed;
    }

    private static string FormatUpcomingMeetingsLine(IReadOnlyList<string> meetings)
    {
        if (meetings.Count == 0)
        {
            return string.Empty;
        }

        // Lines arrive as "title · startsAt · source"; keep title + short local date.
        var parts = new List<string>();
        foreach (var raw in meetings.Take(2))
        {
            var bits = raw.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var title = bits.Length > 0 ? bits[0] : raw;
            if (title.Length > 28)
            {
                title = title[..25] + "…";
            }

            var when = bits.Length > 1 ? ShortMeetingWhen(bits[1]) : string.Empty;
            parts.Add(string.IsNullOrEmpty(when) ? title : $"{title} {when}");
        }

        return "Next: " + string.Join(" · ", parts);
    }

    private static string ShortMeetingWhen(string startsAt)
    {
        if (!DateTimeOffset.TryParse(startsAt, out var dto))
        {
            return string.Empty;
        }

        var local = dto.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
        {
            return local.ToString("h:mm tt");
        }

        if (local.Date == today.AddDays(1))
        {
            return "tomorrow " + local.ToString("h:mm tt");
        }

        return local.ToString("MMM d");
    }

    private void SyncStatusSeparator()
    {
        var showSep = !string.IsNullOrWhiteSpace(AgentStatusText.Text)
            && !string.IsNullOrWhiteSpace(UpcomingMeetingsText.Text);
        StatusSep.Visibility = showSep ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnsureAgentRailVisible()
    {
        AgentRail.Visibility = Visibility.Collapsed;
    }

    private void RebuildCellBoard(IList<ProjectCellVm> cells)
    {
        CellBoard.Children.Clear();
        _wiredCells.Clear();
    }

    private async Task RebuildOrbitTreeAsync(CoreHostClient client, WorkbenchVm root)
    {
        var selectedId = _selectedNode?.Id;
        _treeRoots.Clear();
        _nodesById.Clear();

        foreach (var cell in root.Cells.Where(c => !c.IsLimboCell).OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
        {
            var projectNode = new OrbitTreeNodeVm
            {
                Kind = OrbitTreeNodeKind.Project,
                Id = cell.Id,
                ProjectId = cell.Id,
                Title = cell.Name,
                Status = cell.Status,
                NextAction = cell.TopBlockerSummary,
            };
            _nodesById[projectNode.Id] = projectNode;

            var scoped = await client.GetWorkbenchAsync(cell.Id);
            if (scoped?.IsProjectScoped == true)
            {
                foreach (var taskCell in scoped.Cells.Where(c => c.IsTaskCell))
                {
                    var taskNode = new OrbitTreeNodeVm
                    {
                        Kind = OrbitTreeNodeKind.Task,
                        Id = taskCell.Id,
                        ProjectId = cell.Id,
                        Title = taskCell.Name,
                        Status = taskCell.Status,
                        NextAction = taskCell.Lines.FirstOrDefault()?.NextAction
                            ?? taskCell.TopBlockerSummary,
                    };
                    _nodesById[taskNode.Id] = taskNode;

                    foreach (var line in taskCell.Lines)
                    {
                        if (string.IsNullOrWhiteSpace(line.TaskId)
                            || string.Equals(line.TaskId, taskCell.Id, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var sub = new OrbitTreeNodeVm
                        {
                            Kind = OrbitTreeNodeKind.Subtask,
                            Id = line.TaskId,
                            ProjectId = cell.Id,
                            ParentTaskId = taskCell.Id,
                            Title = line.Title,
                            Status = line.Status,
                            NextAction = line.NextAction,
                        };
                        if (_nodesById.TryAdd(sub.Id, sub))
                        {
                            taskNode.Children.Add(sub);
                        }
                    }

                    projectNode.Children.Add(taskNode);
                }
            }
            else
            {
                foreach (var line in cell.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.TaskId))
                    {
                        continue;
                    }

                    var taskNode = new OrbitTreeNodeVm
                    {
                        Kind = OrbitTreeNodeKind.Task,
                        Id = line.TaskId,
                        ProjectId = cell.Id,
                        Title = line.Title,
                        Status = line.Status,
                        NextAction = line.NextAction,
                    };
                    if (_nodesById.TryAdd(taskNode.Id, taskNode))
                    {
                        projectNode.Children.Add(taskNode);
                    }
                }
            }

            var ctx = await client.GetProjectContextAsync(cell.Id);
            var completed = ctx?.CompletedTasks ?? [];
            var completedGroup = new OrbitTreeNodeVm
            {
                Kind = OrbitTreeNodeKind.Completed,
                Id = $"completed:{cell.Id}",
                ProjectId = cell.Id,
                Title = completed.Count == 0 ? "Completed" : $"Completed · {completed.Count}",
            };
            _nodesById[completedGroup.Id] = completedGroup;
            foreach (var done in completed)
            {
                var doneNode = new OrbitTreeNodeVm
                {
                    Kind = OrbitTreeNodeKind.Task,
                    Id = done.TaskId,
                    ProjectId = cell.Id,
                    Title = done.Title,
                    Status = done.Status,
                    NextAction = done.NextAction,
                };
                if (_nodesById.TryAdd(doneNode.Id, doneNode))
                {
                    completedGroup.Children.Add(doneNode);
                }
            }

            projectNode.Children.Add(completedGroup);
            _treeRoots.Add(projectNode);
        }

        if (root.Limbo.Count > 0 || root.Cells.Any(c => c.IsLimboCell))
        {
            var limbo = new OrbitTreeNodeVm
            {
                Kind = OrbitTreeNodeKind.Limbo,
                Id = "limbo",
                Title = "Limbo",
                NextAction = $"{root.Limbo.Count} captures",
            };
            _treeRoots.Add(limbo);
            _nodesById[limbo.Id] = limbo;
        }

        PinCompletedGroups();
        OrbitTree.ItemsSource = _treeRoots;

        if (!string.IsNullOrWhiteSpace(selectedId)
            && _nodesById.TryGetValue(selectedId, out var keep))
        {
            await ShowDetailForNodeAsync(keep);
        }
    }

    private async void OrbitTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OrbitTreeNodeVm node)
        {
            await ShowDetailForNodeAsync(node);
        }
    }

    private void OrbitTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is OrbitTreeNodeVm node)
        {
            _selectedNode = node;
            SyncAddButtons(node);
            return;
        }

        if (sender.SelectedNodes.Count > 0
            && sender.SelectedNodes[0].Content is OrbitTreeNodeVm fromNode)
        {
            _selectedNode = fromNode;
            SyncAddButtons(fromNode);
            return;
        }

        SyncAddButtons(_selectedNode);
    }

    private void OrbitTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        _treeDragNode = args.Items.OfType<OrbitTreeNodeVm>().FirstOrDefault();
        if (_treeDragNode is null
            || _treeDragNode.Kind is OrbitTreeNodeKind.Limbo or OrbitTreeNodeKind.Completed)
        {
            args.Cancel = true;
            _treeDragNode = null;
            return;
        }

        args.Data.SetText(_treeDragNode.Id);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OrbitTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        // TreeView may reorder siblings during drag — keep Completed pinned last.
        PinCompletedGroups();
        _treeDragNode = null;
    }

    private void PinCompletedGroups()
    {
        foreach (var project in _treeRoots.Where(n => n.Kind == OrbitTreeNodeKind.Project))
        {
            var completed = project.Children.FirstOrDefault(c => c.Kind == OrbitTreeNodeKind.Completed);
            if (completed is null)
            {
                continue;
            }

            var idx = project.Children.IndexOf(completed);
            if (idx < 0 || idx == project.Children.Count - 1)
            {
                continue;
            }

            project.Children.RemoveAt(idx);
            project.Children.Add(completed);
        }
    }

    private void OrbitTree_DragOver(object sender, DragEventArgs e)
    {
        if (_treeDragNode is null && !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void OrbitTree_Drop(object sender, DragEventArgs e)
    {
        var drag = _treeDragNode;
        _treeDragNode = null;
        if (drag is null)
        {
            return;
        }

        var target = NodeFromVisual(e.OriginalSource) ?? OrbitTree.SelectedItem as OrbitTreeNodeVm;
        if (target is null || ReferenceEquals(target, drag) || string.Equals(target.Id, drag.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (drag.Kind == OrbitTreeNodeKind.Project && target.Kind == OrbitTreeNodeKind.Project)
            {
                await ReorderProjectRelativeAsync(drag.Id, target.Id, insertBefore: true);
                return;
            }

            if (drag.Kind is not (OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask))
            {
                return;
            }

            if (target.Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask)
            {
                await NestTaskUnderAsync(drag.Id, target.Id);
                return;
            }

            if (target.Kind == OrbitTreeNodeKind.Project
                && string.Equals(drag.ProjectId, target.ProjectId ?? target.Id, StringComparison.Ordinal))
            {
                await UnnestTaskAsync(drag.Id);
                return;
            }

            if (target.Kind == OrbitTreeNodeKind.Completed
                && string.Equals(drag.ProjectId, target.ProjectId, StringComparison.Ordinal))
            {
                await MarkTaskCompleteAsync(drag.Id);
            }
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Drop failed: {ex.GetType().Name}";
        }
        finally
        {
            PinCompletedGroups();
        }
    }

    private static OrbitTreeNodeVm? NodeFromVisual(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: OrbitTreeNodeVm vm })
            {
                return vm;
            }

            if (d is TreeViewItem item)
            {
                if (item.Content is OrbitTreeNodeVm content)
                {
                    return content;
                }

                if (item.DataContext is OrbitTreeNodeVm ctx)
                {
                    return ctx;
                }
            }
        }

        return null;
    }

    private void OrbitTree_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (OrbitTree.SelectedItem is OrbitTreeNodeVm node)
        {
            ShowTreeContextMenu(node, OrbitTree, args);
        }
    }

    private void OrbitTreeItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var node = NodeFromVisual(sender);
        if (node is null)
        {
            return;
        }

        ShowTreeContextMenu(node, sender, args);
    }

    private void ShowTreeContextMenu(OrbitTreeNodeVm node, UIElement target, ContextRequestedEventArgs args)
    {
        var menu = new MenuFlyout();
        switch (node.Kind)
        {
            case OrbitTreeNodeKind.Project:
                AddMenu(menu, "Open workspace", () => ShowDetailForNodeAsync(node));
                AddMenu(menu, "Add task", async () =>
                {
                    _selectedNode = node;
                    await CreateTaskUnderAsync(node.Id, parentTaskId: null, title: "New task");
                });
                AddMenu(menu, "Move up", () => MoveProjectAsync(node.Id, -1));
                AddMenu(menu, "Move down", () => MoveProjectAsync(node.Id, +1));
                AddMenu(menu, "Archive project", () => ArchiveEntityAsync("project", node.Id));
                break;

            case OrbitTreeNodeKind.Completed:
                AddMenu(menu, "Open completed", () => ShowCompletedListAsync(node));
                AddMenu(menu, "Refresh", ReloadWorkbenchAsync);
                break;

            case OrbitTreeNodeKind.Limbo:
                AddMenu(menu, "Open limbo", () => ShowDetailForNodeAsync(node));
                break;

            case OrbitTreeNodeKind.Task:
            case OrbitTreeNodeKind.Subtask:
                var isDone = string.Equals(node.Status, "complete", StringComparison.OrdinalIgnoreCase);
                AddMenu(menu, "Open", () => ShowDetailForNodeAsync(node));
                if (!isDone)
                {
                    AddMenu(menu, "Add subtask", async () =>
                    {
                        _selectedNode = node;
                        await CreateTaskUnderAsync(node.ProjectId ?? string.Empty, parentTaskId: node.Id, title: "New subtask");
                    });
                    AddMenu(menu, "Mark complete", () => MarkTaskCompleteAsync(node.Id));
                    if (node.Kind == OrbitTreeNodeKind.Subtask || !string.IsNullOrWhiteSpace(node.ParentTaskId))
                    {
                        AddMenu(menu, "Remove from parent", () => UnnestTaskAsync(node.Id));
                    }

                    var nest = new MenuFlyoutSubItem { Text = "Nest under…" };
                    foreach (var sibling in SiblingTasksForNest(node).Take(12))
                    {
                        var parentId = sibling.Id;
                        var item = new MenuFlyoutItem { Text = TruncateHint(sibling.Title, 40) };
                        item.Click += async (_, _) => await NestTaskUnderAsync(node.Id, parentId);
                        nest.Items.Add(item);
                    }

                    if (nest.Items.Count > 0)
                    {
                        menu.Items.Add(nest);
                    }

                    var move = new MenuFlyoutSubItem { Text = "Move to project…" };
                    foreach (var project in _treeRoots
                                 .Where(n => n.Kind == OrbitTreeNodeKind.Project)
                                 .Where(n => !string.Equals(n.Id, node.ProjectId, StringComparison.Ordinal))
                                 .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
                                 .Take(24))
                    {
                        var targetId = project.Id;
                        var targetName = project.Title;
                        var item = new MenuFlyoutItem { Text = TruncateHint(targetName, 40) };
                        item.Click += async (_, _) => await MoveTaskToProjectAsync(node.Id, targetId, targetName);
                        move.Items.Add(item);
                    }

                    if (move.Items.Count == 0)
                    {
                        move.Items.Add(new MenuFlyoutItem
                        {
                            Text = "No other projects",
                            IsEnabled = false,
                        });
                    }

                    menu.Items.Add(move);
                }
                else
                {
                    AddMenu(menu, "Reopen", () => ReopenTaskAsync(node.Id));
                }

                AddMenu(menu, "Archive", () => ArchiveEntityAsync("task", node.Id));
                break;
        }

        if (menu.Items.Count == 0)
        {
            return;
        }

        if (args.TryGetPosition(target, out var point))
        {
            menu.ShowAt(target, point);
        }
        else if (target is FrameworkElement fe)
        {
            menu.ShowAt(fe);
        }
        else
        {
            menu.ShowAt(OrbitTree);
        }

        args.Handled = true;
    }

    private static void AddMenu(MenuFlyout menu, string label, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception)
            {
                // hints set inside actions
            }
        };
        menu.Items.Add(item);
    }

    private IEnumerable<OrbitTreeNodeVm> SiblingTasksForNest(OrbitTreeNodeVm node)
    {
        if (string.IsNullOrWhiteSpace(node.ProjectId)
            || !_nodesById.TryGetValue(node.ProjectId, out var project))
        {
            return [];
        }

        return project.Children
            .Where(c => c.Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask)
            .SelectMany(FlattenTaskBranch)
            .Where(t => !string.Equals(t.Id, node.Id, StringComparison.Ordinal)
                        && !IsDescendantOf(node, t));
    }

    private static IEnumerable<OrbitTreeNodeVm> FlattenTaskBranch(OrbitTreeNodeVm node)
    {
        yield return node;
        foreach (var child in node.Children.Where(c => c.Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask))
        {
            foreach (var nested in FlattenTaskBranch(child))
            {
                yield return nested;
            }
        }
    }

    private static bool IsDescendantOf(OrbitTreeNodeVm ancestor, OrbitTreeNodeVm node)
    {
        foreach (var child in ancestor.Children)
        {
            if (string.Equals(child.Id, node.Id, StringComparison.Ordinal) || IsDescendantOf(child, node))
            {
                return true;
            }
        }

        return false;
    }

    private async Task MoveProjectAsync(string projectId, int delta)
    {
        var projects = _treeRoots.Where(n => n.Kind == OrbitTreeNodeKind.Project).ToList();
        var idx = projects.FindIndex(p => string.Equals(p.Id, projectId, StringComparison.Ordinal));
        if (idx < 0)
        {
            return;
        }

        var dest = idx + delta;
        if (dest < 0 || dest >= projects.Count)
        {
            return;
        }

        (projects[idx], projects[dest]) = (projects[dest], projects[idx]);
        await PersistProjectOrderAsync(projects.Select(p => p.Id).ToList());
    }

    private async Task ReorderProjectRelativeAsync(string dragId, string targetId, bool insertBefore)
    {
        var projects = _treeRoots.Where(n => n.Kind == OrbitTreeNodeKind.Project).ToList();
        var drag = projects.FirstOrDefault(p => string.Equals(p.Id, dragId, StringComparison.Ordinal));
        var target = projects.FirstOrDefault(p => string.Equals(p.Id, targetId, StringComparison.Ordinal));
        if (drag is null || target is null || ReferenceEquals(drag, target))
        {
            return;
        }

        projects.Remove(drag);
        var at = projects.IndexOf(target);
        if (at < 0)
        {
            return;
        }

        if (!insertBefore)
        {
            at++;
        }

        projects.Insert(Math.Clamp(at, 0, projects.Count), drag);
        await PersistProjectOrderAsync(projects.Select(p => p.Id).ToList());
    }

    private async Task PersistProjectOrderAsync(IReadOnlyList<string> projectIds)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            for (var i = 0; i < projectIds.Count; i++)
            {
                if (!await client.SetWorkbenchCellLayoutAsync(
                        projectIds[i],
                        WorkbenchCellKinds.Project,
                        x: i * 10,
                        y: 0,
                        width: 280,
                        height: 200,
                        sortOrder: i))
                {
                    WorkbenchHint.Text = "Could not save project order.";
                    return;
                }
            }

            WorkbenchHint.Text = "Project order saved.";
            await ReloadWorkbenchAsync();
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Reorder failed: {ex.Message}";
        }
    }

    private async Task NestTaskUnderAsync(string childId, string parentId)
    {
        if (string.Equals(childId, parentId, StringComparison.Ordinal))
        {
            return;
        }

        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        await TryUnlinkRelatesParentsAsync(client, childId);
        if (!await client.LinkTasksAsync(
                parentId,
                childId,
                TaskDependencyTypes.Relates,
                reason: "subtask"))
        {
            WorkbenchHint.Text = "Could not nest task.";
            return;
        }

        WorkbenchHint.Text = "Nested as subtask.";
        await ReloadWorkbenchAsync();
        await SelectTaskInTreeAsync(childId);
    }

    private async Task UnnestTaskAsync(string taskId)
    {
        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        await TryUnlinkRelatesParentsAsync(client, taskId);
        WorkbenchHint.Text = "Moved to project root.";
        await ReloadWorkbenchAsync();
        await SelectTaskInTreeAsync(taskId);
    }

    private static async Task TryUnlinkRelatesParentsAsync(CoreHostClient client, string taskId)
    {
        var links = await client.GetTaskDependenciesAsync(taskId);
        foreach (var edge in links.WaitingOn.Concat(links.Feeds))
        {
            if (string.Equals(edge.DependencyType, TaskDependencyTypes.Relates, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(edge.DependencyId))
            {
                await client.UnlinkTasksAsync(edge.DependencyId);
            }
        }
    }

    private async Task MarkTaskCompleteAsync(string taskId)
    {
        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateTaskAsync(taskId, status: TaskStatuses.Complete))
        {
            WorkbenchHint.Text = "Marked complete.";
            await ReloadWorkbenchAsync();
        }
        else
        {
            WorkbenchHint.Text = "Could not mark complete.";
        }
    }

    private async Task ReopenTaskAsync(string taskId)
    {
        using var client = new CoreHostClient(App.Settings, App.SettingsStore);
        if (await client.UpdateTaskAsync(taskId, status: TaskStatuses.Active))
        {
            WorkbenchHint.Text = "Reopened.";
            await ReloadWorkbenchAsync();
            await SelectTaskInTreeAsync(taskId);
        }
        else
        {
            WorkbenchHint.Text = "Could not reopen.";
        }
    }

    private void SyncAddButtons(OrbitTreeNodeVm? node)
    {
        AddTaskButton.IsEnabled = node?.Kind is OrbitTreeNodeKind.Project or OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask;
        AddSubtaskButton.IsEnabled = node?.Kind is OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask;
    }

    private async Task ShowDetailForNodeAsync(OrbitTreeNodeVm node)
    {
        _selectedNode = node;
        SyncAddButtons(node);

        if (node.Kind == OrbitTreeNodeKind.Limbo)
        {
            DetailEmptyText.Text = "Limbo captures — use quick capture below.";
            DetailEmptyText.Visibility = Visibility.Visible;
            DetailHost.Content = null;
            DetailHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (node.Kind == OrbitTreeNodeKind.Completed)
        {
            await ShowCompletedListAsync(node);
            return;
        }

        try
        {
            CloseDetailFlyout();
            if (_detailFlyout is not null)
            {
                _detailFlyout.Content = null;
            }

            var projectId = node.ProjectId ?? node.Id;
            if (node.Kind == OrbitTreeNodeKind.Project)
            {
                await ShowProjectWorkspaceAsync(projectId, node.Title);
                return;
            }

            await ShowTaskBriefAsync(projectId, node.Id, node.Title);
        }
        catch (Exception ex)
        {
            DetailHost.Content = null;
            DetailHost.Visibility = Visibility.Collapsed;
            DetailEmptyText.Visibility = Visibility.Visible;
            DetailEmptyText.Text = "Could not open detail.";
            WorkbenchHint.Text = $"Detail failed: {ex.GetType().Name}";
        }
    }

    private async Task ShowCompletedListAsync(OrbitTreeNodeVm completedNode)
    {
        var projectId = completedNode.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var projectName = _nodesById.TryGetValue(projectId, out var project)
            ? project.Title
            : "Project";

        _completedPanel ??= new CompletedTasksPanel();
        _completedPanel.CloseRequested -= CompletedPanel_CloseRequested;
        _completedPanel.ContentChanged -= DetailPanel_ContentChanged;
        _completedPanel.TaskOpenRequested -= WorkspacePanel_TaskOpenRequested;
        _completedPanel.CloseRequested += CompletedPanel_CloseRequested;
        _completedPanel.ContentChanged += DetailPanel_ContentChanged;
        _completedPanel.TaskOpenRequested += WorkspacePanel_TaskOpenRequested;

        DetailHost.Content = _completedPanel;
        DetailHost.Visibility = Visibility.Visible;
        DetailEmptyText.Visibility = Visibility.Collapsed;
        await _completedPanel.LoadAsync(projectId, projectName);
        OrbitRuntimeContextProvider.Instance.SetFocus(projectId, projectName);
    }

    private void CompletedPanel_CloseRequested(object? sender, EventArgs e)
    {
        DetailHost.Content = null;
        DetailHost.Visibility = Visibility.Collapsed;
        DetailEmptyText.Visibility = Visibility.Visible;
        DetailEmptyText.Text = "Select a project or task";
    }

    private async Task ShowProjectWorkspaceAsync(string projectId, string? title = null)
    {
        _workspacePanel ??= new ProjectWorkspacePanel();
        _workspacePanel.CloseRequested -= WorkspacePanel_CloseRequested;
        _workspacePanel.ContentChanged -= DetailPanel_ContentChanged;
        _workspacePanel.TaskOpenRequested -= WorkspacePanel_TaskOpenRequested;
        _workspacePanel.CloseRequested += WorkspacePanel_CloseRequested;
        _workspacePanel.ContentChanged += DetailPanel_ContentChanged;
        _workspacePanel.TaskOpenRequested += WorkspacePanel_TaskOpenRequested;
        _workspacePanel.TaskCompleteRequested -= WorkspacePanel_TaskCompleteRequested;
        _workspacePanel.TaskArchiveRequested -= WorkspacePanel_TaskArchiveRequested;
        _workspacePanel.TaskCompleteRequested += WorkspacePanel_TaskCompleteRequested;
        _workspacePanel.TaskArchiveRequested += WorkspacePanel_TaskArchiveRequested;

        DetailHost.Content = _workspacePanel;
        DetailHost.Visibility = Visibility.Visible;
        DetailEmptyText.Visibility = Visibility.Collapsed;

        await _workspacePanel.LoadProjectAsync(projectId);
        OrbitRuntimeContextProvider.Instance.SetFocus(projectId, title ?? projectId);
    }

    private async Task ShowTaskBriefAsync(string projectId, string taskId, string? title = null)
    {
        _detailPanel ??= new WorkbenchDetailPanel();
        _detailPanel.CloseRequested -= DetailPanel_CloseRequested;
        _detailPanel.ContentChanged -= DetailPanel_ContentChanged;
        _detailPanel.BackToProjectRequested -= DetailPanel_BackToProjectRequested;
        _detailPanel.CloseRequested += DetailPanel_CloseRequested;
        _detailPanel.ContentChanged += DetailPanel_ContentChanged;
        _detailPanel.BackToProjectRequested += DetailPanel_BackToProjectRequested;
        _detailPanel.SetInlineMode(true);

        DetailHost.Content = _detailPanel;
        DetailHost.Visibility = Visibility.Visible;
        DetailEmptyText.Visibility = Visibility.Collapsed;

        await _detailPanel.LoadTaskAsync(projectId, taskId);
        OrbitRuntimeContextProvider.Instance.SetFocus(projectId, title ?? taskId, taskId);
    }

    private void WorkspacePanel_CloseRequested(object? sender, EventArgs e)
    {
        DetailHost.Content = null;
        DetailHost.Visibility = Visibility.Collapsed;
        DetailEmptyText.Visibility = Visibility.Visible;
        DetailEmptyText.Text = "Select a project or task";
    }

    private void WorkspacePanel_TaskOpenRequested(object? sender, string taskId) =>
        _ = SelectTaskInTreeAsync(taskId);

    private void WorkspacePanel_TaskCompleteRequested(object? sender, string taskId) =>
        _ = MarkTaskCompleteAsync(taskId);

    private void WorkspacePanel_TaskArchiveRequested(object? sender, string taskId) =>
        _ = ArchiveEntityAsync("task", taskId);

    private async void DetailPanel_BackToProjectRequested(object? sender, string projectId)
    {
        if (_nodesById.TryGetValue(projectId, out var node) && node.Kind == OrbitTreeNodeKind.Project)
        {
            await ShowDetailForNodeAsync(node);
            return;
        }

        await ShowProjectWorkspaceAsync(projectId);
    }

    private async Task SelectTaskInTreeAsync(string taskId)
    {
        if (_treeRoots.Count == 0)
        {
            await ReloadWorkbenchAsync();
        }

        if (_nodesById.TryGetValue(taskId, out var node))
        {
            await ShowDetailForNodeAsync(node);
            return;
        }

        await ReloadWorkbenchAsync();
        if (_nodesById.TryGetValue(taskId, out node))
        {
            await ShowDetailForNodeAsync(node);
        }
        else
        {
            WorkbenchHint.Text = "Concern not found in the tree yet — Refresh or wait for Hermes.";
        }
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var projectId = _selectedNode?.ProjectId
            ?? (_selectedNode?.Kind == OrbitTreeNodeKind.Project ? _selectedNode.Id : null);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            WorkbenchHint.Text = "Select a project first.";
            return;
        }

        await CreateTaskUnderAsync(projectId, parentTaskId: null, title: "New task");
    }

    private async void AddSubtask_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null
            || _selectedNode.Kind is not (OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask)
            || string.IsNullOrWhiteSpace(_selectedNode.ProjectId))
        {
            WorkbenchHint.Text = "Select a task to add a subtask.";
            return;
        }

        var parentId = _selectedNode.Kind == OrbitTreeNodeKind.Subtask
            ? (_selectedNode.ParentTaskId ?? _selectedNode.Id)
            : _selectedNode.Id;
        await CreateTaskUnderAsync(_selectedNode.ProjectId!, parentId, "New subtask");
    }

    private async Task CreateTaskUnderAsync(string projectId, string? parentTaskId, string title)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var created = await client.CreateTaskAsync(title, projectId, nextAction: "Define next move", body: "");
            if (created is null)
            {
                WorkbenchHint.Text = "Could not create task.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(parentTaskId))
            {
                await client.LinkTasksAsync(
                    predecessorTaskId: parentTaskId,
                    successorTaskId: created.Value.Id,
                    dependencyType: TaskDependencyTypes.Relates,
                    reason: "subtask");
            }

            await ReloadWorkbenchAsync();
            await SelectTaskInTreeAsync(created.Value.Id);
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Create failed: {ex.Message}";
        }
    }

    private void CellScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 100 || CellBoard.Children.Count == 0)
        {
            return;
        }

        // Only reflow when width changes enough to change wrapping.
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 24
            && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 48)
        {
            return;
        }

        ReflowAllCells(persist: false);
    }

    private void ReflowAllCells(bool persist)
    {
        var cells = EnumerateCellVms().ToList();
        if (cells.Count == 0)
        {
            return;
        }

        var boardWidth = WorkbenchPacker.BoardWidth(CellScroller.ActualWidth);
        var ordered = WorkbenchPacker.PackProjectsAndPinLimbo(cells, boardWidth, BoardViewportHeight);
        ApplyAllCellPositions(ordered, dragged: null, followFinger: false);
        if (persist)
        {
            _ = PersistAllLayoutsAsync(ordered);
        }
    }

    private void ApplyAllCellPositions(
        IList<ProjectCellVm> ordered,
        ProjectCellVm? dragged,
        bool followFinger,
        double fingerX = 0,
        double fingerY = 0)
    {
        double maxRight = 0;
        double maxBottom = 0;
        foreach (var vm in ordered)
        {
            var control = FindCellControl(vm.Id);
            if (control is null)
            {
                continue;
            }

            control.Width = vm.BoardW;
            control.Height = vm.BoardH;
            if (followFinger && dragged is not null && ReferenceEquals(vm, dragged))
            {
                Canvas.SetLeft(control, fingerX);
                Canvas.SetTop(control, fingerY);
                Canvas.SetZIndex(control, 10);
                maxRight = Math.Max(maxRight, fingerX + vm.BoardW);
                maxBottom = Math.Max(maxBottom, fingerY + vm.BoardH);
            }
            else
            {
                Canvas.SetLeft(control, vm.BoardX);
                Canvas.SetTop(control, vm.BoardY);
                Canvas.SetZIndex(control, 0);
                maxRight = Math.Max(maxRight, vm.BoardX + vm.BoardW);
                maxBottom = Math.Max(maxBottom, vm.BoardY + vm.BoardH);
            }
        }

        ResizeBoardCanvas(maxRight, maxBottom, WorkbenchPacker.BoardWidth(CellScroller.ActualWidth));
    }

    private void ResizeBoardCanvas(double maxRight, double maxBottom, double boardWidth)
    {
        CellBoard.Width = Math.Max(boardWidth, maxRight + 48);
        CellBoard.Height = Math.Max(CellScroller.ActualHeight > 0 ? CellScroller.ActualHeight : 600, maxBottom + 48);
    }

    private async Task PersistAllLayoutsAsync(IEnumerable<ProjectCellVm> cells)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            foreach (var cell in cells)
            {
                var kind = cell.IsLimboCell
                    ? WorkbenchCellKinds.Limbo
                    : cell.IsTaskCell
                        ? WorkbenchCellKinds.Task
                        : WorkbenchCellKinds.Project;
                if (!await client.SetWorkbenchCellLayoutAsync(
                        cell.Id,
                        kind,
                        cell.BoardX,
                        cell.BoardY,
                        cell.BoardW,
                        cell.BoardH,
                        cell.SortOrder))
                {
                    WorkbenchHint.Text = "Could not save cell layout.";
                    return;
                }
            }
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Could not save cell layout.";
        }
    }

    private void WireCell(ProjectCellControl cell)
    {
        if (!_wiredCells.Add(cell))
        {
            return;
        }

        cell.OpenRequested += async (_, item) =>
        {
            if (item.IsLimboCell)
            {
                return;
            }

            if (item.IsTaskCell)
            {
                if (_scopeProjectId is null)
                {
                    return;
                }

                OpenConcernOnPulse(item.Id);
                return;
            }

            await OpenDetailNearAsync(cell, item.Id, taskId: null);
        };
        cell.EnterBoardRequested += async (_, project) => await EnterProjectBoardAsync(project);
        cell.LineOpenRequested += async (_, args) =>
        {
            if (args.Cell.IsLimboCell)
            {
                await OpenLimboDetailNearAsync(cell, args.TaskId);
                return;
            }

            OpenConcernOnPulse(args.TaskId);
        };
        cell.CaptureSubmitted += async (_, args) =>
        {
            if (args.Cell.IsLimboCell)
            {
                // Agent input lives in the side rail; ignore cell capture on Limbo.
                return;
            }

            if (args.Cell.IsTaskCell)
            {
                await CaptureLineOnTaskCellAsync(args.Text, args.Cell, cell);
                return;
            }

            await CaptureAsync(args.Text, args.Cell.Id, args.Cell.Name, cell);
        };
        cell.AgentClarifyReply += async (_, args) => await HandleClarifyReplyAsync(cell, args.TaskId, args.Reply);
        cell.AgentClarifyDone += async (_, args) => await HandleClarifyDoneAsync(cell, args.TaskId);
        cell.ArchiveProjectRequested += async (_, item) =>
        {
            if (item.IsLimboCell)
            {
                return;
            }

            await ArchiveEntityAsync(item.IsTaskCell ? "task" : "project", item.Id);
        };
        cell.MergeProjectRequested += async (_, item) =>
        {
            if (item.IsLimboCell || item.IsTaskCell)
            {
                return;
            }

            await MergeProjectIntoAsync(item);
        };
        cell.AccentColorRequested += async (_, args) => await SetProjectAccentAsync(cell, args);
        cell.SetHomeFolderRequested += async (_, project) => await SetProjectHomeFolderAsync(project);
        cell.OpenHomeFolderRequested += async (_, project) => await OpenProjectHomeFolderAsync(project);
        cell.LayoutChanged += async (_, args) => await HandleCellLayoutChangedAsync(cell, args);
        cell.TitleCommitted += async (_, args) => await HandleCellTitleCommittedAsync(cell, args.Cell, args.Name);
    }

    private async Task SetProjectHomeFolderAsync(ProjectCellVm project)
    {
        if (project.IsTaskCell || project.IsLimboCell)
        {
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (App.MainWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                WorkbenchHint.Text = "Home folder cancelled.";
                return;
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.SetProjectHomeFolderAsync(project.Id, folder.Path);
            if (result is null)
            {
                WorkbenchHint.Text = "Could not set home folder.";
                return;
            }

            WorkbenchHint.Text =
                $"Home set · {result.RootPath} · indexed {result.IndexedCount} · sandbox {result.OrbitSandboxPath}";
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Home folder failed: {ex.Message}";
        }
    }

    private async Task OpenProjectHomeFolderAsync(ProjectCellVm project)
    {
        if (project.IsTaskCell || project.IsLimboCell)
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var home = await client.GetProjectHomeFolderAsync(project.Id);
            if (home is null || string.IsNullOrWhiteSpace(home.RootPath))
            {
                WorkbenchHint.Text = "No home folder yet — use Set home folder…";
                return;
            }

            if (!Directory.Exists(home.RootPath))
            {
                WorkbenchHint.Text = "Home folder is missing on disk.";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = home.RootPath,
                UseShellExecute = true,
            });
            WorkbenchHint.Text = $"Opened {home.RootPath}";
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Open home failed: {ex.Message}";
        }
    }

    private async Task HandleCellTitleCommittedAsync(ProjectCellControl control, ProjectCellVm cell, string name)
    {
        if (cell.IsLimboCell)
        {
            control.Bind(cell);
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = cell.IsTaskCell
                ? await client.UpdateTaskAsync(cell.Id, title: name)
                : await client.UpdateProjectAsync(cell.Id, name: name);
            if (!ok)
            {
                WorkbenchHint.Text = "Could not save name.";
                control.Bind(cell);
                return;
            }

            cell.Name = name;
            control.Bind(cell);
            WorkbenchHint.Text = "Name saved.";
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Could not save name.";
            control.Bind(cell);
        }
    }

    private async Task HandleCellLayoutChangedAsync(ProjectCellControl control, CellLayoutChangedEventArgs args)
    {
        var w = Math.Clamp(args.Width, 200, 960);
        var h = Math.Clamp(args.Height, 180, 800);
        var boardWidth = WorkbenchPacker.BoardWidth(CellScroller.ActualWidth);
        var all = EnumerateCellVms().ToList();
        if (all.Count == 0)
        {
            return;
        }

        args.Cell.BoardW = w;
        args.Cell.BoardH = h;

        List<ProjectCellVm> ordered;
        if (args.IsResize)
        {
            ordered = WorkbenchPacker.PackProjectsAndPinLimbo(all, boardWidth, BoardViewportHeight);
            ApplyAllCellPositions(ordered, dragged: null, followFinger: false);
        }
        else
        {
            // Home-screen drag: finger follows the cell; others reflow around the insert slot.
            // Limbo stays free-positioned / re-pinned bottom-right after project moves.
            ordered = WorkbenchPacker.ReorderWithDrag(
                all,
                args.Cell,
                args.X + (w / 2),
                args.Y + (h / 2),
                boardWidth,
                BoardViewportHeight);
            if (args.IsComplete)
            {
                if (!args.Cell.IsLimboCell)
                {
                    var limbo = ordered.FirstOrDefault(c => c.IsLimboCell);
                    var projects = ordered.Where(c => !c.IsLimboCell).ToList();
                    if (limbo is not null)
                    {
                        WorkbenchPacker.PinLimboBottomRight(limbo, projects, boardWidth, BoardViewportHeight);
                    }
                }

                ApplyAllCellPositions(ordered, dragged: null, followFinger: false);
            }
            else
            {
                ApplyAllCellPositions(ordered, args.Cell, followFinger: true, fingerX: args.X, fingerY: args.Y);
            }
        }

        if (!args.IsComplete)
        {
            return;
        }

        await PersistAllLayoutsAsync(ordered);
    }

    private void ProjectCell_Loaded(object sender, RoutedEventArgs e)
    {
        // Cells are wired in RebuildCellBoard; keep handler for any leftover XAML references.
    }

    private async Task SetProjectAccentAsync(ProjectCellControl cell, ProjectAccentRequestedEventArgs args)
    {
        if (args.Cell.IsTaskCell || args.Cell.IsLimboCell)
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (!await client.SetProjectAccentAsync(args.Cell.Id, args.AccentColor))
            {
                WorkbenchHint.Text = "Could not save stripe color.";
                return;
            }

            cell.ApplyAccentStripe(args.AccentColor);
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Could not save stripe color.";
        }
    }

    private async void AgentInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(AgentInputBox.Text))
            {
                AgentInputBox.Text = string.Empty;
            }

            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Enter || _agentBusy)
        {
            return;
        }

        e.Handled = true;
        await SubmitAgentRailAsync();
    }

    private async void AgentSendButton_Click(object sender, RoutedEventArgs e) =>
        await SubmitAgentRailAsync();

    private async Task SubmitAgentRailAsync()
    {
        var text = AgentInputBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || _agentBusy)
        {
            return;
        }

        AgentInputBox.Text = string.Empty;
        await HandleAgentRailCommandAsync(text);
    }

    private async Task HandleAgentRailCommandAsync(string text)
    {
        // Legacy chat rail stays hidden; replies surface under the capture box + status line.
        EnsureAgentRailVisible();
        AgentQuickReply.Visibility = Visibility.Collapsed;
        AgentQuickReply.Text = string.Empty;

        if (TryParseNewProjectIntent(text, out var projectName) && !string.IsNullOrWhiteSpace(projectName))
        {
            WorkbenchHint.Text = "Creating project…";
            try
            {
                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                var created = await client.CreateProjectAsync(projectName!);
                if (created is null)
                {
                    ShowQuickReply("Could not create project.");
                    WorkbenchHint.Text = "Could not create project.";
                    return;
                }

                ShowQuickReply($"Created project “{created.Name}”.");
                WorkbenchHint.Text = $"Created project {created.Name}.";
                await ReloadWorkbenchAsync();
            }
            catch (Exception ex)
            {
                ShowQuickReply(ex.Message);
                WorkbenchHint.Text = $"New project failed: {ex.Message}";
            }

            return;
        }

        var askHermes = TryParseAskHermes(text, out var askText);
        var captureTarget = ResolveQuickCaptureTarget();
        if (!askHermes && captureTarget is not null)
        {
            WorkbenchHint.Text = $"Capturing to {captureTarget.Value.ProjectName}…";
            try
            {
                await CaptureAsync(text, captureTarget.Value.ProjectId, captureTarget.Value.ProjectName);
                if (string.IsNullOrWhiteSpace(WorkbenchHint.Text)
                    || WorkbenchHint.Text.StartsWith("Capturing", StringComparison.Ordinal))
                {
                    WorkbenchHint.Text = $"Captured to {captureTarget.Value.ProjectName}.";
                }

                ShowQuickReply($"Captured → {captureTarget.Value.ProjectName}");
            }
            catch (Exception ex)
            {
                ShowQuickReply(ex.Message);
                WorkbenchHint.Text = ex.Message;
            }

            return;
        }

        var prompt = askHermes ? askText : text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            WorkbenchHint.Text = "Type a note to capture, or ? followed by a question for Hermes.";
            return;
        }

        _agentBusy = true;
        AgentSendButton.IsEnabled = false;
        AgentRailStatus.Text = "Thinking…";
        WorkbenchHint.Text = "Asking Hermes…";
        ShowQuickReply("…");

        try
        {
            var reply = await AskLimboHermesAsync(prompt, _agentHermesHistory);
            var display = string.IsNullOrWhiteSpace(reply)
                ? "No reply from Hermes. Check Settings → Hermes."
                : reply.Trim();
            ShowQuickReply(display);
            AgentRailStatus.Text = "new project · ? ask · select project to capture";
            WorkbenchHint.Text = "Hermes replied.";
            _ = SoftRefreshAfterAgentAsync(display);
        }
        catch (Exception ex)
        {
            ShowQuickReply(ex.Message);
            AgentRailStatus.Text = "Error";
            WorkbenchHint.Text = $"Agent command failed: {ex.Message}";
        }
        finally
        {
            _agentBusy = false;
            AgentSendButton.IsEnabled = true;
        }
    }

    private void ShowQuickReply(string text)
    {
        AgentQuickReply.Text = text;
        AgentQuickReply.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static bool TryParseAskHermes(string text, out string askText)
    {
        askText = text;
        if (text.StartsWith('?'))
        {
            askText = text[1..].Trim();
            return askText.Length > 0;
        }

        if (text.StartsWith("ask ", StringComparison.OrdinalIgnoreCase))
        {
            askText = text[4..].Trim();
            return askText.Length > 0;
        }

        if (text.StartsWith("hermes ", StringComparison.OrdinalIgnoreCase))
        {
            askText = text[7..].Trim();
            return askText.Length > 0;
        }

        return false;
    }

    private (string? ProjectId, string ProjectName)? ResolveQuickCaptureTarget()
    {
        if (_selectedNode is null)
        {
            return null;
        }

        return _selectedNode.Kind switch
        {
            OrbitTreeNodeKind.Project when !string.IsNullOrWhiteSpace(_selectedNode.Id)
                => (_selectedNode.Id, _selectedNode.Title),
            OrbitTreeNodeKind.Task or OrbitTreeNodeKind.Subtask or OrbitTreeNodeKind.Completed
                when !string.IsNullOrWhiteSpace(_selectedNode.ProjectId)
                => (_selectedNode.ProjectId!,
                    FindProjectTitle(_selectedNode.ProjectId!) ?? "project"),
            OrbitTreeNodeKind.Limbo => (null, "Limbo"),
            _ => null,
        };
    }

    private string? FindProjectTitle(string projectId)
    {
        foreach (var root in _treeRoots)
        {
            if (root.Kind == OrbitTreeNodeKind.Project && root.Id == projectId)
            {
                return root.Title;
            }
        }

        if (_nodesById.TryGetValue(projectId, out var node)
            && node.Kind == OrbitTreeNodeKind.Project)
        {
            return node.Title;
        }

        return null;
    }

    private void AppendAgentBubble(string role, string text)
    {
        _agentBubbles.Add(new WorkbenchAgentBubbleVm { RoleLabel = role, Text = text });
        ScrollAgentRailToEnd();
    }

    private void ScrollAgentRailToEnd()
    {
        if (_agentBubbles.Count == 0)
        {
            return;
        }

        AgentMessageList.ScrollIntoView(_agentBubbles[^1]);
    }

    private static bool TryParseNewProjectIntent(string text, out string? name)
    {
        name = null;
        var trimmed = text.Trim();
        var match = Regex.Match(
            trimmed,
            @"^(?:start\s+)?new\s+project(?:\s+[""']?(.+?)[""']?)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value.Trim()
            : "Untitled project";
        return true;
    }

    private async Task SoftRefreshAfterAgentAsync(string? hermesReply)
    {
        if (string.IsNullOrWhiteSpace(hermesReply))
        {
            return;
        }

        // Skip refresh when Hermes clearly failed to reach Core / tools.
        if (hermesReply.Contains("isn't reachable", StringComparison.OrdinalIgnoreCase)
            || hermesReply.Contains("not reachable", StringComparison.OrdinalIgnoreCase)
            || hermesReply.Contains("can't reach", StringComparison.OrdinalIgnoreCase)
            || hermesReply.Contains("cannot reach", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await ReloadWorkbenchAsync();
        }
        catch (Exception)
        {
            // best-effort UI sync
        }
    }

    private async Task<string> AskLimboHermesAsync(string userText, List<HermesChatMessage> history)
    {
        if (!HermesUrlValidation.TryValidate(App.Settings.HermesBaseUrl, out var url, out var error))
        {
            return error ?? "Configure Hermes URL in Settings to use the agent rail.";
        }

        var key = App.SettingsStore.ReadHermesApiKey(App.Settings);
        using var client = new HermesHttpClient(new Uri(url!), key);
        if (history.Count == 0)
        {
            history.Add(new HermesChatMessage
            {
                Role = "system",
                Content =
                    OrbitRuntimeContextProvider.Instance.Capture().ToSystemPrompt()
                    + "\nYou are the Orbit workbench agent. Be concise. "
                    + "You have full Orbit access through MCP tools (mcp__orbit__*). "
                    + "Do not invent app state — call tools. "
                    + "To remove a capture line or task, call orbit_get_workbench (or orbit_get_project) to find the task id, "
                    + "then orbit_archive_entity with entityType=task (captures on project cells are tasks). "
                    + "Notes use entityType=note. "
                    + "If Core is unreachable, say so and tell the user to set ORBIT_CORE_URL on the Hermes host "
                    + "to the Windows LAN address (not 127.0.0.1) and reload MCP.",
            });
        }

        history.Add(new HermesChatMessage { Role = "user", Content = userText });
        var buffer = new StringBuilder();
        await foreach (var delta in client.StreamChatAsync(new HermesChatRequest
        {
            Messages = history,
            Stream = true,
        }))
        {
            if (delta.Kind == HermesChatDeltaKind.Error)
            {
                return delta.Text ?? "Hermes error";
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

        var final = buffer.ToString().Trim();
        if (final.Length > 0)
        {
            history.Add(new HermesChatMessage { Role = "assistant", Content = final });
        }

        return final;
    }

    private async Task ShowLimboLineActionsAsync(ProjectCellControl cell, string noteId)
    {
        var line = EnumerateCellVms()
            .FirstOrDefault(c => c.IsLimboCell)
            ?.Lines.FirstOrDefault(l => l.TaskId == noteId);
        if (line is null)
        {
            return;
        }

        var menu = new MenuFlyout();
        var assign = new MenuFlyoutItem { Text = "Assign…", Tag = noteId };
        assign.Click += LimboAssign_Click;
        menu.Items.Add(assign);

        if (!string.IsNullOrWhiteSpace(line.Body))
        {
            var accept = new MenuFlyoutItem { Text = "Accept suggestion", Tag = line.Body };
            accept.Click += LimboAcceptSuggestion_Click;
            var reject = new MenuFlyoutItem { Text = "Reject suggestion", Tag = line.Body };
            reject.Click += LimboRejectSuggestion_Click;
            menu.Items.Add(accept);
            menu.Items.Add(reject);
        }

        var archive = new MenuFlyoutItem { Text = "Archive", Tag = noteId };
        archive.Click += LimboArchive_Click;
        menu.Items.Add(archive);
        menu.ShowAt(cell);
        await Task.CompletedTask;
    }

    /// <summary>
    /// On a project board, "Capture a line" under a task adds a related task as a line — not a sibling cell.
    /// </summary>
    private async Task CaptureLineOnTaskCellAsync(
        string text,
        ProjectCellVm parentTask,
        ProjectCellControl control)
    {
        var projectId = _scopeProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            WorkbenchHint.Text = "Capture failed — leave and re-enter the board.";
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.CreateNoteAsync(text, projectId);
            if (result is null || string.IsNullOrWhiteSpace(result.TaskId))
            {
                WorkbenchHint.Text = "Capture failed.";
                return;
            }

            await client.LinkTasksAsync(
                predecessorTaskId: parentTask.Id,
                successorTaskId: result.TaskId!,
                dependencyType: Orbit.Core.Data.TaskDependencyTypes.Relates,
                reason: "Captured as a line on the task board");

            var line = new CellLineVm
            {
                TaskId = result.TaskId!,
                Title = result.OriginalText,
                Status = "not_started",
            };
            TryPrependLineOnVisibleCell(parentTask.Id, line);
            _ = SoftRefreshLimboAsync();
            _ = ShowCaptureAgentNudgeAsync(
                text,
                _scopeProjectName ?? parentTask.Name,
                control,
                projectId,
                result.TaskId);
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Capture failed.";
        }
    }

    private async Task CaptureAsync(
        string text,
        string? projectId,
        string? projectName = null,
        ProjectCellControl? sourceCell = null,
        CoreHostClient.EmailIngestResult? linkEmail = null)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.CreateNoteAsync(text, projectId);
            if (result is null)
            {
                WorkbenchHint.Text = "Capture failed.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.TaskId)
                && linkEmail is not null
                && !string.IsNullOrWhiteSpace(linkEmail.ConversationId))
            {
                await client.LinkEmailThreadAsync(
                    result.TaskId!,
                    linkEmail.ConversationId!,
                    linkEmail.Id);
            }

            if (string.IsNullOrWhiteSpace(result.TaskId) || string.IsNullOrWhiteSpace(projectId))
            {
                if (result.IsLimbo
                    && !string.IsNullOrWhiteSpace(result.NoteId)
                    && linkEmail is not null)
                {
                    _limboEmailByNoteId[result.NoteId] = linkEmail;
                }

                if (result.IsLimbo)
                {
                    await Task.Delay(400);
                }

                await ReloadWorkbenchAsync();
                if (result.IsLimbo)
                {
                    WorkbenchHint.Text = linkEmail is not null
                        ? "Email parked in Limbo — use Assign to put it on a project."
                        : "Parked in Limbo — use Assign to put it on a project.";
                }

                return;
            }

            // Root board: prepend onto the project cell.
            if (EnumerateCellVms().Any(c => c.Id == projectId && !c.IsTaskCell))
            {
                var cellVm = EnumerateCellVms().First(c => c.Id == projectId);
                var line = new CellLineVm
                {
                    TaskId = result.TaskId!,
                    Title = result.OriginalText,
                    Status = "not_started",
                };
                TryPrependLineOnVisibleCell(projectId!, line);
                _ = SoftRefreshLimboAsync();
                _ = ShowCaptureAgentNudgeAsync(
                    text,
                    projectName ?? cellVm.Name,
                    sourceCell,
                    projectId,
                    result.TaskId);
                return;
            }

            // Project board: new top-level task becomes its own cell; clarify on that cell.
            if (string.Equals(_scopeProjectId, projectId, StringComparison.Ordinal))
            {
                await ReloadWorkbenchAsync();
                var newCell = FindCellControl(result.TaskId!);
                _ = ShowCaptureAgentNudgeAsync(
                    text,
                    projectName ?? _scopeProjectName ?? "project",
                    newCell,
                    projectId,
                    result.TaskId);
                return;
            }

            if (result.IsLimbo)
            {
                await Task.Delay(400);
            }

            await ReloadWorkbenchAsync();
            if (!string.IsNullOrWhiteSpace(projectName)
                && (string.IsNullOrWhiteSpace(WorkbenchHint.Text)
                    || WorkbenchHint.Text.StartsWith("Capturing", StringComparison.Ordinal)))
            {
                WorkbenchHint.Text = $"Captured to {projectName}.";
            }
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Capture failed.";
        }
    }

    private async Task ShowCaptureAgentNudgeAsync(
        string captureText,
        string projectName,
        ProjectCellControl? sourceCell,
        string? projectId,
        string? taskId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        try
        {
            var session = new CaptureClarifySession
            {
                ProjectId = projectId,
                ProjectName = projectName,
                TaskId = taskId,
                OriginalCapture = captureText,
            };
            _clarifySessions[taskId] = session;
            OrbitRuntimeContextProvider.Instance.SetFocus(projectId, projectName, taskId);

            var cell = sourceCell ?? FindCellControl(projectId);
            var local = CaptureClarify.Open(captureText, projectName);
            cell?.BeginAgentClarify(taskId, local.Message);

            var opened = await CaptureClarifyHermes.OpenAsync(
                captureText,
                projectName,
                App.Settings.HermesBaseUrl,
                App.SettingsStore.ReadHermesApiKey(App.Settings),
                session.HermesHistory);

            if (opened.IsComplete && !string.IsNullOrWhiteSpace(opened.FinalTitle))
            {
                await CommitClarifyAsync(cell, session, opened);
                return;
            }

            if (!string.Equals(opened.Message, local.Message, StringComparison.Ordinal))
            {
                cell?.SetAgentClarifyMessage(opened.Message);
            }
        }
        catch (Exception)
        {
            // Non-fatal; capture already succeeded.
        }
    }

    private async Task HandleClarifyReplyAsync(ProjectCellControl cell, string taskId, string reply)
    {
        if (!_clarifySessions.TryGetValue(taskId, out var session))
        {
            cell.CloseAgentClarify();
            return;
        }

        cell.SetAgentClarifyMessage("Thinking…", busy: true);
        try
        {
            var result = await CaptureClarifyHermes.ContinueAsync(
                session.OriginalCapture,
                session.ProjectName,
                reply,
                session.UserReplies,
                App.Settings.HermesBaseUrl,
                App.SettingsStore.ReadHermesApiKey(App.Settings),
                session.HermesHistory);

            session.UserReplies.Add(reply);

            if (result.IsComplete && !string.IsNullOrWhiteSpace(result.FinalTitle))
            {
                await CommitClarifyAsync(cell, session, result);
                return;
            }

            // Safety: after max replies, force finalize even if Hermes keeps asking.
            if (session.UserReplies.Count >= CaptureClarify.MaxUserReplies)
            {
                var forced = await CaptureClarifyHermes.FinishAsync(
                    session.OriginalCapture,
                    session.ProjectName,
                    session.UserReplies,
                    App.Settings.HermesBaseUrl,
                    App.SettingsStore.ReadHermesApiKey(App.Settings),
                    session.HermesHistory);
                await CommitClarifyAsync(cell, session, forced);
                return;
            }

            cell.SetAgentClarifyMessage(result.Message);
        }
        catch (Exception)
        {
            var fallback = CaptureClarify.Continue(
                session.OriginalCapture,
                session.ProjectName,
                session.UserReplies,
                reply);
            session.UserReplies.Add(reply);
            if (fallback.IsComplete && !string.IsNullOrWhiteSpace(fallback.FinalTitle))
            {
                await CommitClarifyAsync(cell, session, fallback);
            }
            else
            {
                cell.SetAgentClarifyMessage(fallback.Message);
            }
        }
    }

    private async Task HandleClarifyDoneAsync(ProjectCellControl cell, string taskId)
    {
        if (!_clarifySessions.TryGetValue(taskId, out var session))
        {
            cell.CloseAgentClarify();
            return;
        }

        cell.SetAgentClarifyMessage("Finishing…", busy: true);
        CaptureClarifyResult result;
        try
        {
            result = await CaptureClarifyHermes.FinishAsync(
                session.OriginalCapture,
                session.ProjectName,
                session.UserReplies,
                App.Settings.HermesBaseUrl,
                App.SettingsStore.ReadHermesApiKey(App.Settings),
                session.HermesHistory);
        }
        catch (Exception)
        {
            result = CaptureClarify.Finalize(session.OriginalCapture, session.ProjectName, session.UserReplies);
        }

        await CommitClarifyAsync(cell, session, result);
    }

    private async Task CommitClarifyAsync(
        ProjectCellControl? cell,
        CaptureClarifySession session,
        CaptureClarifyResult result)
    {
        var title = CaptureClarify.SanitizeTitle(
            string.IsNullOrWhiteSpace(result.FinalTitle)
                ? CaptureClarify.ComposeFinalTitle(session.OriginalCapture, session.UserReplies)
                : result.FinalTitle!);

        var subtitle = !string.IsNullOrWhiteSpace(result.Note)
            ? result.Note
            : CaptureClarify.ComposeSubtitle(session.UserReplies);

        var summary = !string.IsNullOrWhiteSpace(result.Summary)
            ? result.Summary
            : CaptureClarify.ComposeSummary(session.OriginalCapture, session.UserReplies);

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            await client.UpdateTaskAsync(
                session.TaskId,
                title: title,
                nextAction: subtitle,
                body: summary);

            cell ??= FindCellControl(session.ProjectId);
            cell?.UpdateLine(session.TaskId, title, subtitle);
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Could not save clarified line.";
            cell ??= FindCellControl(session.ProjectId);
            cell?.UpdateLine(session.TaskId, title, subtitle);
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            cell ??= FindCellControl(session.ProjectId);
            cell?.SetAgentClarifyMessage($"Locked in:\n{title}", busy: true);
            await Task.Delay(900);
        }

        cell ??= FindCellControl(session.ProjectId);
        cell?.CloseAgentClarify();
        _clarifySessions.Remove(session.TaskId);
        OrbitRuntimeContextProvider.Instance.SetFocus(session.ProjectId, session.ProjectName, session.TaskId);
    }

    private IEnumerable<ProjectCellVm> EnumerateCellVms() =>
        CellBoard.Children.OfType<ProjectCellControl>()
            .Select(c => c.DataContext)
            .OfType<ProjectCellVm>();

    private ProjectCellControl? FindCellControl(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return _wiredCells.FirstOrDefault(c =>
            c.DataContext is ProjectCellVm vm && vm.Id == projectId);
    }

    private void TryPrependLineOnVisibleCell(string projectId, CellLineVm line)
    {
        var control = FindCellControl(projectId);
        if (control is not null)
        {
            control.PrependLine(line);
            return;
        }

        var vm = EnumerateCellVms().FirstOrDefault(c => c.Id == projectId);
        if (vm is null)
        {
            return;
        }

        var mutable = vm.Lines as List<CellLineVm> ?? vm.Lines.ToList();
        mutable.RemoveAll(l => l.TaskId == line.TaskId);
        mutable.Insert(0, line);
        vm.Lines = mutable;
    }

    private async Task SoftRefreshLimboAsync()
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var snapshot = await client.GetWorkbenchAsync(_scopeProjectId);
            if (snapshot is null)
            {
                return;
            }

            if (!snapshot.IsProjectScoped)
            {
                var limboCount = snapshot.Limbo.Count;
                WorkbenchHint.Text = limboCount == 0
                    ? "Drop a folder to create a project · Limbo holds unassigned captures."
                    : $"{limboCount} in Limbo · Assign or Archive on a Limbo line.";

                var limboVm = EnumerateCellVms().FirstOrDefault(c => c.IsLimboCell);
                var freshLimbo = snapshot.Cells.FirstOrDefault(c => c.IsLimboCell);
                if (limboVm is not null && freshLimbo is not null)
                {
                    limboVm.Lines = freshLimbo.Lines.ToList();
                    limboVm.PendingSuggestionCount = freshLimbo.PendingSuggestionCount;
                    limboVm.Summary = freshLimbo.Summary;
                    limboVm.RecentActivityAt = freshLimbo.RecentActivityAt;
                    FindCellControl(WorkbenchCellKinds.LimboEntityId)?.Bind(limboVm);
                }
            }

            // Patch suggestion badges on existing cells without rebuilding the board.
            foreach (var cell in EnumerateCellVms())
            {
                if (cell.IsLimboCell)
                {
                    continue;
                }

                var fresh = snapshot.Cells.FirstOrDefault(c => c.Id == cell.Id);
                if (fresh is null)
                {
                    continue;
                }

                cell.PendingSuggestionCount = fresh.PendingSuggestionCount;
                cell.OpenBlockerCount = fresh.OpenBlockerCount;
                cell.TopBlockerSummary = fresh.TopBlockerSummary;
                cell.UpcomingMeetingTitle = fresh.UpcomingMeetingTitle;
                cell.UpcomingMeetingStartsAt = fresh.UpcomingMeetingStartsAt;
                cell.RecentActivityAt = fresh.RecentActivityAt;
                FindCellControl(cell.Id)?.Bind(cell);
            }
        }
        catch (Exception)
        {
            // best-effort soft refresh
        }
    }

    private static void OpenConcernOnPulse(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        if (App.MainWindow is MainWindow { Shell: ShellPage shell })
        {
            shell.OpenConcernBrief(taskId);
        }
    }

    private async Task OpenDetailNearAsync(FrameworkElement anchor, string projectId, string? taskId)
    {
        try
        {
            _detailPanel ??= new WorkbenchDetailPanel();
            _detailPanel.CloseRequested -= DetailPanel_CloseRequested;
            _detailPanel.ContentChanged -= DetailPanel_ContentChanged;
            _detailPanel.CloseRequested += DetailPanel_CloseRequested;
            _detailPanel.ContentChanged += DetailPanel_ContentChanged;

            if (!string.IsNullOrWhiteSpace(taskId))
            {
                await _detailPanel.LoadTaskAsync(projectId, taskId);
            }
            else
            {
                await _detailPanel.LoadProjectAsync(projectId);
            }

            _detailFlyout ??= CreateDetailFlyout();
            _detailFlyout.Closing -= DetailFlyout_Closing;
            _detailFlyout.Closing += DetailFlyout_Closing;
            _detailFlyout.Content = _detailPanel;
            _allowDetailFlyoutClose = false;
            _detailFlyout.ShowAt(
                anchor,
                new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
                    ShowMode = FlyoutShowMode.Standard,
                });

            // Keep the legacy drawer available as a fallback overview when flyout is used —
            // hide it so focus stays near the cell.
            CloseDrawer();
            _drawerProjectId = projectId;
            _drawerTaskId = taskId;
        }
        catch (Exception)
        {
            // Fallback to old drawer path
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                await OpenTaskDetailAsync(projectId, taskId);
            }
            else
            {
                await OpenProjectContextAsync(projectId);
            }
        }
    }

    private async Task OpenLimboDetailNearAsync(ProjectCellControl cell, string noteId)
    {
        try
        {
            _detailPanel ??= new WorkbenchDetailPanel();
            _detailPanel.CloseRequested -= DetailPanel_CloseRequested;
            _detailPanel.ContentChanged -= DetailPanel_ContentChanged;
            _detailPanel.CloseRequested += DetailPanel_CloseRequested;
            _detailPanel.ContentChanged += DetailPanel_ContentChanged;

            await _detailPanel.LoadLimboNoteAsync(noteId);

            _detailFlyout ??= CreateDetailFlyout();
            _detailFlyout.Closing -= DetailFlyout_Closing;
            _detailFlyout.Closing += DetailFlyout_Closing;
            _detailFlyout.Content = _detailPanel;
            _allowDetailFlyoutClose = false;
            _detailFlyout.ShowAt(
                cell,
                new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
                    ShowMode = FlyoutShowMode.Standard,
                });
            CloseDrawer();
            _drawerProjectId = null;
            _drawerTaskId = null;
        }
        catch (Exception)
        {
            await ShowLimboLineActionsAsync(cell, noteId);
        }
    }

    private static Flyout CreateDetailFlyout()
    {
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(0)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, 520d));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MaxHeightProperty, 760d));
        presenterStyle.Setters.Add(
            new Setter(FlyoutPresenter.BackgroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = presenterStyle,
        };
        // Closing handler attached once from OpenDetailNearAsync after CreateDetailFlyout.
        return flyout;
    }

    private void DetailFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        // Stay open on outside click / light-dismiss; only X / delete / archive may close.
        if (!_allowDetailFlyoutClose)
        {
            args.Cancel = true;
        }
    }

    private void CloseDetailFlyout()
    {
        if (_detailFlyout is null)
        {
            return;
        }

        _allowDetailFlyoutClose = true;
        try
        {
            _detailFlyout.Hide();
        }
        finally
        {
            _allowDetailFlyoutClose = false;
        }
    }

    private void DetailPanel_CloseRequested(object? sender, EventArgs e)
    {
        if (DetailHost.Visibility == Visibility.Visible)
        {
            DetailHost.Content = null;
            DetailHost.Visibility = Visibility.Collapsed;
            DetailEmptyText.Visibility = Visibility.Visible;
            DetailEmptyText.Text = "Select a project or task";
            // Keep tree selection — Add task / Add subtask stay available.
            SyncAddButtons(_selectedNode);
            return;
        }

        CloseDetailFlyout();
    }

    private async void DetailPanel_ContentChanged(object? sender, EventArgs e)
    {
        await ReloadWorkbenchAsync();
    }

    private async Task ArchiveEntityAsync(string entityType, string entityId)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (await client.ArchiveEntityAsync(entityType, entityId))
            {
                CloseDetailFlyout();
                CloseDrawer();
                await ReloadWorkbenchAsync();
                WorkbenchHint.Text = entityType == "project" ? "Project archived." : "Archived.";
            }
            else
            {
                WorkbenchHint.Text = "Archive failed.";
            }
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Archive failed.";
        }
    }

    private async Task MergeProjectIntoAsync(ProjectCellVm source)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var projects = await client.GetProjectsAsync();
            var choices = (projects ?? [])
                .Where(p => !string.Equals(p.Id, source.Id, StringComparison.Ordinal))
                .Select(p => new ProjectPickUi.Choice { Id = p.Id, Name = p.Name })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (choices.Count == 0)
            {
                WorkbenchHint.Text = "No other project to merge into.";
                return;
            }

            var targetId = await ProjectPickUi.ShowPickerAsync(
                XamlRoot,
                choices,
                "Merge project into…",
                $"Choose the project that should keep the work from “{source.Name}”. The source will be archived.");
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return;
            }

            var preview = await client.PreviewMergeProjectAsync(source.Id, targetId);
            if (preview is null)
            {
                WorkbenchHint.Text = "Could not preview merge.";
                return;
            }

            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = $"Merge “{preview.SourceName}” into “{preview.TargetName}”?",
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = preview.CountsLine,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });
            if (preview.Warnings.Count > 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = string.Join("\n", preview.Warnings),
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var confirm = new ContentDialog
            {
                Title = "Confirm merge",
                Content = body,
                PrimaryButtonText = preview.Warnings.Count > 0 ? "Merge anyway" : "Merge",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var force = preview.Warnings.Count > 0;
            var result = await client.MergeProjectAsync(source.Id, targetId, force);
            if (result is null)
            {
                WorkbenchHint.Text = "Merge failed.";
                return;
            }

            CloseDetailFlyout();
            CloseDrawer();
            await ReloadWorkbenchAsync();
            WorkbenchHint.Text = $"Merged “{result.SourceName}” into “{result.TargetName}”.";
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Merge failed: {ex.Message}";
        }
    }

    private async Task OpenProjectContextAsync(string projectId, string? focusTaskId = null)
    {
        if (!string.IsNullOrWhiteSpace(focusTaskId))
        {
            await OpenTaskDetailAsync(projectId, focusTaskId);
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var context = await client.GetProjectContextAsync(projectId);
            if (context is null)
            {
                return;
            }

            _drawerProjectId = projectId;
            _drawerTaskId = null;
            DrawerTitle.Text = context.Name;
            OrbitRuntimeContextProvider.Instance.SetFocus(projectId, context.Name);
            DrawerBody.Children.Clear();

            if (!string.IsNullOrWhiteSpace(context.Summary))
            {
                DrawerBody.Children.Add(Section("Summary", [context.Summary!]));
            }

            if (context.Tasks.Count > 0)
            {
                DrawerBody.Children.Add(TaskListSection(projectId, context.Tasks));
            }

            if (context.Blockers.Count > 0)
            {
                DrawerBody.Children.Add(Section("Blockers", context.Blockers));
            }

            if (context.Notes.Count > 0)
            {
                DrawerBody.Children.Add(Section("Notes", context.Notes.Select(n => n.Text)));
            }

            if (context.Contacts.Count > 0)
            {
                DrawerBody.Children.Add(Section(
                    "Contacts",
                    context.Contacts.Select(c => c.DisplayName)));
            }

            if (context.Meetings.Count > 0)
            {
                DrawerBody.Children.Add(Section("Meetings", context.Meetings));
            }

            if (context.Suggestions.Count > 0)
            {
                DrawerBody.Children.Add(SuggestionSection(context.Suggestions));
            }

            if (context.Files.Count > 0)
            {
                DrawerBody.Children.Add(RelatedFilesSection(context.Files));
            }

            if (DrawerBody.Children.Count == 0)
            {
                DrawerBody.Children.Add(new TextBlock
                {
                    Text = "No additional context yet.",
                    Opacity = 0.7,
                });
            }

            ShowDrawer(340);
        }
        catch (Exception)
        {
            // keep workbench usable
        }
    }

    private async Task OpenTaskDetailAsync(string projectId, string taskId)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var context = await client.GetProjectContextAsync(projectId);
            if (context is null)
            {
                return;
            }

            var task = context.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task is null)
            {
                // Optimistic line may exist on cell before full context refresh.
                if (EnumerateCellVms().FirstOrDefault(c => c.Id == projectId) is { } cell)
                {
                    task = cell.Lines.FirstOrDefault(l => l.TaskId == taskId);
                }
            }

            if (task is null)
            {
                await OpenProjectContextAsync(projectId);
                return;
            }

            _drawerProjectId = projectId;
            _drawerTaskId = taskId;
            DrawerTitle.Text = task.Title;
            OrbitRuntimeContextProvider.Instance.SetFocus(projectId, context.Name, taskId);
            DrawerBody.Children.Clear();

            DrawerBody.Children.Add(new TextBlock
            {
                Text = context.Name,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
            });

            DrawerBody.Children.Add(TaskStatusSection(task));

            var agentSummary = new TextBlock
            {
                Text = "Agent is summarizing…",
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.9,
            };
            var agentPanel = new StackPanel { Spacing = 4 };
            agentPanel.Children.Add(new TextBlock
            {
                Text = "Agent summary",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            agentPanel.Children.Add(agentSummary);
            DrawerBody.Children.Add(agentPanel);

            var matchingNotes = context.Notes
                .Where(n => n.Text.Contains(task.Title, StringComparison.OrdinalIgnoreCase)
                            || task.Title.Contains(n.Text, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Text)
                .ToList();
            var notesToShow = matchingNotes.Count > 0
                ? matchingNotes
                : context.Notes.Take(8).Select(n => n.Text).ToList();
            DrawerBody.Children.Add(Section(
                matchingNotes.Count > 0 ? "Notes" : "Project notes",
                notesToShow.Count > 0 ? notesToShow : ["No notes yet — add one below."]));

            DrawerBody.Children.Add(TaskNoteCaptureSection(projectId, taskId));

            var taskBlockers = context.Blockers; // host returns summary strings only; show project blockers
            if (taskBlockers.Count > 0)
            {
                DrawerBody.Children.Add(Section("Blockers", taskBlockers));
            }

            if (context.Contacts.Count > 0)
            {
                DrawerBody.Children.Add(Section(
                    "Contacts",
                    context.Contacts.Select(c => c.DisplayName)));
            }
            else
            {
                DrawerBody.Children.Add(Section("Contacts", ["No linked contacts yet — Agent can attach people here."]));
            }

            if (context.Meetings.Count > 0)
            {
                DrawerBody.Children.Add(Section("Meetings", context.Meetings));
            }

            if (context.Suggestions.Count > 0)
            {
                DrawerBody.Children.Add(SuggestionSection(context.Suggestions));
            }

            if (context.Files.Count > 0)
            {
                DrawerBody.Children.Add(RelatedFilesSection(context.Files));
            }
            else
            {
                DrawerBody.Children.Add(Section("Files", ["No related files indexed for this project yet."]));
            }

            ShowDrawer(380);

            _ = FillTaskAgentSummaryAsync(agentSummary, context.Name, task, notesToShow);
        }
        catch (Exception)
        {
            // keep workbench usable
        }
    }

    private async Task FillTaskAgentSummaryAsync(
        TextBlock target,
        string projectName,
        CellLineVm task,
        IReadOnlyList<string> notes)
    {
        try
        {
            var summary = await CaptureAgentNudgeHermes.SummarizeTaskAsync(
                projectName,
                task.Title,
                task.Status,
                notes,
                App.Settings.HermesBaseUrl,
                App.SettingsStore.ReadHermesApiKey(App.Settings));
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_drawerTaskId == task.TaskId)
                {
                    target.Text = summary;
                }
            });
        }
        catch (Exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_drawerTaskId == task.TaskId)
                {
                    target.Text = $"{task.StatusLabel} · {task.Title}";
                }
            });
        }
    }

    private StackPanel TaskStatusSection(CellLineVm task)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "Status",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = task.TaskId,
        };
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

        var selected = statuses.Select((s, i) => (s, i))
            .FirstOrDefault(x => string.Equals(x.s.Id, task.Status, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = selected.s.Id is null ? 0 : selected.i;
        combo.SelectionChanged += TaskStatusCombo_SelectionChanged;
        panel.Children.Add(combo);

        if (!string.IsNullOrWhiteSpace(task.NextAction))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Next: {task.NextAction}",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
        }

        return panel;
    }

    private async void TaskStatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string status }, Tag: string taskId })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.UpdateTaskAsync(taskId, status: status);
            if (!ok)
            {
                WorkbenchHint.Text = "Could not update task status.";
                return;
            }

            PatchCellLineStatus(taskId, status);
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Could not update task status.";
        }
    }

    private void PatchCellLineStatus(string taskId, string status)
    {
        foreach (var cell in EnumerateCellVms())
        {
            foreach (var line in cell.Lines)
            {
                if (line.TaskId == taskId)
                {
                    line.Status = status;
                }
            }

            FindCellControl(cell.Id)?.Bind(cell);
        }
    }

    private StackPanel TaskListSection(string projectId, IEnumerable<CellLineVm> tasks)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Tasks / workstreams",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var task in tasks)
        {
            var button = new Button
            {
                Content = task.DisplayLine,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 4),
                Tag = $"{projectId}\n{task.TaskId}",
            };
            button.Click += async (_, _) =>
            {
                if (button.Tag is string tag)
                {
                    var parts = tag.Split('\n', 2);
                    if (parts.Length == 2)
                    {
                        await OpenTaskDetailAsync(parts[0], parts[1]);
                    }
                }
            };
            panel.Children.Add(button);
        }

        return panel;
    }

    private StackPanel TaskNoteCaptureSection(string projectId, string taskId)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Add note",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var box = new TextBox
        {
            PlaceholderText = "Note on this task…",
            Tag = (projectId, taskId),
        };
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                if (!string.IsNullOrEmpty(box.Text))
                {
                    box.Text = string.Empty;
                }

                e.Handled = true;
                return;
            }

            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            var text = box.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return;
            }

            e.Handled = true;
            box.Text = string.Empty;
            try
            {
                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                var result = await client.CreateNoteAsync(text, projectId);
                if (result is null)
                {
                    WorkbenchHint.Text = "Note save failed.";
                    return;
                }

                await OpenTaskDetailAsync(projectId, taskId);
            }
            catch (Exception)
            {
                WorkbenchHint.Text = "Note save failed.";
            }
        };
        panel.Children.Add(box);
        return panel;
    }

    private void ShowDrawer(double width)
    {
        DrawerHost.Visibility = Visibility.Visible;
    }

    private async void LimboArchive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string noteId } || string.IsNullOrWhiteSpace(noteId))
        {
            return;
        }

        _limboEmailByNoteId.Remove(noteId);
        await ArchiveEntityAsync("note", noteId);
    }

    private async void LimboAssign_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.Tag is not string noteId
            || string.IsNullOrWhiteSpace(noteId))
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var projects = await client.GetProjectsAsync();
            if (projects.Count == 0)
            {
                WorkbenchHint.Text = "No projects to assign to.";
                return;
            }

            var flyout = new MenuFlyout();
            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new MenuFlyoutItem
                {
                    Text = project.Name,
                    Tag = new LimboAssignTarget(noteId, project.Id, project.Name),
                };
                item.Click += LimboAssignProject_Click;
                flyout.Items.Add(item);
            }

            flyout.ShowAt(element);
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Could not load projects for Assign.";
        }
    }

    private async void LimboAssignProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LimboAssignTarget target })
        {
            return;
        }

        await AssignLimboNoteAsync(target.NoteId, target.ProjectId, target.ProjectName);
    }

    private async Task AssignLimboNoteAsync(string noteId, string projectId, string projectName)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.AssignLimboNoteAsync(noteId, projectId);
            if (result is null || string.IsNullOrWhiteSpace(result.TaskId))
            {
                WorkbenchHint.Text = "Assign failed.";
                return;
            }

            if (_limboEmailByNoteId.Remove(noteId, out var email)
                && !string.IsNullOrWhiteSpace(email.ConversationId))
            {
                await client.LinkEmailThreadAsync(result.TaskId!, email.ConversationId!, email.Id);
            }

            WorkbenchHint.Text = $"Assigned to {projectName}.";
            await ReloadWorkbenchAsync();

            var cell = FindCellControl(projectId);
            _ = ShowCaptureAgentNudgeAsync(
                result.OriginalText,
                projectName,
                cell,
                projectId,
                result.TaskId);
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Assign failed.";
        }
    }

    private async void LimboAcceptSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string suggestionId }
            || string.IsNullOrWhiteSpace(suggestionId))
        {
            return;
        }

        await DecideSuggestionAsync(suggestionId, accept: true);
    }

    private async void LimboRejectSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string suggestionId }
            || string.IsNullOrWhiteSpace(suggestionId))
        {
            return;
        }

        await DecideSuggestionAsync(suggestionId, accept: false);
    }

    private async Task DecideSuggestionAsync(string suggestionId, bool accept)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            string? applyProjectId = null;
            if (accept)
            {
                var pending = await client.GetPendingSuggestionsAsync();
                var match = pending.FirstOrDefault(s =>
                    string.Equals(s.Id, suggestionId, StringComparison.Ordinal));
                if (match is not null
                    && string.Equals(match.SuggestionType, "disambiguate_email_claim", StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(match.ProjectId))
                {
                    var choices = ProjectPickUi.MergeChoices(
                        ProjectPickUi.ParseCandidates(match.PayloadJson),
                        await ProjectPickUi.LoadActiveProjectsAsync(client));
                    applyProjectId = await ProjectPickUi.ShowPickerAsync(
                        XamlRoot,
                        choices,
                        title: "Pick a project",
                        message: "This email claim is ambiguous — choose a project to attach it to.");
                    if (string.IsNullOrWhiteSpace(applyProjectId))
                    {
                        WorkbenchHint.Text = "Pick a project to accept this claim.";
                        return;
                    }
                }
                else
                {
                    applyProjectId = match?.ProjectId;
                }
            }

            var ok = accept
                ? await client.AcceptSuggestionAsync(suggestionId, applyProjectId)
                : await client.RejectSuggestionAsync(suggestionId);
            WorkbenchHint.Text = ok
                ? (accept ? "Suggestion accepted." : "Suggestion rejected.")
                : "Suggestion update failed.";
            await ReloadWorkbenchAsync();
            if (DrawerHost.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(DrawerTitle.Text))
            {
                // refresh drawer if a project is open — best-effort via full reload of current title match
            }
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Suggestion update failed.";
        }
    }

    private async Task MoveTaskToProjectAsync(string taskId, string projectId, string projectName)
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var (ok, error) = await client.MoveTaskAsync(taskId, projectId);
            if (ok)
            {
                WorkbenchHint.Text = $"Moved task to {projectName}.";
                await ReloadWorkbenchAsync();
                return;
            }

            WorkbenchHint.Text = string.IsNullOrWhiteSpace(error)
                ? "Could not move task."
                : error;
        }
        catch (Exception ex)
        {
            WorkbenchHint.Text = $"Could not move task: {ex.Message}";
        }
    }

    private StackPanel RelatedFilesSection(IEnumerable<ContextFileVm> files)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "Related files",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var file in files)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = file.DisplayName,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
            });
            var open = new Button
            {
                Content = "Open",
                Padding = new Thickness(10, 2, 10, 2),
                Tag = file.Id,
            };
            open.Click += RelatedFileOpen_Click;
            var preview = new Button
            {
                Content = "Preview",
                Padding = new Thickness(10, 2, 10, 2),
                Tag = file.Id,
            };
            preview.Click += RelatedFilePreview_Click;
            row.Children.Add(open);
            row.Children.Add(preview);
            panel.Children.Add(row);
        }

        return panel;
    }

    private async void RelatedFileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fileId } || string.IsNullOrWhiteSpace(fileId))
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.OpenFileExternallyAsync(fileId);
            WorkbenchHint.Text = ok ? "Opened related file." : "Open related file failed.";
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Open related file failed.";
        }
    }

    private async void RelatedFilePreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fileId } || string.IsNullOrWhiteSpace(fileId))
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var text = await client.PreviewFileAsync(fileId);
            var dialog = new ContentDialog
            {
                Title = "File preview",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(text) ? "(No preview text)" : text,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        IsTextSelectionEnabled = true,
                    },
                    MaxHeight = 420,
                },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            WorkbenchHint.Text = "Preview related file failed.";
        }
    }

    private StackPanel SuggestionSection(IEnumerable<ContextSuggestionVm> suggestions)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "Agent suggestions",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var suggestion in suggestions)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = suggestion.Summary,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.9,
            });
            if (string.Equals(suggestion.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var accept = new Button { Content = "Accept", Padding = new Thickness(10, 2, 10, 2), Tag = suggestion.Id };
                accept.Click += LimboAcceptSuggestion_Click;
                var reject = new Button { Content = "Reject", Padding = new Thickness(10, 2, 10, 2), Tag = suggestion.Id };
                reject.Click += LimboRejectSuggestion_Click;
                actions.Children.Add(accept);
                actions.Children.Add(reject);
                row.Children.Add(actions);
            }

            panel.Children.Add(row);
        }

        return panel;
    }

    private static StackPanel Section(string title, IEnumerable<string> lines)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var line in lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.9,
            });
        }

        return panel;
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e) => CloseDrawer();

    private void CloseDrawer()
    {
        DrawerHost.Visibility = Visibility.Collapsed;
        DrawerBody.Children.Clear();
        _drawerProjectId = null;
        _drawerTaskId = null;
    }

    private async Task RefreshHostStatusAsync()
    {
        try
        {
            if (App.HostConnection is not null)
            {
                await App.HostConnection.EnsureConnectedAsync();
            }
        }
        catch (Exception)
        {
            // keep last status
        }

        _ = ProbeHermesStatusAsync();
        DispatcherQueue.TryEnqueue(RefreshHostStatus);
    }

    private async Task ProbeHermesStatusAsync()
    {
        try
        {
            if (!HermesUrlValidation.TryValidate(App.Settings.HermesBaseUrl, out var url, out _))
            {
                _hermesStatusLabel = "Hermes: not configured";
                _hermesStatusTooltip = null;
                DispatcherQueue.TryEnqueue(RefreshHostStatus);
                return;
            }

            var key = App.SettingsStore.ReadHermesApiKey(App.Settings);
            using var client = new HermesHttpClient(new Uri(url!), key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var health = await client.HealthAsync(cts.Token);
            _hermesStatusLabel = health.Ok ? "Hermes: ok" : "Hermes: down";
            _hermesStatusTooltip = url;
        }
        catch (Exception)
        {
            _hermesStatusLabel = "Hermes: down";
            _hermesStatusTooltip = App.Settings.HermesBaseUrl;
        }

        DispatcherQueue.TryEnqueue(RefreshHostStatus);
    }

    private void RefreshHostStatus()
    {
        var host = App.HostConnection?.LastStatus;
        string hostLabel;
        if (host is null || host.State == CoreHostConnectionState.Unknown)
        {
            hostLabel = "Core: …";
        }
        else if (host.State == CoreHostConnectionState.Connected)
        {
            hostLabel = host.Version is null ? "Core: ok" : $"Core: ok v{host.Version}";
        }
        else
        {
            hostLabel = "Core: degraded";
        }

        AgentStatusText.Text = $"{_hermesStatusLabel} · {hostLabel}";
        ToolTipService.SetToolTip(AgentStatusText, _hermesStatusTooltip);
        SyncStatusSeparator();
    }
}
