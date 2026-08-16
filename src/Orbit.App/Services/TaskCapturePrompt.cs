using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Workbench;

namespace Orbit_App.Services;

/// <summary>
/// Capture preview: original wording preserved + editable structured fields before save.
/// Shows project match reason when auto-matched; operator can correct the project.
/// </summary>
public static class TaskCapturePrompt
{
    public sealed record Result(
        string OriginalText,
        string Title,
        string? Brief,
        string? NextAction,
        string? ProjectId,
        string? ProjectMatchReason,
        string? DueAt,
        string? WaitingOn,
        string? People,
        string? Location,
        string Source = CapturePreviewProposer.SourceCapture);

    /// <summary>Text used for Phase 2 note-or-update matching (original preferred).</summary>
    public static string CaptureTextForUpdateMatch(Result result)
    {
        var original = (result.OriginalText ?? string.Empty).Trim();
        if (original.Length > 0)
        {
            return original;
        }

        var title = (result.Title ?? string.Empty).Trim();
        var brief = (result.Brief ?? string.Empty).Trim();
        if (title.Length > 0 && brief.Length > 0
            && !string.Equals(title, brief, StringComparison.OrdinalIgnoreCase))
        {
            return title + " " + brief;
        }

        return title.Length > 0 ? title : brief;
    }

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
        CapturePreviewDto? preview = null;
        using (var client = new CoreHostClient(App.Settings, App.SettingsStore))
        {
            if (showProjectPicker || !string.IsNullOrWhiteSpace(defaultProjectId))
            {
                try
                {
                    preview = await client.GetCapturePreviewAsync(initialTitle, defaultProjectId, ct)
                        .ConfigureAwait(true);
                }
                catch
                {
                    preview = null;
                }
            }

            if (showProjectPicker)
            {
                try
                {
                    projects = await ProjectPickUi.LoadActiveProjectsAsync(client, ct).ConfigureAwait(true);
                }
                catch
                {
                    projects = [];
                }
            }
        }

        // Local fallback when Host preview is unavailable.
        var local = CapturePreviewProposer.Propose(initialTitle);
        var originalSeed = preview?.OriginalText ?? local.OriginalText;
        var titleSeed = FirstNonEmpty(preview?.Title, local.Title, initialTitle);
        var briefSeed = preview?.Brief ?? local.Brief;
        var nextSeed = preview?.NextAction ?? local.NextAction;
        var dueSeed = preview?.DueAt ?? local.DueHint;
        var waitingSeed = preview?.WaitingOn ?? local.WaitingOnHint;
        var peopleSeed = preview?.People ?? local.PeopleHint;
        var locationSeed = preview?.Location ?? local.LocationHint;
        var sourceSeed = FirstNonEmpty(preview?.Source, CapturePreviewProposer.SourceCapture)!;

        string? autoProjectId = null;
        string? autoReason = null;
        string? autoReasonLabel = null;
        double? autoScore = null;
        if (preview?.MatchedProject is { ProjectId: { Length: > 0 } matchedId } mp)
        {
            autoProjectId = matchedId;
            autoReason = mp.Reason;
            autoReasonLabel = string.IsNullOrWhiteSpace(mp.ReasonLabel)
                ? CaptureMatchReasonFormatter.Format(mp.Reason)
                : mp.ReasonLabel;
            autoScore = mp.Score;
        }
        else if (!string.IsNullOrWhiteSpace(defaultProjectId))
        {
            autoProjectId = defaultProjectId;
            autoReason = "scoped";
            autoReasonLabel = CaptureMatchReasonFormatter.Format("scoped");
        }

        var originalBox = new TextBox
        {
            Header = "Original note",
            Text = originalSeed,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            MinHeight = 56,
            MaxHeight = 120,
            Opacity = 0.92,
        };

        var titleBox = new TextBox
        {
            Header = "Title",
            PlaceholderText = "Short title",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 360,
            MaxLength = CapturePreviewProposer.MaxTitleLength,
            Text = titleSeed ?? string.Empty,
        };

        var briefBox = new TextBox
        {
            Header = "Brief",
            PlaceholderText = "Preserves original wording when title is cleaned",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            MinHeight = 64,
            MaxHeight = 140,
            Text = briefSeed ?? string.Empty,
        };

        var nextBox = new TextBox
        {
            Header = "Next action",
            PlaceholderText = CapturePreviewProposer.DefaultNextAction,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 360,
            MaxLength = 200,
            Text = nextSeed ?? string.Empty,
        };

        var dueBox = new TextBox
        {
            Header = "Due (optional)",
            PlaceholderText = "yyyy-MM-dd or leave blank",
            AcceptsReturn = false,
            MinWidth = 360,
            Text = dueSeed ?? string.Empty,
        };

        var waitingBox = new TextBox
        {
            Header = "Waiting on (optional)",
            PlaceholderText = "Person or item this depends on",
            AcceptsReturn = false,
            MinWidth = 360,
            Text = waitingSeed ?? string.Empty,
        };

        var peopleBox = new TextBox
        {
            Header = "People / vendor (optional)",
            PlaceholderText = "Only when known from the note",
            AcceptsReturn = false,
            MinWidth = 360,
            Text = peopleSeed ?? string.Empty,
        };

        var locationBox = new TextBox
        {
            Header = "Location / unit (optional)",
            PlaceholderText = "Only when known from the note",
            AcceptsReturn = false,
            MinWidth = 360,
            Text = locationSeed ?? string.Empty,
        };

