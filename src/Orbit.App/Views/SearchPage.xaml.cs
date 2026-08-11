using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Orbit_App.Services;
using Windows.System;

namespace Orbit_App.Views;

public sealed partial class SearchPage : Page
{
    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            QueryBox.Text = query;
            _ = RunSearchAsync();
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            QueryBox.Text = string.Empty;
            ResultsList.ItemsSource = null;
            StatusText.Text = string.Empty;
            ClearPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private async Task RunSearchAsync()
    {
        var query = QueryBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            StatusText.Text = "Enter a search query.";
            ResultsList.ItemsSource = null;
            ClearPreview();
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var hits = await client.GlobalSearchAsync(query);
            ResultsList.ItemsSource = hits;
            StatusText.Text = hits.Count == 0
                ? "No matches."
                : $"{hits.Count} result(s). Select a file or email to preview.";
            ClearPreview();
        }
        catch (Exception)
        {
            StatusText.Text = "Search failed — is Core Host running?";
        }
    }

    private async void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchHitItem hit)
        {
            return;
        }

        PreviewTitle.Text = hit.Title;

        if (string.Equals(hit.EntityType, "file", StringComparison.OrdinalIgnoreCase))
        {
            string? fallback = null;
            try
            {
                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                fallback = await client.PreviewFileAsync(hit.EntityId);
            }
            catch (Exception)
            {
                fallback = hit.Snippet;
            }

            await PreviewHost.ShowAsync(hit.Path, fallback ?? hit.Snippet);
            return;
        }

        if (string.Equals(hit.EntityType, "email", StringComparison.OrdinalIgnoreCase))
        {
            string? body = hit.Snippet;
            try
            {
                using var client = new CoreHostClient(App.Settings, App.SettingsStore);
                var email = await client.GetEmailAsync(hit.EntityId);
                if (email is not null)
                {
                    body = $"{email.Subject}{Environment.NewLine}{Environment.NewLine}{email.BodyPreview}";
                    PreviewTitle.Text = email.Subject ?? hit.Title;
                }
            }
            catch (Exception)
            {
                // keep snippet
            }

            await PreviewHost.ShowAsync(null, body);
            return;
        }

        await PreviewHost.ShowAsync(null, $"{hit.EntityType}: {hit.Title}{Environment.NewLine}{Environment.NewLine}{hit.Snippet}");
    }

    private void ClearPreview()
    {
        PreviewTitle.Text = "Preview";
        PreviewHost.Clear();
    }
}

public sealed class SearchHitItem
{
    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string Title { get; init; }

    public required string Snippet { get; init; }

    public double Score { get; init; }

    public string? ProjectId { get; init; }

    public string? Path { get; init; }

    public string MetaLine =>
        string.IsNullOrWhiteSpace(ProjectId)
            ? $"{EntityType} · score {Score:0.##}"
            : $"{EntityType} · project {ProjectId[..Math.Min(8, ProjectId.Length)]}… · score {Score:0.##}";
}
