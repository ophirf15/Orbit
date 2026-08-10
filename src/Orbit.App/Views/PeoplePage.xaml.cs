using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Orbit_App.Services;
using Windows.System;

namespace Orbit_App.Views;

public sealed partial class PeoplePage : Page
{
    private IReadOnlyList<ContactListResult> _people = [];
    private string? _companyFilter;
    private string? _categoryFilter = "pending";
    private string? _dispositionFilter;
    private ContactDetailResult? _selected;
    private bool _selectMode;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);

    public PeoplePage()
    {
        InitializeComponent();
        FilterPending.IsChecked = true;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            _people = await client.GetContactsAsync(_categoryFilter, _dispositionFilter);
            RebuildBrowse();
            StatusText.Text = _people.Count == 0
                ? EmptyStatus()
                : $"{_people.Count} people · {_categoryFilter ?? _dispositionFilter ?? "all"}.";
        }
        catch (Exception)
        {
            StatusText.Text = "Could not load people from Core Host.";
        }
    }

    private string EmptyStatus()
    {
        if (string.Equals(_dispositionFilter, "flagged_resident", StringComparison.Ordinal))
        {
            return "Review queue is empty — no flagged residents.";
        }

        return "No people in this filter. Ingest email or switch category.";
    }

    private async void CategoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag)
        {
            return;
        }

        foreach (var btn in new[] { FilterCompany, FilterClients, FilterVendors, FilterPending, FilterReview })
        {
            btn.IsChecked = ReferenceEquals(btn, clicked);
        }

        if (string.Equals(tag, "review", StringComparison.OrdinalIgnoreCase))
        {
            _categoryFilter = null;
            _dispositionFilter = "flagged_resident";
        }
        else
        {
            _categoryFilter = tag;
            _dispositionFilter = null;
        }

        _companyFilter = null;
        _selected = null;
        _selectedIds.Clear();
        EditHost.Visibility = Visibility.Collapsed;
        DeleteButton.IsEnabled = false;
        RefreshBulkBar();
        await ReloadAsync();
    }

    private void SelectMode_Click(object sender, RoutedEventArgs e)
    {
        _selectMode = SelectModeToggle.IsChecked == true;
        if (!_selectMode)
        {
            _selectedIds.Clear();
        }

        RefreshBulkBar();
        RebuildBrowse();
    }

    private void BulkClear_Click(object sender, RoutedEventArgs e)
    {
        _selectedIds.Clear();
        RefreshBulkBar();
        RebuildBrowse();
    }

    private async void BulkApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIds.Count == 0)
        {
            StatusText.Text = "Select people first.";
            return;
        }

        var categoryTag = (BulkCategory.SelectedItem as ComboBoxItem)?.Tag as string;
        var org = BulkOrganization.Text?.Trim();
        var hasCategory = BulkCategory.SelectedItem is not null;
        var hasOrg = !string.IsNullOrWhiteSpace(org);
        if (!hasCategory && !hasOrg)
        {
            StatusText.Text = "Pick a category and/or organization to apply.";
            return;
        }

        StatusText.Text = $"Updating {_selectedIds.Count}…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = 0;
            foreach (var id in _selectedIds.ToList())
            {
                var patch = new Dictionary<string, object?>();
                if (hasCategory)
                {
                    patch["category"] = categoryTag ?? string.Empty;
                }

                if (hasOrg)
                {
                    patch["organizationName"] = org;
                }

                var updated = await client.UpdateContactAsync(
                    id,
                    patch,
                    provenance: "People UI bulk edit",
                    requestedBy: "user");
                if (updated is not null)
                {
                    ok++;
                }
            }

            _selectedIds.Clear();
            RefreshBulkBar();
            await ReloadAsync();
            StatusText.Text = ok == 0 ? "Bulk update failed." : $"Updated {ok} people.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Bulk update failed: {ex.Message}";
        }
    }

    private void RefreshBulkBar()
    {
        BulkBar.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        BulkCountText.Text = _selectedIds.Count == 1
            ? "1 selected"
            : $"{_selectedIds.Count} selected";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildBrowse();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        _companyFilter = null;
        SearchBox.Text = string.Empty;
        RebuildBrowse();
    }

    private void RebuildBrowse()
    {
        BrowseHost.Children.Clear();
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        ClearFilterButton.Visibility = string.IsNullOrWhiteSpace(_companyFilter) && query.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        IEnumerable<ContactListResult> filtered = _people;
        if (_companyFilter is not null)
        {
            filtered = string.IsNullOrEmpty(_companyFilter)
                ? filtered.Where(p => string.IsNullOrWhiteSpace(p.OrganizationName))
                : filtered.Where(p =>
                    string.Equals(p.OrganizationName, _companyFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Length > 0)
        {
            filtered = filtered.Where(p =>
                Contains(p.DisplayName, query)
                || Contains(p.OrganizationName, query)
                || Contains(p.Title, query)
                || Contains(p.PrimaryEmail, query)
                || Contains(p.PrimaryPhone, query));
        }

        var list = filtered.ToList();
        if (list.Count == 0)
        {
            BrowseHost.Children.Add(new TextBlock
            {
                Text = query.Length > 0 || _companyFilter is not null
                    ? "No matches — try another name or company."
                    : EmptyStatus(),
                Opacity = 0.7,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            return;
        }

        var showCompanies = string.IsNullOrWhiteSpace(_companyFilter)
            && query.Length == 0
            && _dispositionFilter is null;
        if (showCompanies)
        {
            BrowseHost.Children.Add(SectionLabel("Companies"));
            var byOrg = list
                .GroupBy(p => string.IsNullOrWhiteSpace(p.OrganizationName) ? "No company" : p.OrganizationName!)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in byOrg)
            {
                var orgName = group.Key;
                var count = group.Count();
                var sample = group.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .Select(p => p.DisplayName);
                var card = MakeCard(
                    title: orgName,
                    subtitle: count == 1 ? "1 person" : $"{count} people",
                    footer: string.Join(" · ", sample),
                    onClick: () =>
                    {
                        _companyFilter = string.Equals(orgName, "No company", StringComparison.Ordinal)
                            ? string.Empty
                            : orgName;
                        SearchBox.Text = string.Empty;
                        RebuildBrowse();
                        StatusText.Text = string.IsNullOrEmpty(_companyFilter)
                            ? $"{count} people without a company."
                            : $"{count} people at {orgName}.";
                    });
                BrowseHost.Children.Add(card);
            }

            BrowseHost.Children.Add(SectionLabel($"Everyone ({list.Count})"));
            foreach (var person in list.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                BrowseHost.Children.Add(PersonRow(person));
            }

            return;
        }

        var heading = _dispositionFilter is not null
            ? "Flagged residents"
            : _companyFilter is null
                ? "Matches"
                : string.IsNullOrEmpty(_companyFilter)
                    ? "No company"
                    : _companyFilter;
        BrowseHost.Children.Add(SectionLabel(heading));
        foreach (var person in list.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            BrowseHost.Children.Add(PersonRow(person));
        }
    }

    private UIElement PersonRow(ContactListResult person)
    {
        var card = MakeCard(
            title: person.DisplayName,
            subtitle: FormatSubtitle(person),
            footer: person.PrimaryEmail ?? person.PrimaryPhone ?? "Open card",
            onClick: () =>
            {
                if (_selectMode)
                {
                    ToggleSelected(person.Id);
                    RebuildBrowse();
                    return;
                }

                _ = LoadDetailAsync(person.Id);
            });

        if (!_selectMode)
        {
            return card;
        }

        var check = new CheckBox
        {
            IsChecked = _selectedIds.Contains(person.Id),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 32,
        };
        check.Checked += (_, _) =>
        {
            _selectedIds.Add(person.Id);
            RefreshBulkBar();
        };
        check.Unchecked += (_, _) =>
        {
            _selectedIds.Remove(person.Id);
            RefreshBulkBar();
        };

        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(check, 0);
        Grid.SetColumn(card, 1);
        row.Children.Add(check);
        row.Children.Add(card);
        return row;
    }

    private void ToggleSelected(string id)
    {
        if (!_selectedIds.Add(id))
        {
            _selectedIds.Remove(id);
        }

        RefreshBulkBar();
    }

    private static Button MakeCard(string title, string subtitle, string footer, Action onClick)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 2),
        };
        button.Content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.WrapWholeWords,
                },
                new TextBlock
                {
                    Text = subtitle,
                    Opacity = 0.75,
                    FontSize = 12,
                    TextWrapping = TextWrapping.WrapWholeWords,
                },
                new TextBlock
                {
                    Text = footer,
                    Opacity = 0.55,
                    FontSize = 11,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Opacity = 0.85,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string FormatSubtitle(ContactListResult p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Category))
        {
            parts.Add(p.Category);
        }
        else if (string.Equals(p.Disposition, "flagged_resident", StringComparison.Ordinal))
        {
            parts.Add("resident?");
        }

        if (!string.IsNullOrWhiteSpace(p.Title))
        {
            parts.Add(p.Title);
        }

        if (!string.IsNullOrWhiteSpace(p.OrganizationName))
        {
            parts.Add(p.OrganizationName);
        }

        return parts.Count == 0 ? "Contact" : string.Join(" · ", parts);
    }

    private async Task LoadDetailAsync(string personId)
    {
        StatusText.Text = "Loading contact…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var detail = await client.GetContactAsync(personId);
            if (detail is null)
            {
                StatusText.Text = "Contact detail failed.";
                return;
            }

            _selected = detail;
            DetailName.Text = detail.DisplayName;
            DetailOrgTitle.Text = string.Join(
                " · ",
                new[] { detail.Title, detail.OrganizationName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            DetailMeta.Text = string.Join(
                " · ",
                new[]
                {
                    string.IsNullOrWhiteSpace(detail.Category) ? "pending" : detail.Category,
                    detail.Disposition,
                });

            MethodsText.Text = detail.Methods.Count == 0
                ? "—"
                : string.Join(
                    "\n",
                    detail.Methods.Select(m => $"{TitleCase(m.MethodType)} · {m.Value}"));

            ProjectsText.Text = detail.Projects.Count == 0
                ? "—"
                : string.Join(", ", detail.Projects.Select(p => p.Name));

            EmailsText.Text = detail.RecentEmails.Count == 0
                ? "—"
                : string.Join(
                    "\n",
                    detail.RecentEmails.Select(m =>
                        $"{m.SentAt ?? "—"} · {m.Subject ?? "(no subject)"}"));

            ProvenanceText.Text = detail.Provenance.Count == 0
                ? "Enriched from email when available."
                : string.Join(
                    "\n",
                    detail.Provenance.Select(p => $"{p.Field} ← {p.SourceKind}"));

            var email = detail.Methods.FirstOrDefault(m =>
                string.Equals(m.MethodType, "email", StringComparison.OrdinalIgnoreCase))?.Value;
            var phone = detail.Methods.FirstOrDefault(m =>
                    string.Equals(m.MethodType, "mobile", StringComparison.OrdinalIgnoreCase))?.Value
                ?? detail.Methods.FirstOrDefault(m =>
                    string.Equals(m.MethodType, "phone", StringComparison.OrdinalIgnoreCase))?.Value;

            EmailButton.IsEnabled = !string.IsNullOrWhiteSpace(email);
            EmailButton.Tag = email;
            PhoneButton.IsEnabled = !string.IsNullOrWhiteSpace(phone);
            PhoneButton.Tag = phone;
            DeleteButton.IsEnabled = true;
            DeleteButton.Content = string.Equals(detail.Disposition, "flagged_resident", StringComparison.Ordinal)
                ? "Confirm not tracking"
                : "Remove";

            EditHost.Visibility = Visibility.Visible;
            EditDisplayName.Text = detail.DisplayName;
            EditTitle.Text = detail.Title ?? string.Empty;
            EditOrganization.Text = detail.OrganizationName ?? string.Empty;
            EditMobile.Text = phone ?? string.Empty;
            EditEmail.Text = email ?? string.Empty;
            SelectCategoryCombo(detail.Category);
            EditReportsTo.Text = string.IsNullOrWhiteSpace(detail.ReportsToDisplayName)
                ? string.Empty
                : $"Reports to {detail.ReportsToDisplayName}";
            StatusText.Text = detail.DisplayName;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Detail failed: {ex.Message}";
        }
    }

    private void SelectCategoryCombo(string? category)
    {
        foreach (var item in EditCategory.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag as string ?? string.Empty;
            if (string.Equals(tag, category ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                EditCategory.SelectedItem = item;
                return;
            }
        }

        EditCategory.SelectedIndex = 0;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var categoryTag = (EditCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        var patch = new Dictionary<string, object?>
        {
            ["displayName"] = EditDisplayName.Text?.Trim(),
            ["title"] = string.IsNullOrWhiteSpace(EditTitle.Text) ? null : EditTitle.Text.Trim(),
            ["organizationName"] = string.IsNullOrWhiteSpace(EditOrganization.Text)
                ? null
                : EditOrganization.Text.Trim(),
            ["mobile"] = string.IsNullOrWhiteSpace(EditMobile.Text) ? null : EditMobile.Text.Trim(),
            ["email"] = string.IsNullOrWhiteSpace(EditEmail.Text) ? null : EditEmail.Text.Trim(),
            ["category"] = categoryTag,
        };

        StatusText.Text = "Saving…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var updated = await client.UpdateContactAsync(
                _selected.Id,
                patch,
                provenance: "People UI edit",
                requestedBy: "user");
            if (updated is null)
            {
                StatusText.Text = "Save failed.";
                return;
            }

            await ReloadAsync();
            await LoadDetailAsync(updated.Id);
            StatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var flagged = string.Equals(_selected.Disposition, "flagged_resident", StringComparison.Ordinal);
        var dialog = new ContentDialog
        {
            Title = flagged ? "Confirm not tracking" : "Remove contact",
            Content = flagged
                ? $"Exclude {_selected.DisplayName} as a resident? They will leave normal People lists and will not be revived on re-ingest."
                : $"Archive {_selected.DisplayName}? Soft-delete only — email artifacts stay linked.",
            PrimaryButtonText = flagged ? "Exclude" : "Archive",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        StatusText.Text = "Removing…";
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.ArchiveContactAsync(
                _selected.Id,
                excludeAsResident: flagged,
                provenance: flagged ? "People UI confirm not tracking" : "People UI archive",
                requestedBy: "user");
            if (!ok)
            {
                StatusText.Text = "Remove failed.";
                return;
            }

            _selected = null;
            EditHost.Visibility = Visibility.Collapsed;
            DeleteButton.IsEnabled = false;
            DetailName.Text = "Select a person";
            DetailOrgTitle.Text = string.Empty;
            DetailMeta.Text = string.Empty;
            await ReloadAsync();
            StatusText.Text = flagged ? "Excluded from tracking." : "Archived.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Remove failed: {ex.Message}";
        }
    }

    private static string TitleCase(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private async void EmailButton_Click(object sender, RoutedEventArgs e)
    {
        if (EmailButton.Tag is not string address || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri($"mailto:{address}"));
    }

    private async void PhoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneButton.Tag is not string phone || string.IsNullOrWhiteSpace(phone))
        {
            return;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri($"tel:{digits}"));
    }
}
