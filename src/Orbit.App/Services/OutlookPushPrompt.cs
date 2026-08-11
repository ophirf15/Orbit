using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit_App.Views;

namespace Orbit_App.Services;

/// <summary>Memo + optional project before Outlook push (shown in Orbit App, not Outlook).</summary>
public static class OutlookPushPrompt
{
    public sealed record Result(string Memo, string? ProjectId);

    public static async Task<Result?> ShowAsync(
        XamlRoot xamlRoot,
        string? mailSummary = null,
        int queuedRemaining = 0,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProjectPickUi.Choice> projects;
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            projects = await ProjectPickUi.LoadActiveProjectsAsync(client, ct).ConfigureAwait(true);
        }
        catch
        {
            projects = [];
        }

        var memoBox = new TextBox
        {
            Header = "Memo for Hermes",
            PlaceholderText = "What is this about, or what should Hermes do?",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
            MaxLength = 4000,
        };

        var choices = new List<ProjectPickUi.Choice>
        {
            new() { Id = string.Empty, Name = "No project (limbo)" },
        };
        choices.AddRange(projects);

        var combo = new ComboBox
        {
            Header = "Project",
            MinWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = choices,
            SelectedIndex = 0,
        };

        var intro = string.IsNullOrWhiteSpace(mailSummary)
            ? "Orbit already captured this Outlook mail. Add a memo, then Hermes will organize it."
            : $"Captured: {mailSummary.Trim()}. Add a memo, then Hermes will organize it.";
        if (queuedRemaining > 0)
        {
            intro += $" ({queuedRemaining} more waiting in queue.)";
        }

        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        body.Children.Add(new TextBlock
        {
            Text = intro,
            TextWrapping = TextWrapping.WrapWholeWords,
            Opacity = 0.85,
        });
        body.Children.Add(memoBox);
        body.Children.Add(combo);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = queuedRemaining > 0 ? "Send to Orbit (queued)" : "Send to Orbit",
            Content = body,
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var memo = (memoBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(memo))
        {
            var needMemo = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Memo required",
                Content = "Write a short memo so Hermes knows what to do with the email.",
                CloseButtonText = "OK",
            };
            await needMemo.ShowAsync();
            return await ShowAsync(xamlRoot, mailSummary, queuedRemaining, ct).ConfigureAwait(true);
        }

        var projectId = combo.SelectedItem is ProjectPickUi.Choice { Id: { Length: > 0 } id } ? id : null;
        return new Result(memo, projectId);
    }
}
