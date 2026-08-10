using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Orbit_App.Services;
using Orbit_App.ViewModels;

namespace Orbit_App.Views;

public sealed partial class ConcernBriefPage : Page
{
    private string _taskId = string.Empty;
    private string? _anchorEmailId;
    private bool _loading;
    public ConcernBriefPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _taskId = e.Parameter as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_taskId))
        {
            StatusText.Text = "No concern selected.";
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        StatusText.Text = "Loading concern…";

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var concern = await client.GetConcernAsync(_taskId);
            if (concern is null)
            {
                var task = await client.GetTaskAsync(_taskId);
                concern = task is null
                    ? null
                    : new ConcernVm
                    {
                        TaskId = task.TaskId,
                        ProjectId = task.ProjectId,
                        Title = task.Title,
                        Status = task.Status,
                        NextAction = task.NextAction,
                        Body = task.Body,
                    };
            }

            if (concern is null)
            {
                StatusText.Text = "Concern not found.";
                return;
            }

            TitleBox.Text = concern.Title;
            ProjectText.Text = string.IsNullOrWhiteSpace(concern.ProjectName)
                ? concern.ProjectId ?? "Unknown project"
                : concern.ProjectName;
            NextActionBox.Text = concern.NextAction ?? string.Empty;
            BodyBox.Text = concern.Body ?? string.Empty;
            SelectStatus(concern.Status);

            _anchorEmailId = null;
            OpenEmailButton.Visibility = Visibility.Collapsed;
            var threads = await client.GetTaskEmailThreadsAsync(_taskId);
            var anchor = threads.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.AnchorEmailId));
            if (anchor?.AnchorEmailId is { Length: > 0 } emailId)
            {
                _anchorEmailId = emailId;
                OpenEmailButton.Visibility = Visibility.Visible;
            }

            StatusText.Text = "Edits save when you leave a field.";
        }
        catch (Exception)
        {
            StatusText.Text = "Could not load concern.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void SelectStatus(string status)
    {
        for (var i = 0; i < StatusCombo.Items.Count; i++)
        {
            if (StatusCombo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, status, StringComparison.Ordinal))
            {
                StatusCombo.SelectedIndex = i;
                return;
            }
        }

        StatusCombo.SelectedIndex = 0;
    }

    private string CurrentStatus =>
        StatusCombo.SelectedItem is ComboBoxItem item ? item.Tag as string ?? "active" : "active";

    private async void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || string.IsNullOrWhiteSpace(_taskId))
        {
            return;
        }

        await SaveAsync();
    }

    private async void StatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || string.IsNullOrWhiteSpace(_taskId))
        {
            return;
        }

        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.UpdateTaskAsync(
                _taskId,
                title: TitleBox.Text?.Trim(),
                status: CurrentStatus,
                nextAction: NextActionBox.Text?.Trim(),
                body: BodyBox.Text?.Trim());
            StatusText.Text = ok ? "Saved." : "Save failed.";
        }
        catch (Exception)
        {
            StatusText.Text = "Save failed.";
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
            return;
        }

        FindWorkbenchPage(this)?.ShowHome();
        FindPulsePage(this)?.ShowHome();
    }

    private static WorkbenchPage? FindWorkbenchPage(DependencyObject start)
    {
        var current = start;
        while (current != null)
        {
            if (current is WorkbenchPage workbench)
            {
                return workbench;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static PulsePage? FindPulsePage(DependencyObject start)
    {
        var current = start;
        while (current != null)
        {
            if (current is PulsePage pulse)
            {
                return pulse;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void OpenEmailButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_anchorEmailId))
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.OpenEmailInOutlookAsync(_anchorEmailId);
            StatusText.Text = ok ? "Opening email in Outlook…" : "Could not open email.";
        }
        catch (Exception)
        {
            StatusText.Text = "Could not open email.";
        }
    }
}
