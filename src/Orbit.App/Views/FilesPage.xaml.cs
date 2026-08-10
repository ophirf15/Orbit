using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orbit_App.Services;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Orbit_App.Views;

public sealed partial class FilesPage : Page
{
    private string? _selectedFileId;

    public FilesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadProjectsAsync();
    }

    private ProjectItem? SelectedProject => ProjectCombo.SelectedItem as ProjectItem;

    private async Task ReloadProjectsAsync()
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var projects = await client.GetProjectsAsync();
            ProjectCombo.ItemsSource = projects;
            if (projects.Count > 0)
            {
                ProjectCombo.SelectedIndex = 0;
            }
        }
        catch (Exception)
        {
            StatusText.Text = "Could not load projects from Core Host.";
        }
    }

    private async void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearPreview();
        await ReloadFoldersAsync();
        await ReloadFileListAsync();
    }

    private async Task ReloadFoldersAsync()
    {
        if (SelectedProject is null)
        {
            FoldersText.Text = "No project selected.";
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var folders = await client.GetProjectFoldersAsync(SelectedProject.Id);
            FoldersText.Text = folders.Count == 0
                ? "No folders attached."
                : string.Join(" · ", folders.Select(f => f.RootPath));
        }
        catch (Exception)
        {
            FoldersText.Text = "Could not load folders.";
        }
    }

    private async Task ReloadFileListAsync(string? statusPrefix = null)
    {
        if (SelectedProject is null)
        {
            ResultsList.ItemsSource = null;
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var query = SearchBox.Text?.Trim() ?? string.Empty;
            var hits = string.IsNullOrEmpty(query)
                ? await client.ListProjectFilesAsync(SelectedProject.Id)
                : await client.SearchFilesAsync(query, SelectedProject.Id);
            ResultsList.ItemsSource = hits;
            var prefix = string.IsNullOrWhiteSpace(statusPrefix) ? string.Empty : statusPrefix + " ";
            StatusText.Text = hits.Count == 0
                ? $"{prefix}No indexed files yet. Attach a folder or reindex."
                : $"{prefix}{hits.Count} file(s). Select one to preview.";
        }
        catch (Exception)
        {
            StatusText.Text = "Could not load indexed files.";
        }
    }

    private void ClearPreview()
    {
        _selectedFileId = null;
        OpenButton.IsEnabled = false;
        PreviewTitle.Text = "Preview";
        PreviewHost.Clear();
    }

    private async void AttachFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            StatusText.Text = "Select a project first.";
            return;
        }

        string? path = null;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (App.MainWindow is not null)
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            path = folder?.Path;
        }
        catch (Exception)
        {
            path = null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "Folder picker cancelled or unavailable. Try again.";
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var attached = await client.AttachProjectFolderAsync(SelectedProject.Id, path);
            await ReloadFoldersAsync();
            SearchBox.Text = string.Empty;
            await ReloadFileListAsync(attached is null
                ? "Attach failed."
                : $"Attached and indexed {attached.IndexedCount} files.");
        }
        catch (Exception)
        {
            StatusText.Text = "Attach failed.";
        }
    }

    private async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var folders = await client.GetProjectFoldersAsync(SelectedProject.Id);
            var total = 0;
            foreach (var folder in folders)
            {
                total += await client.ReindexFolderAsync(SelectedProject.Id, folder.Id);
            }

            SearchBox.Text = string.Empty;
            await ReloadFileListAsync($"Reindexed {total} files across {folders.Count} folders.");
        }
        catch (Exception)
        {
            StatusText.Text = "Reindex failed.";
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await ReloadFileListAsync();

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = string.Empty;
            e.Handled = true;
            await ReloadFileListAsync();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ReloadFileListAsync();
        }
    }

    private async void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FileHitItem hit)
        {
            return;
        }

        _selectedFileId = hit.Id;
        OpenButton.IsEnabled = true;
        PreviewTitle.Text = hit.DisplayName;

        string? fallback = null;
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            fallback = await client.PreviewFileAsync(hit.Id);
        }
        catch (Exception)
        {
            fallback = null;
        }

        await PreviewHost.ShowAsync(hit.Path, fallback);
    }

    private async void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFileId is null)
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.OpenFileExternallyAsync(_selectedFileId);
            StatusText.Text = ok ? "Opened in Windows shell." : "Open failed.";
        }
        catch (Exception)
        {
            StatusText.Text = "Open failed.";
        }
    }

}

public sealed class ProjectItem
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

public sealed class FolderItem
{
    public string Id { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public bool IsHome { get; init; }
}

public sealed class FileHitItem
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;
}
