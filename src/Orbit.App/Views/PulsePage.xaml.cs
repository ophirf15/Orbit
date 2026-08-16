using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Orbit.Core.Shell;
using Orbit_App.Services;
using Orbit_App.Shell;
using Orbit_App.ViewModels;

namespace Orbit_App.Views;

public sealed partial class PulsePage : Page
{
    private IReadOnlyList<PulseConcernVm> _allConcerns = [];
    private IReadOnlyList<OrbitProjectVm> _projects = [];
    private IReadOnlyList<PendingSuggestionVm> _lowConfidence = [];
    private string? _filterProjectId;

    public PulsePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        DetailFrame.Navigated += DetailFrame_Navigated;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string taskId && !string.IsNullOrWhiteSpace(taskId))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                PulseHome.Visibility = Visibility.Collapsed;
                DetailFrame.Visibility = Visibility.Visible;
                DetailFrame.Navigate(typeof(ConcernBriefPage), taskId);
            });
        }
    }

    private void DetailFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (DetailFrame.Content is ConcernBriefPage)
        {
            PulseHome.Visibility = Visibility.Collapsed;
            DetailFrame.Visibility = Visibility.Visible;
        }
        else
        {
            ShowHome();
        }
    }

    public void ShowHome()
    {
        DetailFrame.BackStack.Clear();
        DetailFrame.Content = null;
        DetailFrame.Visibility = Visibility.Collapsed;
        PulseHome.Visibility = Visibility.Visible;
    }

    private async Task LoadAsync(bool refresh = false)
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = refresh ? "Refreshing…" : "Loading…";

        try
        {
            if (App.HostConnection is not null)
            {
                await App.HostConnection.EnsureConnectedAsync();
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);

            var orbit = await client.GetOrbitAsync();
            _projects = orbit?.Projects?.Where(p => p.InOrbit).ToList()
                ?? orbit?.Projects?.ToList()
                ?? [];
            IgnitionButton.Visibility = orbit is { IgnitionCompleted: false } || _projects.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            BindProjects();

            var pulse = refresh
                ? await client.RefreshPulseAsync()
                : await client.GetPulseAsync();

            if (pulse is null)
            {
                DayBriefText.Text = "Could not load pulse from Core Host.";
                _allConcerns = [];
                _lowConfidence = [];
                LowConfidenceList.ItemsSource = _lowConfidence;
                LowConfidencePanel.Visibility = Visibility.Collapsed;
                BindBriefing(null);
                ApplyConcernFilter();
                GeneratedAtText.Text = string.Empty;
                var host = App.HostConnection?.LastStatus;
                StatusText.Text = host?.State == CoreHostConnectionState.Connected
                    ? "Core Host is running an outdated build without Pulse. Restart Orbit to refresh Host, or rebuild Orbit.Core.Host."
                    : "Core Host unavailable. Enable Background host in Settings → Advanced, then reopen Pulse.";
                return;
            }

            DayBriefText.Text = string.IsNullOrWhiteSpace(pulse.DayBrief)
                ? "Nothing open in the orbit yet. Ask Hermes (Telegram or Agent) to add a project or concern — this page is the feed Hermes fills for you."
                : pulse.DayBrief;
            if (!string.IsNullOrWhiteSpace(pulse.HermesHint))
            {
                HermesHintText.Text = pulse.HermesHint;
                HermesHintText.Visibility = Visibility.Visible;
            }
            else
            {
                HermesHintText.Visibility = Visibility.Collapsed;
            }

            GeneratedAtText.Text = string.IsNullOrWhiteSpace(pulse.GeneratedAt)
                ? "Hermes feeds this home — open a concern to act."
                : $"Updated {pulse.GeneratedAtDisplay}";
            _allConcerns = pulse.Concerns.ToList();
            ConcernsHeader.Text = _allConcerns.Count == 0
                ? "Needs you now"
                : $"Needs you now · {_allConcerns.Count}";

            var unmatched = pulse.UnmatchedMail?.ToList() ?? [];
            UnmatchedMailList.ItemsSource = unmatched;
            UnmatchedMailPanel.Visibility = unmatched.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UnmatchedMailHeader.Text = unmatched.Count == 0
                ? "Unmatched mail"
                : $"Unmatched mail · {unmatched.Count}";

            _lowConfidence = await client.GetSuggestionsAsync(queue: "low");
            LowConfidenceList.ItemsSource = _lowConfidence;
            LowConfidencePanel.Visibility = _lowConfidence.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LowConfidenceHeader.Text = _lowConfidence.Count == 0
                ? "Low-confidence suggestions"
                : $"Low-confidence · {_lowConfidence.Count}";
            DismissLowButton.IsEnabled = _lowConfidence.Count > 0;

            BindBriefing(pulse.Briefing);
            ApplyConcernFilter();
            StatusText.Text = string.Empty;
        }
        catch (Exception)
        {
            DayBriefText.Text = "Could not load pulse.";
            StatusText.Text = "Pulse load failed.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void BindBriefing(PulseBriefingVm? briefing)
    {
        BindBriefingSection(
            BriefingMeetingsHeader,
            BriefingMeetingsList,
            briefing?.UpcomingMeetings?.Select(m => m.Line).ToList());
        BindBriefingSection(
            BriefingActionsHeader,
            BriefingActionsList,
            briefing?.TopActions?.Select(a => a.Line).ToList());
        BindBriefingSection(
            BriefingWaitingHeader,
            BriefingWaitingList,
            briefing?.WaitingOn?.Select(w => w.Line).ToList());
        BindBriefingSection(
            BriefingAlertsHeader,
            BriefingAlertsList,
            briefing?.Alerts?.Select(a => a.Message).ToList());
        BindBriefingSection(
            BriefingChangesHeader,
            BriefingChangesList,
            briefing?.RecentChanges?.Select(c => c.Line).ToList());

        var any = (briefing?.UpcomingMeetings?.Count ?? 0)
            + (briefing?.TopActions?.Count ?? 0)
            + (briefing?.WaitingOn?.Count ?? 0)
            + (briefing?.Alerts?.Count ?? 0)
            + (briefing?.RecentChanges?.Count ?? 0)
            > 0;
        BriefingEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void BindBriefingSection(TextBlock header, ItemsControl list, IList<string>? lines)
    {
        var items = lines ?? [];
        list.ItemsSource = items;
        var visible = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        header.Visibility = visible;
        list.Visibility = visible;
    }

    private void BindProjects()
    {
        ProjectsRepeater.ItemsSource = _projects;
        ProjectsEmptyText.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyConcernFilter()
    {
        IEnumerable<PulseConcernVm> view = _allConcerns;
        if (!string.IsNullOrWhiteSpace(_filterProjectId))
        {
            view = view.Where(c => string.Equals(c.ProjectId, _filterProjectId, StringComparison.Ordinal));
            var name = _projects.FirstOrDefault(p => p.Id == _filterProjectId)?.Name ?? "project";
            ConcernsHeader.Text = $"Concerns · {name}";
            ClearFilterLink.Visibility = Visibility.Visible;
        }
        else
        {
            ConcernsHeader.Text = "Concerns";
            ClearFilterLink.Visibility = Visibility.Collapsed;
        }

        var list = view.ToList();
        ConcernsList.ItemsSource = list;
        StatusText.Text = list.Count == 0
            ? (_projects.Count == 0 ? "Add projects via Ignition to fill your orbit." : "No concerns for this filter yet.")
            : $"{list.Count} concern(s) · {_projects.Count} project(s) in orbit";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await LoadAsync(refresh: true);

    private void IgnitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow window && window.Shell is ShellPage shell)
        {
            shell.NavigateTo(CommandCatalog.Ignition);
        }
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        _filterProjectId = null;
        ApplyConcernFilter();
    }

    private void ProjectTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string projectId } || string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        _filterProjectId = string.Equals(_filterProjectId, projectId, StringComparison.Ordinal)
            ? null
            : projectId;
        ApplyConcernFilter();
    }

    private void ConcernsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PulseConcernVm concern || string.IsNullOrWhiteSpace(concern.TaskId))
        {
            return;
        }

        PulseHome.Visibility = Visibility.Collapsed;
        DetailFrame.Visibility = Visibility.Visible;
        DetailFrame.Navigate(typeof(ConcernBriefPage), concern.TaskId);
    }

    private async void UnmatchedMailList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PulseUnmatchedMailVm mail || string.IsNullOrWhiteSpace(mail.SuggestionId))
        {
            return;
        }

        if (XamlRoot is null)
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var projects = await ProjectPickUi.LoadActiveProjectsAsync(client);
            var pickerMessage = string.IsNullOrWhiteSpace(mail.Snippet)
                ? mail.Summary
                : $"{mail.Summary}\n\n{mail.Snippet}";
            var projectId = await ProjectPickUi.ShowPickerAsync(
                XamlRoot,
                projects,
                title: "Assign unmatched mail",
                message: pickerMessage);
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return;
            }

            var ok = await client.AcceptSuggestionAsync(mail.SuggestionId, projectId);
            StatusText.Text = ok ? "Mail assigned to project." : "Could not assign mail.";
            if (ok)
            {
                await LoadAsync();
            }
        }
        catch (Exception)
        {
            StatusText.Text = "Could not assign unmatched mail.";
        }
    }

    private async void DismissLowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lowConfidence.Count == 0)
        {
            return;
        }

        DismissLowButton.IsEnabled = false;
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ids = _lowConfidence.Select(s => s.Id).ToList();
            var result = await client.BatchDecideSuggestionsAsync(ids, "reject");
            StatusText.Text = result.Rejected > 0
                ? $"Dismissed {result.Rejected} low-confidence suggestion(s)."
                : "Could not dismiss low-confidence suggestions.";
            await LoadAsync();
        }
        catch (Exception)
        {
            StatusText.Text = "Could not dismiss low-confidence suggestions.";
            DismissLowButton.IsEnabled = _lowConfidence.Count > 0;
        }
    }
}
