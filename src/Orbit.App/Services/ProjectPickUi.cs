using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orbit_App.Services;

/// <summary>Shared project picker for disambiguation accepts and move-task flows.</summary>
public static class ProjectPickUi
{
    public sealed class Choice
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public override string ToString() => Name;
    }

    public static IReadOnlyList<Choice> ParseCandidates(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<Choice>();
            foreach (var el in candidates.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadString(el, "id")
                    ?? ReadString(el, "projectId")
                    ?? ReadString(el, "project_id");
                var name = ReadString(el, "name")
                    ?? ReadString(el, "title")
                    ?? id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                list.Add(new Choice { Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name! });
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<IReadOnlyList<Choice>> LoadActiveProjectsAsync(
        CoreHostClient client,
        CancellationToken ct = default)
    {
        var projects = await client.GetProjectsAsync(ct);
        return projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new Choice
            {
                Id = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<Choice> MergeChoices(
        IEnumerable<Choice> candidates,
        IEnumerable<Choice> activeProjects)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<Choice>();
        foreach (var c in candidates.Concat(activeProjects))
        {
            if (!seen.Add(c.Id))
            {
                continue;
            }

            merged.Add(c);
        }

        return merged;
    }

    /// <summary>
    /// Title + optional body snippet for ambiguous email claim cards (Agent / Pulse / Workbench).
    /// </summary>
    public static (string Title, string? Detail) FormatAmbiguousEmailDisplay(
        string? summary,
        string? payloadJson)
    {
        var subject = ReadPayloadString(payloadJson, "subject");
        var snippet = ReadPayloadString(payloadJson, "snippet");
        var title = !string.IsNullOrWhiteSpace(summary)
            ? summary.Trim()
            : "Ambiguous email claim — pick a project";

        if (!string.IsNullOrWhiteSpace(subject)
            && title.Equals("Ambiguous email claim — pick a project", StringComparison.Ordinal))
        {
            var subj = subject.Trim();
            if (subj.Length > 100)
            {
                subj = subj[..100].TrimEnd() + "…";
            }

            title = $"Ambiguous email — “{subj}”";
        }

        string? detail = null;
        if (!string.IsNullOrWhiteSpace(snippet))
        {
            detail = snippet.Trim();
            if (detail.Length > 200)
            {
                detail = detail[..200].TrimEnd() + "…";
            }
        }

        return (title, detail);
    }

    private static string? ReadPayloadString(string? payloadJson, string property)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty(property, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                var value = el.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// Shows a ComboBox dialog. Returns selected project id, or null if cancelled / empty.
    /// </summary>
    public static async Task<string?> ShowPickerAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<Choice> choices,
        string title = "Pick a project",
        string? message = null)
    {
        if (choices.Count == 0)
        {
            var empty = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = "No active projects available.",
                CloseButtonText = "Close",
            };
            await empty.ShowAsync();
            return null;
        }

        var combo = new ComboBox
        {
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = choices,
            SelectedIndex = 0,
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = message ?? "Choose which project this applies to.",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        body.Children.Add(combo);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = "Use project",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return combo.SelectedItem is Choice selected ? selected.Id : null;
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
