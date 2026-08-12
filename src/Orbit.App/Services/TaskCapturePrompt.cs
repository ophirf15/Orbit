using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orbit_App.Services;

/// <summary>Title/memo + optional project before creating a workbench task.</summary>
public static class TaskCapturePrompt
{
    public sealed record Result(string Title, string? ProjectId);

    public static async Task<Result?> ShowAsync(
        XamlRoot xamlRoot,
        string? defaultProjectId = null,
        string dialogTitle = "New task",
        bool showProjectPicker = true,
        bool allowLimbo = true,
        string? initialTitle = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProjectPickUi.Choice> projects = [];
        if (showProjectPicker)
        {
            try
            {
                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                projects = await ProjectPickUi.LoadActiveProjectsAsync(client, ct).ConfigureAwait(true);
            }
            catch
            {
                projects = [];
            }
        }

        var titleBox = new TextBox
        {
            Header = "Title",
            PlaceholderText = "Short title or memo",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 320,
            MaxLength = 200,
            Text = initialTitle ?? string.Empty,
        };

        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        body.Children.Add(titleBox);

        ComboBox? combo = null;
        if (showProjectPicker)
        {
            var choices = new List<ProjectPickUi.Choice>();
            if (allowLimbo)
            {
                choices.Add(new ProjectPickUi.Choice { Id = string.Empty, Name = "Limbo (no project)" });
            }

            choices.AddRange(projects);

            combo = new ComboBox
            {
                Header = "Project",
                MinWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = choices,
                SelectedIndex = choices.Count > 0 ? 0 : -1,
            };

            if (!string.IsNullOrWhiteSpace(defaultProjectId))
            {
                var match = choices.FindIndex(c =>
                    string.Equals(c.Id, defaultProjectId, StringComparison.Ordinal));
                if (match >= 0)
                {
                    combo.SelectedIndex = match;
                }
            }

            body.Children.Add(combo);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = dialogTitle,
            Content = body,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(titleBox.Text),
        };

        titleBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(titleBox.Text);
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var title = (titleBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(title))
        {
            // Primary should stay disabled while empty; guard anyway.
            return null;
        }

        if (!showProjectPicker)
        {
            return new Result(title, string.IsNullOrWhiteSpace(defaultProjectId) ? null : defaultProjectId);
        }

        if (combo?.SelectedItem is ProjectPickUi.Choice { Id: { Length: > 0 } id })
        {
            return new Result(title, id);
        }

        if (allowLimbo)
        {
            return new Result(title, null);
        }

        // No Limbo and nothing selected (empty project list / Host down).
        var needProject = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Project required",
            Content = "No projects available. Create a project first, then try again.",
            CloseButtonText = "OK",
        };
        await needProject.ShowAsync();
        return null;
    }
}
