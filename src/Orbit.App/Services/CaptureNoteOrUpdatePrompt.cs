using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Workbench;

namespace Orbit_App.Services;

/// <summary>
/// After capture dialog confirm: score open tasks and let the operator choose
/// update-existing vs create-new. Never applies without confirmation.
/// </summary>
public static class CaptureNoteOrUpdatePrompt
{
    public sealed record Choice(bool Cancelled, string? UpdateTaskId, string? UpdateTaskTitle);

    public static Choice CreateNew() => new(false, null, null);

    public static Choice Cancel() => new(true, null, null);

    public static Choice Update(string taskId, string title) => new(false, taskId, title);

    /// <summary>
    /// Loads open tasks for <paramref name="projectId"/>, ranks against capture text,
    /// and returns create-new / update / cancel after operator confirmation.
    /// </summary>
    public static async Task<Choice> ResolveAsync(
        XamlRoot xamlRoot,
        CoreHostClient client,
        string captureText,
        string projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(captureText) || string.IsNullOrWhiteSpace(projectId))
        {
            return CreateNew();
        }

        IReadOnlyList<CaptureTaskCandidate> candidates;
        try
        {
            var context = await client.GetProjectContextAsync(projectId, ct).ConfigureAwait(true);
            candidates = (context?.Tasks ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.TaskId) && !string.IsNullOrWhiteSpace(t.Title))
                .Select(t => new CaptureTaskCandidate(t.TaskId, t.Title, t.NextAction, t.Body))
                .ToList();
        }
        catch
        {
            return CreateNew();
        }

        if (candidates.Count == 0)
        {
            return CreateNew();
        }

        var ranked = CaptureTaskUpdateMatcher.Rank(captureText, candidates);
        var decision = CaptureTaskUpdateMatcher.Decide(ranked);

        return decision.Intent switch
        {
            CaptureTaskMatchIntent.CreateNew => CreateNew(),
            CaptureTaskMatchIntent.ProposeUpdate =>
                await ConfirmHighAsync(xamlRoot, decision.Primary!).ConfigureAwait(true),
            CaptureTaskMatchIntent.ShowAlternatives =>
                await ConfirmMediumAsync(xamlRoot, decision.Alternatives).ConfigureAwait(true),
            _ => throw new InvalidOperationException($"Unhandled match intent: {decision.Intent}"),
        };
    }

    /// <summary>
    /// Appends a dated capture stamp to the task body (additive; preserves existing notes).
    /// </summary>
    public static async Task<bool> AppendCaptureUpdateAsync(
        CoreHostClient client,
        string taskId,
        string captureText,
        string? currentBody,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(captureText))
        {
            return false;
        }

        var body = currentBody;
        if (body is null)
        {
            var task = await client.GetTaskAsync(taskId, ct).ConfigureAwait(true);
            body = task?.Body;
        }

        var merged = CaptureTaskUpdateAppender.AppendToBody(
            body,
            captureText,
            DateTimeOffset.UtcNow);
        return await client.UpdateTaskAsync(taskId, body: merged, ct: ct).ConfigureAwait(true);
    }

    private static async Task<Choice> ConfirmHighAsync(XamlRoot xamlRoot, CaptureTaskMatch match)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Update existing task?",
            Content = new TextBlock
            {
                Text =
                    $"This looks like an update to “{match.Title}”.\n\n" +
                    "Append a dated note to that task, or create a new one?",
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxWidth = 420,
            },
            PrimaryButtonText = $"Update existing: {Truncate(match.Title, 42)}",
            SecondaryButtonText = "Create new",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => Update(match.TaskId, match.Title),
            ContentDialogResult.Secondary => CreateNew(),
            _ => Cancel(),
        };
    }

    private static async Task<Choice> ConfirmMediumAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<CaptureTaskMatch> alternatives)
    {
        var options = new List<MatchOption>
        {
            new(null, "Create new task"),
        };
        foreach (var match in alternatives.Take(3))
        {
            options.Add(new(match, $"Update: {Truncate(match.Title, 56)}"));
        }

        var list = new ListBox
        {
            ItemsSource = options,
            SelectedIndex = alternatives.Count > 0 ? 1 : 0,
            MinWidth = 360,
            MaxHeight = 280,
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "Possible existing tasks — pick one to append a dated update, or create new.",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Update existing or create new?",
            Content = panel,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return Cancel();
        }

        if (list.SelectedItem is not MatchOption selected || selected.Match is null)
        {
            return CreateNew();
        }

        return Update(selected.Match.TaskId, selected.Match.Title);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private sealed record MatchOption(CaptureTaskMatch? Match, string Label)
    {
        public override string ToString() => Label;
    }
}
