using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Orbit.Core.Updates;
using Orbit_App.Services;

namespace Orbit_App.Views;

public sealed partial class AboutPage : Page
{
    private UpdateCheckResult? _lastCheck;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version: {AppVersion.GetInformationalVersion()} (assembly {AppVersion.GetAssemblyVersion()})";
        TfmText.Text = $"Target framework: {AppVersion.TargetFrameworkDisplay}";
        SettingsPathText.Text = $"Settings: {App.SettingsStore.SettingsPath}";
        DataPathText.Text = $"Data root: {App.Settings.LocalDataRoot}";
        HostStatusText.Text = FormatHostStatus();
        UpdateStatusText.Text = UpdateUiService.FormatStatus(App.Settings);
        _ = RefreshHostAsync();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateActionText.Text = "Checking GitHub Releases…";
        try
        {
            var result = await UpdateUiService.CheckAndPersistAsync(App.Settings, App.SettingsStore);
            _lastCheck = result;
            ApplyCheckResult(result);

            if (result.Succeeded && result.UpdateAvailable)
            {
                var confirm = new ContentDialog
                {
                    Title = "Update available",
                    Content = $"Version {result.RemoteVersion} is available. Snapshot DB (if sync folder set) and download/install the update?",
                    PrimaryButtonText = "Install update",
                    CloseButtonText = "Not now",
                    XamlRoot = XamlRoot,
                };
                if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                {
                    await RunInstallFlowAsync(result);
                }
            }
        }
        catch (Exception ex)
        {
            UpdateActionText.Text = "Check failed: " + ex.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCheck is null || !_lastCheck.UpdateAvailable)
        {
            UpdateActionText.Text = "Run Check now first.";
            return;
        }

        await RunInstallFlowAsync(_lastCheck);
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        ExportDiagnosticsButton.IsEnabled = false;
        DiagnosticsActionText.Text = "Building support bundle…";
        try
        {
            var result = await OrbitSupportBundle.ExportAsync(App.Settings, App.SettingsStore);
            DiagnosticsActionText.Text = result.Message;
            if (result.Ok && !string.IsNullOrWhiteSpace(result.ZipPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{result.ZipPath}\"",
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // path still shown
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsActionText.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private async Task RunInstallFlowAsync(UpdateCheckResult result)
    {
        InstallUpdateButton.IsEnabled = false;
        try
        {
            UpdateActionText.Text = "Preparing update…";
            var snap = await UpdateUiService.SnapshotBeforeApplyAsync(App.Settings, App.SettingsStore);
            UpdateActionText.Text = snap.Message + Environment.NewLine + "Downloading installer…";
            var (_, openMsg) = await UpdateUiService.ApplyUpdateAsync(result);
            UpdateActionText.Text = snap.Message + Environment.NewLine + openMsg;
        }
        catch (Exception ex)
        {
            UpdateActionText.Text = "Install flow failed: " + ex.Message;
        }
        finally
        {
            InstallUpdateButton.IsEnabled = _lastCheck?.UpdateAvailable == true;
        }
    }

    private void ApplyCheckResult(UpdateCheckResult result)
    {
        UpdateStatusText.Text = UpdateUiService.FormatStatus(App.Settings, result);
        InstallUpdateButton.IsEnabled = result.Succeeded && result.UpdateAvailable;

        if (result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            var notes = result.ReleaseNotes.Length > 1200
                ? result.ReleaseNotes[..1200] + "…"
                : result.ReleaseNotes;
            ReleaseNotesText.Text = notes;
            ReleaseNotesText.Visibility = Visibility.Visible;
        }
        else
        {
            ReleaseNotesText.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(result.ReleaseHtmlUrl))
        {
            ReleaseNotesLink.NavigateUri = new Uri(result.ReleaseHtmlUrl);
            ReleaseNotesLink.Visibility = Visibility.Visible;
        }
        else
        {
            ReleaseNotesLink.Visibility = Visibility.Collapsed;
        }

        UpdateActionText.Text = result.Succeeded
            ? (result.UpdateAvailable ? "Update available." : "No update available.")
            : (result.Error ?? "Check failed.");
    }

    private async Task RefreshHostAsync()
    {
        try
        {
            if (App.HostConnection is not null)
            {
                await App.HostConnection.EnsureConnectedAsync();
            }
        }
        catch (Exception)
        {
            // ignore
        }

        DispatcherQueue.TryEnqueue(() => HostStatusText.Text = FormatHostStatus());
    }

    private static string FormatHostStatus()
    {
        var status = App.HostConnection?.LastStatus;
        if (status is null)
        {
            return $"Core Host: not checked · URL {App.Settings.CoreHostBaseUrl}";
        }

        return $"Core Host: {status.State} · {status.Message} · {status.BaseUrl}";
    }
}
