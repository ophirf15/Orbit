using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orbit_App.Services;
using Orbit_App.ViewModels;
using Windows.System;

namespace Orbit_App.Controls;

/// <summary>Shared custom-fields UI (pencil rename, +, value save) for project/task surfaces.</summary>
public static class CustomFieldsEditor
{
    public static async Task BuildIntoAsync(
        Panel host,
        CoreHostClient client,
        string entityType,
        string entityId,
        Action<string>? statusHint = null,
        Func<Task>? onChanged = null,
        UIElement? focusAfterEdit = null)
    {
        host.Children.Clear();

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "Custom fields",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var addPlus = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 12 },
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(20),
        };
        try
        {
            addPlus.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        catch (Exception)
        {
            // theme optional
        }

        AutomationProperties.SetName(addPlus, "Add custom field");
        ToolTipService.SetToolTip(addPlus, "Add another field");
        Grid.SetColumn(title, 0);
        Grid.SetColumn(addPlus, 1);
        header.Children.Add(title);
        header.Children.Add(addPlus);
        host.Children.Add(header);
        host.Children.Add(new TextBlock
        {
            Text = "Values save on Enter. Pencil edits the field title.",
            Opacity = 0.65,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.WrapWholeWords,
        });

        var fields = await client.GetCustomFieldsAsync(entityType, entityId);
        if (fields.All(f => !string.Equals(f.Key, "cost", StringComparison.OrdinalIgnoreCase)))
        {
            await client.EnsureCustomFieldAsync(entityType, "cost", label: "Cost", fieldType: "number");
        }

        if (fields.Count == 0)
        {
            await client.EnsureCustomFieldAsync(entityType, "custom_field", label: "Custom field");
            await client.SetCustomFieldValueAsync(entityType, entityId, "custom_field", string.Empty);
        }

        fields = await client.GetCustomFieldsAsync(entityType, entityId);

        foreach (var field in fields)
        {
            var fieldKey = field.Key;
            var displayTitle = FriendlyTitle(field);

            var titleRow = new Grid { Margin = new Thickness(0, 10, 0, 2), ColumnSpacing = 4 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleLabel = new TextBlock
            {
                Text = displayTitle,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var titleEdit = SoftBox("Field title");
            titleEdit.Text = displayTitle;
            titleEdit.FontSize = 12;
            titleEdit.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            titleEdit.Visibility = Visibility.Collapsed;

            var pencil = new Button
            {
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 12, Opacity = 0.75 },
                Padding = new Thickness(6, 4, 6, 4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(pencil, "Edit field title");
            ToolTipService.SetToolTip(pencil, "Rename field");

            var suppressTitleCommit = false;
            async Task CommitTitleAsync()
            {
                var label = titleEdit.Text?.Trim() ?? string.Empty;
                if (label.Length == 0)
                {
                    titleEdit.Text = displayTitle;
                    titleEdit.Visibility = Visibility.Collapsed;
                    titleLabel.Visibility = Visibility.Visible;
                    return;
                }

                if (await client.UpdateCustomFieldLabelAsync(entityType, fieldKey, label))
                {
                    field.Label = label;
                    displayTitle = label;
                    titleLabel.Text = label;
                    statusHint?.Invoke("Field title saved.");
                    if (onChanged is not null)
                    {
                        await onChanged();
                    }
                }
                else
                {
                    statusHint?.Invoke("Could not save field title.");
                }

                titleEdit.Visibility = Visibility.Collapsed;
                titleLabel.Visibility = Visibility.Visible;
            }

            pencil.Click += (_, _) =>
            {
                suppressTitleCommit = false;
                titleEdit.Text = titleLabel.Text;
                titleLabel.Visibility = Visibility.Collapsed;
                titleEdit.Visibility = Visibility.Visible;
                titleEdit.Focus(FocusState.Programmatic);
            };
            titleEdit.LostFocus += async (_, _) =>
            {
                if (suppressTitleCommit)
                {
                    suppressTitleCommit = false;
                    return;
                }

                await CommitTitleAsync();
            };
            titleEdit.KeyDown += async (_, e) =>
            {
                if (e.Key == VirtualKey.Escape)
                {
                    suppressTitleCommit = true;
                    titleEdit.Text = titleLabel.Text;
                    titleEdit.Visibility = Visibility.Collapsed;
                    titleLabel.Visibility = Visibility.Visible;
                    e.Handled = true;
                    focusAfterEdit?.Focus(FocusState.Programmatic);
                    return;
                }

                if (e.Key != VirtualKey.Enter)
                {
                    return;
                }

                e.Handled = true;
                await CommitTitleAsync();
                focusAfterEdit?.Focus(FocusState.Programmatic);
            };

            var titleStack = new Grid();
            titleStack.Children.Add(titleLabel);
            titleStack.Children.Add(titleEdit);
            Grid.SetColumn(titleStack, 0);
            Grid.SetColumn(pencil, 1);
            titleRow.Children.Add(titleStack);
            titleRow.Children.Add(pencil);
            host.Children.Add(titleRow);

            var box = SoftBox("Value");
            box.Text = field.Value;
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != VirtualKey.Enter)
                {
                    return;
                }

                e.Handled = true;
                if (await client.SetCustomFieldValueAsync(entityType, entityId, fieldKey, box.Text ?? string.Empty))
                {
                    field.Value = box.Text ?? string.Empty;
                    statusHint?.Invoke($"{displayTitle} saved.");
                }
                else
                {
                    statusHint?.Invoke($"Could not save {displayTitle}.");
                }

                focusAfterEdit?.Focus(FocusState.Programmatic);
            };
            box.LostFocus += async (_, _) =>
            {
                if (string.Equals(box.Text ?? string.Empty, field.Value, StringComparison.Ordinal))
                {
                    return;
                }

                if (await client.SetCustomFieldValueAsync(entityType, entityId, fieldKey, box.Text ?? string.Empty))
                {
                    field.Value = box.Text ?? string.Empty;
                    statusHint?.Invoke($"{displayTitle} saved.");
                }
            };
            host.Children.Add(box);
        }

        addPlus.Click += async (_, _) =>
        {
            var key = "field_" + Guid.NewGuid().ToString("N")[..10];
            await client.EnsureCustomFieldAsync(entityType, key, label: "Custom field");
            await client.SetCustomFieldValueAsync(entityType, entityId, key, string.Empty);
            statusHint?.Invoke("Added a custom field.");
            await BuildIntoAsync(host, client, entityType, entityId, statusHint, onChanged, focusAfterEdit);
            if (onChanged is not null)
            {
                await onChanged();
            }
        };
    }

    public static string FriendlyTitle(CustomFieldRowVm field)
    {
        if (!string.IsNullOrWhiteSpace(field.Label)
            && !string.Equals(field.Label, field.Key, StringComparison.Ordinal))
        {
            return field.Label;
        }

        return string.IsNullOrWhiteSpace(field.Key)
            ? "Custom field"
            : field.Key.Replace('_', ' ');
    }

    private static TextBox SoftBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 10, 12, 10),
        AcceptsReturn = false,
        TextWrapping = TextWrapping.NoWrap,
    };
}
