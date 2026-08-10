using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Settings;
using Orbit_App.Services;
using Orbit_App.Shell;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Orbit_App.Views;

public sealed partial class EmailsPage : Page
{
    private string? _lastEmailId;
    private IReadOnlyList<ProjectItem> _projects = [];

    public EmailsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadProjectsAsync();
    }

    private async Task ReloadProjectsAsync()
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            _projects = await client.GetProjectsAsync();
            ProjectList.ItemsSource = _projects;
        }
        catch (Exception)
        {
            StatusText.Text = "Could not load projects from Core Host.";
        }
    }

    private IReadOnlyList<string> SelectedProjectIds()
    {
        return ProjectList.SelectedItems
            .OfType<ProjectItem>()
            .Select(p => p.Id)
            .Where(id => id.Length > 0)
            .ToList();
    }

    private string FormatProjectLinks(IReadOnlyList<string> projectIds)
    {
        if (projectIds.Count == 0)
        {
            return "(none — check a project above before pushing to link it)";
        }

        return string.Join(
            ", ",
            projectIds.Select(id =>
            {
                var name = _projects.FirstOrDefault(p => p.Id == id)?.Name;
                return string.IsNullOrWhiteSpace(name) ? id : $"{name}";
            }));
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        MsgDropHelper.AcceptMsgDrag(e);
        StatusText.Text = "Release to ingest…";
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        StatusText.Text = "Ingesting…";
        try
        {
            var payload = await MsgDropHelper.TryGetMsgAsync(e.DataView);
            if (payload is null)
            {
                StatusText.Text =
                    "Could not read that drop (Outlook OLE often fails). Save As .msg, then drop or browse.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(payload.SourcePath))
            {
                await IngestPathAsync(payload.SourcePath);
                return;
            }

            var email = await EmailIngestUi.IngestAsync(
                App.Settings,
                App.SettingsStore,
                payload,
                SelectedProjectIds());
            if (email is null)
            {
                StatusText.Text = "Ingest failed. Is Core Host running?";
                return;
            }

            await ApplyIngestResultAsync(email);
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Drop failed: {ex.Message}. If Outlook drag fails, Save As .msg then drop or browse.";
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".msg");
            if (App.MainWindow is not null)
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                StatusText.Text = "Browse cancelled.";
                return;
            }

            await IngestPathAsync(file.Path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Browse failed: {ex.Message}";
        }
    }

    private async Task IngestPathAsync(string path) => _ = await TryIngestPathAsync(path);

    private async Task<bool> TryIngestPathAsync(string path)
    {
        StatusText.Text = "Ingesting…";
        try
        {
            var inbox = Path.Combine(App.Settings.GeneratedFilesRoot, "inbox");
            Directory.CreateDirectory(inbox);
            var dest = Path.Combine(inbox, $"{Guid.NewGuid():N}.msg");
            File.Copy(path, dest, overwrite: true);

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.IngestEmailAsync(dest, SelectedProjectIds());
            if (result is null)
            {
                StatusText.Text = client.LastEmailIngestError ?? "Ingest failed. Is Core Host running?";
                SummaryText.Text = "No summary.";
                return false;
            }

            await ApplyIngestResultAsync(result);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ingest failed: {ex.Message}";
            return false;
        }
    }

    private async Task ApplyIngestResultAsync(CoreHostClient.EmailIngestResult result)
    {
        var linked = FormatProjectLinks(result.ProjectIds);
        _lastEmailId = result.Id;
        OpenInOutlookButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastEmailId);

        var projectBit = result.ProjectIds.Count == 0
            ? "Hermes will match a project when it can."
            : $"Pre-linked to {linked}.";

        StatusText.Text = result.WasExisting
            ? $"Updated existing email. {projectBit} Hermes organizing…"
            : $"Loaded into Orbit. {projectBit} Hermes organizing…";

        SummaryText.Text =
            $"Subject: {result.Subject ?? "(none)"}\n" +
            $"Linked projects: {linked}\n" +
            $"Preview: {result.BodyPreview ?? "—"}\n\n" +
            "Waiting for Hermes…";

        var briefing = await OutlookPushCoordinator.WaitForDutyBriefingAsync(
            App.Settings,
            App.SettingsStore,
            result.Id,
            result.Subject);
        if (!string.IsNullOrWhiteSpace(briefing))
        {
            SummaryText.Text =
                $"Subject: {result.Subject ?? "(none)"}\n" +
                $"Linked projects: {linked}\n\n" +
                "What Hermes did:\n" +
                briefing;
            StatusText.Text = "Done — Hermes organized this mail. Stay on Workbench; only real merges need Accept.";
        }
        else
        {
            SummaryText.Text +=
                "\n\nNo duty run yet. Check Settings → Core Host / Hermes.";
            StatusText.Text = "Email saved, but Hermes did not finish — check Core Host + Hermes.";
        }
    }

    private async void PushFromOutlook_Click(object sender, RoutedEventArgs e)
    {
        PushFromOutlookButton.IsEnabled = false;
        StatusText.Text = "Pushing Outlook selection…";
        try
        {
            var result = await OutlookPushCoordinator.PushSelectedAsync(
                App.Settings,
                App.SettingsStore,
                SelectedProjectIds());
            StatusText.Text = result.StatusLine;
            SummaryText.Text = result.Detail;
            _lastEmailId = result.LastEmailId;
            OpenInOutlookButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastEmailId);

            if (result.Ok
                && App.MainWindow?.Content is FrameworkElement root
                && FindShell(root) is { } shell)
            {
                shell.ShowDutyBanner(result.StatusLine, Truncate(result.Detail, 400), InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Outlook push failed: {ex.Message}";
            SummaryText.Text = "No summary.";
        }
        finally
        {
            PushFromOutlookButton.IsEnabled = true;
        }
    }

    private static string Truncate(string text, int max)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static ShellPage? FindShell(DependencyObject root)
    {
        if (root is ShellPage shell)
        {
            return shell;
        }

        if (root is Frame frame && frame.Content is DependencyObject inner)
        {
            return FindShell(inner);
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindShell(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async void OpenInOutlook_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastEmailId))
        {
            StatusText.Text = "Ingest an email first.";
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.OpenEmailInOutlookAsync(_lastEmailId);
            StatusText.Text = ok
                ? "Opened in Outlook (or the default .msg handler)."
                : "Could not open — is the .msg still on disk?";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open failed: {ex.Message}";
        }
    }
}
