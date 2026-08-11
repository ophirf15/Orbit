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
    private IReadOnlyList<FolderItem> _folders = [];

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
            _folders = [];
            FoldersText.Text = "No project selected.";
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            _folders = await client.GetProjectFoldersAsync(SelectedProject.Id);
            FoldersText.Text = _folders.Count == 0
                ? "No folders attached. Set a home folder on Workbench or attach one here — indexing walks all subfolders."
                : string.Join(
                    " · ",
                    _folders.Select(f => f.IsHome ? $"home: {f.RootPath}" : f.RootPath));
        }
        catch (Exception)
        {
            _folders = [];
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
            var roots = _folders.Select(f => f.RootPath).ToList();
            foreach (var hit in hits)
            {
                hit.RelativePath = ToRelativePath(hit.Path, roots);
            }

            ResultsList.ItemsSource = hits;
            if (!string.IsNullOrWhiteSpace(statusPrefix))
            {
                StatusText.Text = statusPrefix.Trim();
            }
            else
            {
                StatusText.Text = hits.Count == 0
                    ? "No indexed files yet. Attach a folder or reindex."
                    : $"{hits.Count} file(s). Paths are relative to the attached/home folder.";
            }
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
                : FormatReindexStatus("Attached", attached.Reindex.IndexedCount > 0
                    ? attached.Reindex
                    : new CoreHostClient.ReindexFolderResult { IndexedCount = attached.IndexedCount }));
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
            _folders = folders;
            var merged = new CoreHostClient.ReindexFolderResult();
            var sample = new List<string>();
            var softDirs = new List<string>();
            var total = 0;
            var softSkipped = 0;
            var offline = 0;
            string? warning = null;

            foreach (var folder in folders)
            {
                var one = await client.ReindexFolderAsync(SelectedProject.Id, folder.Id);
                total += one.IndexedCount;
                softSkipped += one.SoftSkippedDirectoryCount;
                offline += one.OfflinePlaceholderCount;
                foreach (var path in one.SampleRelativePaths)
                {
                    if (sample.Count < 8 && !sample.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        sample.Add(path);
                    }
                }

                foreach (var dir in one.SoftSkippedDirectories)
                {
                    if (softDirs.Count < 4 && !softDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        softDirs.Add(dir);
                    }
                }

                if (!string.IsNullOrWhiteSpace(one.Warning))
                {
                    warning = one.Warning;
                }
            }

            merged = new CoreHostClient.ReindexFolderResult
            {
                IndexedCount = total,
                SoftSkippedDirectoryCount = softSkipped,
                OfflinePlaceholderCount = offline,
                SampleRelativePaths = sample,
                SoftSkippedDirectories = softDirs,
                Warning = warning,
            };

            SearchBox.Text = string.Empty;
            await ReloadFileListAsync(FormatReindexStatus(
                $"Reindexed across {folders.Count} folder(s).",
                merged));
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
        PreviewTitle.Text = string.IsNullOrWhiteSpace(hit.RelativePath) ? hit.DisplayName : hit.RelativePath;

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

    private static string FormatReindexStatus(string verb, CoreHostClient.ReindexFolderResult result)
    {
        var parts = new List<string>
        {
            $"{verb.TrimEnd('.')} · {result.IndexedCount} file(s).",
        };

        if (result.SampleRelativePaths.Count > 0)
        {
            parts.Add("Samples: " + string.Join(", ", result.SampleRelativePaths.Take(5)) + ".");
        }

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            parts.Add(result.Warning);
        }
        else if (result.SoftSkippedDirectoryCount > 0)
        {
            parts.Add(
                $"Skipped {result.SoftSkippedDirectoryCount} subdirectory tree(s) (ACL or cloud placeholder).");
        }

        return string.Join(' ', parts);
    }

    internal static string ToRelativePath(string fullPath, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        var best = fullPath;
        var bestLen = -1;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > bestLen)
            {
                bestLen = trimmed.Length;
                var relative = fullPath[trimmed.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                best = string.IsNullOrEmpty(relative) ? Path.GetFileName(fullPath) : relative;
            }
        }

        return best.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
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

    /// <summary>Path relative to the project folder root when known; otherwise absolute.</summary>
    public string RelativePath { get; set; } = string.Empty;
}
