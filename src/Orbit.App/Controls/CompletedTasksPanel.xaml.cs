using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Data;
using Orbit_App.Services;
using Orbit_App.ViewModels;

namespace Orbit_App.Controls;

public sealed partial class CompletedTasksPanel : UserControl
{
    public event EventHandler? CloseRequested;

    public event EventHandler<string>? TaskOpenRequested;

    public event EventHandler? ContentChanged;

    private string? _projectId;

    public CompletedTasksPanel()
    {
        InitializeComponent();
    }

    public async Task LoadAsync(string projectId, string? projectName = null)
    {
        _projectId = projectId;
        ProjectLabel.Text = string.IsNullOrWhiteSpace(projectName) ? "Project" : projectName!;
        TitleText.Text = "Completed tasks";
        ListHost.Children.Clear();
        FooterHint.Text = "Loading…";

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ctx = await client.GetProjectContextAsync(projectId);
            var done = ctx?.CompletedTasks ?? [];
            TitleText.Text = done.Count == 0 ? "Completed tasks" : $"Completed · {done.Count}";

            if (done.Count == 0)
            {
                ListHost.Children.Add(new TextBlock
                {
                    Text = "Nothing completed yet.",
                    Opacity = 0.6,
                });
                FooterHint.Text = "Mark tasks complete from the tree or board.";
                return;
            }

            foreach (var task in done)
            {
                ListHost.Children.Add(MakeRow(task));
            }

            FooterHint.Text = "Click a task to open · Reopen returns it to the board.";
        }
        catch (Exception)
        {
            FooterHint.Text = "Could not load completed tasks.";
        }
    }

    private UIElement MakeRow(CellLineVm task)
    {
        var open = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10, 12, 10),
            Tag = task.TaskId,
        };
        open.Content = new StackPanel
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
                    Text = string.IsNullOrWhiteSpace(task.NextAction) ? "Done" : task.NextAction,
                    Opacity = 0.7,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        open.Click += (_, _) => TaskOpenRequested?.Invoke(this, task.TaskId);

        var reopen = new Button
        {
            Content = "Reopen",
            VerticalAlignment = VerticalAlignment.Center,
            Tag = task.TaskId,
        };
        reopen.Click += async (_, _) =>
        {
            if (_projectId is null || reopen.Tag is not string id)
            {
                return;
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (await client.UpdateTaskAsync(id, status: TaskStatuses.Active))
            {
                FooterHint.Text = "Reopened.";
                ContentChanged?.Invoke(this, EventArgs.Empty);
                await LoadAsync(_projectId, ProjectLabel.Text);
            }
            else
            {
                FooterHint.Text = "Reopen failed.";
            }
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(reopen, 1);
        row.Children.Add(open);
        row.Children.Add(reopen);
        return row;
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
