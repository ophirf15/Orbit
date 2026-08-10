using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Shell;
using Orbit_App.Services;
using Orbit_App.Shell;
using Orbit_App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Orbit_App.Views;

public sealed partial class IgnitionPage : Page
{
    private readonly List<IgnitionProjectVm> _results = [];

    public IgnitionPage()
    {
        InitializeComponent();
    }

    private static IReadOnlyList<string> ParseNames(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .ToList();

    private void AppendResults(IEnumerable<IgnitionProjectVm> items)
    {
        foreach (var item in items)
        {
            _results.Add(item);
        }

        ResultsList.ItemsSource = null;
        ResultsList.ItemsSource = _results;
    }

    private async void FromListButton_Click(object sender, RoutedEventArgs e)
    {
        var names = ParseNames(NamesBox.Text ?? string.Empty);
        if (names.Count == 0)
        {
            StatusText.Text = "Enter at least one project name.";
            return;
        }

        FromListButton.IsEnabled = false;
        StatusText.Text = "Creating projects…";

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var results = await client.IgnitionFromListAsync(names);
            if (results is null || results.Count == 0)
            {
                StatusText.Text = "Create from list failed.";
                return;
            }

            AppendResults(results);
            StatusText.Text = $"Added {results.Count} project(s). Confirm when ready.";
        }
        catch (Exception)
        {
            StatusText.Text = "Create from list failed.";
        }
        finally
        {
            FromListButton.IsEnabled = true;
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
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
            if (!string.IsNullOrWhiteSpace(folder?.Path))
            {
                RootPathBox.Text = folder.Path;
            }
        }
        catch (Exception)
        {
            StatusText.Text = "Folder picker unavailable.";
        }
    }

    private async void LinkFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var rootPath = RootPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            StatusText.Text = "Enter or browse to a projects root folder.";
            return;
        }

        LinkFolderButton.IsEnabled = false;
        StatusText.Text = "Linking projects folder…";

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var results = await client.IgnitionFromProjectsRootAsync(rootPath);
            if (results is null || results.Count == 0)
            {
                StatusText.Text = "Link folder failed or found no subfolders.";
                return;
            }

            AppendResults(results);
            StatusText.Text = $"Linked {results.Count} project(s) from folder.";
        }
        catch (Exception)
        {
            StatusText.Text = "Link folder failed.";
        }
        finally
        {
            LinkFolderButton.IsEnabled = true;
        }
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmButton.IsEnabled = false;
        StatusText.Text = "Confirming orbit…";

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var confirm = await client.IgnitionConfirmAsync();
            if (confirm is null || !confirm.IgnitionCompleted)
            {
                StatusText.Text = "Confirm failed.";
                return;
            }

            StatusText.Text = "Orbit confirmed. Opening Pulse…";
            if (App.MainWindow is MainWindow window && window.Shell is ShellPage shell)
            {
                shell.NavigateTo(CommandCatalog.Pulse);
            }
        }
        catch (Exception)
        {
            StatusText.Text = "Confirm failed.";
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }
}