        var sourceBox = new TextBox
        {
            Header = "Source",
            Text = sourceSeed,
            IsReadOnly = true,
            MinWidth = 360,
            Opacity = 0.85,
        };

        var body = new StackPanel { Spacing = 10, MinWidth = 380 };
        body.Children.Add(originalBox);
        body.Children.Add(titleBox);
        body.Children.Add(briefBox);
        body.Children.Add(nextBox);

        // Optional signal fields: hide when empty on open (cheap proposals only).
        if (!string.IsNullOrWhiteSpace(peopleBox.Text) || !string.IsNullOrWhiteSpace(locationBox.Text))
        {
            if (!string.IsNullOrWhiteSpace(peopleBox.Text))
            {
                body.Children.Add(peopleBox);
            }

            if (!string.IsNullOrWhiteSpace(locationBox.Text))
            {
                body.Children.Add(locationBox);
            }
        }

        body.Children.Add(dueBox);
        body.Children.Add(waitingBox);
        body.Children.Add(sourceBox);

        ComboBox? combo = null;
        TextBlock? matchCaption = null;
        string? selectedReason = autoReason;
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
                MinWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = choices,
                SelectedIndex = choices.Count > 0 ? 0 : -1,
            };

            var preferId = autoProjectId ?? defaultProjectId;
            if (!string.IsNullOrWhiteSpace(preferId))
            {
                var match = choices.FindIndex(c =>
                    string.Equals(c.Id, preferId, StringComparison.Ordinal));
                if (match >= 0)
                {
                    combo.SelectedIndex = match;
                }
            }

            matchCaption = new TextBlock
            {
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.72,
                FontSize = 12,
                Margin = new Thickness(0, -4, 0, 0),
            };

            void RefreshCaption()
            {
                if (matchCaption is null || combo is null)
                {
                    return;
                }

                if (combo.SelectedItem is not ProjectPickUi.Choice choice || choice.Id.Length == 0)
                {
                    matchCaption.Text = allowLimbo ? "No project — parks in Limbo" : string.Empty;
                    matchCaption.Visibility = string.IsNullOrWhiteSpace(matchCaption.Text)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    selectedReason = null;
                    return;
                }

                var isAuto = !string.IsNullOrWhiteSpace(autoProjectId)
                    && string.Equals(choice.Id, autoProjectId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(autoReasonLabel);
                if (isAuto)
                {
                    selectedReason = autoReason;
                    matchCaption.Text = CaptureMatchReasonFormatter.FormatCaption(
                        choice.Name,
                        autoReason,
                        autoScore);
                }
                else
                {
                    selectedReason = "operator";
                    matchCaption.Text = CaptureMatchReasonFormatter.Format("operator");
                }

                matchCaption.Visibility = string.IsNullOrWhiteSpace(matchCaption.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            combo.SelectionChanged += (_, _) => RefreshCaption();
            RefreshCaption();

            // Insert project picker above source.
            var sourceIndex = body.Children.IndexOf(sourceBox);
            body.Children.Insert(sourceIndex, combo);
            body.Children.Insert(sourceIndex + 1, matchCaption);
        }
        else if (!string.IsNullOrWhiteSpace(defaultProjectId))
        {
            var scopedName = projects.FirstOrDefault(p =>
                    string.Equals(p.Id, defaultProjectId, StringComparison.Ordinal))
                ?.Name
                ?? preview?.MatchedProject?.Name
                ?? "Current project";
            body.Children.Insert(
                body.Children.IndexOf(sourceBox),
                new TextBlock
                {
                    Text = $"Project: {scopedName}",
                    Opacity = 0.85,
                });
            body.Children.Insert(
                body.Children.IndexOf(sourceBox),
                new TextBlock
                {
                    Text = CaptureMatchReasonFormatter.FormatCaption(
                        scopedName,
                        autoReason ?? "scoped",
                        autoScore),
                    Opacity = 0.72,
                    FontSize = 12,
                });
            selectedReason = autoReason ?? "scoped";
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = dialogTitle,
            Content = scroller,
            PrimaryButtonText = "Save",
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
            return null;
        }

        var original = originalBox.Text ?? string.Empty;
        var brief = NullIfBlank(briefBox.Text);
        var next = NullIfBlank(nextBox.Text) ?? CapturePreviewProposer.DefaultNextAction;
        var due = NullIfBlank(dueBox.Text);
        var waiting = NullIfBlank(waitingBox.Text);
        var people = NullIfBlank(peopleBox.Text);
        var location = NullIfBlank(locationBox.Text);
        var source = NullIfBlank(sourceBox.Text) ?? CapturePreviewProposer.SourceCapture;

        // Persist brief always keeps original wording when title was cleaned.
        brief = CapturePreviewProposer.BuildPersistBrief(
            original,
            title,
            brief,
            people,
            location,
            waiting);

        if (!showProjectPicker)
        {
            return new Result(
                original,
                title,
                brief,
                next,
                string.IsNullOrWhiteSpace(defaultProjectId) ? null : defaultProjectId,
                selectedReason,
                due,
                waiting,
                people,
                location,
                source);
        }

        if (combo?.SelectedItem is ProjectPickUi.Choice { Id: { Length: > 0 } id })
        {
            return new Result(
                original,
                title,
                brief,
                next,
                id,
                selectedReason,
                due,
                waiting,
                people,
                location,
                source);
        }

        if (allowLimbo)
        {
            return new Result(
                original,
                title,
                brief,
                next,
                null,
                null,
                due,
                waiting,
                people,
                location,
                source);
        }

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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }
}
