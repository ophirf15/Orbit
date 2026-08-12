using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Settings;
using Orbit.Core.Updates;
using Orbit.Infrastructure.Diagnostics;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Hermes;
using Orbit.Infrastructure.Sync;
using Orbit_App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Orbit_App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _hermesKeyPresent;
    private bool _hermesModeUiReady;
    private UpdateCheckResult? _lastUpdateCheck;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeCombo.Items.Add("System");
        ThemeCombo.Items.Add("Light");
        ThemeCombo.Items.Add("Dark");
        LoadFromSettings();
        ApplyHermesModeUi(App.Settings.HermesConnectMode);
        _hermesModeUiReady = true;
        RefreshHermesInstallStatus();
        _ = RefreshHermesStatusAsync();
        _ = RefreshHostConnectionAsync();
        _ = RefreshCalendarSourcesAsync();
        _ = RefreshSyncAsync();
        RefreshOutlookAddInStatus();
        ShowSettingsPane("appearance");
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowSettingsPane(tag);
        }
    }

    private void ShowSettingsPane(string tag)
    {
        PaneAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PaneHermes.Visibility = tag == "hermes" ? Visibility.Visible : Visibility.Collapsed;
        PaneMail.Visibility = tag == "mail" ? Visibility.Visible : Visibility.Collapsed;
        PaneBackup.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
        PaneUpdates.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        PaneAdvanced.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetAdvancedOrHermesStatus(string message)
    {
        if (PaneAdvanced.Visibility == Visibility.Visible)
        {
            DiagnosticsActionText.Text = message;
            return;
        }

        HermesTestResultText.Visibility = Visibility.Visible;
        HermesTestResultText.Text = message;
    }

    private void LoadFromSettings()
    {
        var s = App.Settings;
        ThemeCombo.SelectedIndex = ThemeMapping.Normalize(s.ThemePreference) switch
        {
            ThemePreference.Light => 1,
            ThemePreference.Dark => 2,
            _ => 0,
        };
        HermesModeCombo.SelectedIndex = s.HermesConnectMode == HermesConnectMode.Manual ? 1 : 0;
        HermesUrlBox.Text = s.HermesBaseUrl;
        HermesApiKeyBox.Password = string.Empty;
        _hermesKeyPresent = !string.IsNullOrWhiteSpace(App.SettingsStore.ReadHermesApiKey(s));
        HermesApiKeyBox.Header = _hermesKeyPresent
            ? "Hermes API key (API_SERVER_KEY — sidecar present; leave blank to keep)"
            : "Hermes API key (API_SERVER_KEY — leave blank to keep existing sidecar)";
        CoreHostUrlBox.Text = s.CoreHostBaseUrl;
        CoreHostBindBox.Text = s.CoreHostBindAddress;
        CoreHostApiKeyBox.Password = string.Empty;
        var coreKeyPresent = !string.IsNullOrWhiteSpace(App.SettingsStore.ReadCoreHostApiKey(s));
        CoreHostApiKeyBox.Header = coreKeyPresent
            ? "Core Host API key (sidecar present — leave blank to keep; enter new value to replace)"
            : "Core Host API key (required for LAN bind; auto-created on Save if missing)";
        OneDrivePathText.Text = string.IsNullOrWhiteSpace(s.OneDriveSnapshotFolder)
            ? "Backup folder: (not set)"
            : "Backup folder: " + s.OneDriveSnapshotFolder;
        SyncDeviceText.Text = string.IsNullOrWhiteSpace(s.DeviceId)
            ? "Device id: (pending)"
            : "Device id: " + s.DeviceId;
        CalendarIcsBox.Text = s.CalendarIcsPath ?? string.Empty;
        GraphClientIdBox.Text = s.MicrosoftGraphClientId ?? string.Empty;
        GraphTenantBox.Text = string.IsNullOrWhiteSpace(s.MicrosoftGraphTenantId) ? "common" : s.MicrosoftGraphTenantId;
        GraphAccountText.Text = string.IsNullOrWhiteSpace(s.MicrosoftGraphSignedInUser)
            ? "Not signed in."
            : $"Signed in as {s.MicrosoftGraphSignedInUser}";
        GraphSignOutButton.IsEnabled = !string.IsNullOrWhiteSpace(s.MicrosoftGraphSignedInUser);
        DeveloperToggle.IsOn = s.DeveloperMode;
        SourceRepoRootBox.Text = s.SourceRepoRoot ?? string.Empty;
        UpdateDeveloperFieldsVisibility();
        HostToggle.IsOn = s.BackgroundHostEnabled;
        UpdatesStatusText.Text = UpdateUiService.FormatStatus(s);
        PathsText.Text =
            $"Settings file: {App.SettingsStore.SettingsPath}\n" +
            $"Local data: {s.LocalDataRoot}\n" +
            $"Generated files: {s.GeneratedFilesRoot}\n" +
            $"Device id: {s.DeviceId}";
        KeyRefText.Text =
            $"Hermes API key file (not shown): {s.HermesApiKeyReference}\n" +
            $"Core Host API key file (not shown): {s.CoreHostApiKeyReference}";
        StatusText.Visibility = Visibility.Collapsed;
        HostConnectionText.Text = FormatHostConnection();
    }

    private async Task RefreshHostConnectionAsync()
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

        DispatcherQueue.TryEnqueue(() => HostConnectionText.Text = FormatHostConnection());
    }

    private static string FormatHostConnection()
    {
        var status = App.HostConnection?.LastStatus;
        if (status is null)
        {
            return "Core Host connection: not checked yet.";
        }

        return $"Core Host connection: {status.State} — {status.Message}";
    }

    private async void TestHermesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunHermesConnectionProbeAsync(saveOnSuccess: false).ConfigureAwait(true);
    }

    private async void ClearStuckOperatorRuns_Click(object sender, RoutedEventArgs e)
    {
        HermesTestResultText.Visibility = Visibility.Visible;
        HermesTestResultText.Text = "Clearing stuck operator runs…";
        try
        {
            using var core = new CoreHostClient(App.Settings, App.SettingsStore);
            var n = await core.ClearStuckOperatorRunsAsync().ConfigureAwait(true);
            HermesTestResultText.Text = n == 0
                ? "No stuck operator runs — queue is clear."
                : $"Cleared {n} stuck run(s). Next mail push should not stall.";
        }
        catch (Exception ex)
        {
            HermesTestResultText.Text = $"Could not clear stuck runs: {ex.Message}";
        }
    }

    private async void ConnectHermesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunHermesConnectionProbeAsync(saveOnSuccess: true).ConfigureAwait(true);
    }

    private async Task RunHermesConnectionProbeAsync(bool saveOnSuccess)
    {
        HermesTestResultText.Visibility = Visibility.Visible;
        HermesTestResultText.Text = saveOnSuccess ? "Connecting to Hermes…" : "Testing Hermes…";
        TestHermesButton.IsEnabled = false;
        ConnectHermesButton.IsEnabled = false;

        try
        {
            if (!HermesUrlValidation.TryValidate(HermesUrlBox.Text, out var hermesUrl, out var error))
            {
                HermesTestResultText.Text = error;
                return;
            }

            var typedKey = HermesApiKeyBox.Password;
            var apiKey = string.IsNullOrWhiteSpace(typedKey)
                ? App.SettingsStore.ReadHermesApiKey(App.Settings)
                : typedKey.Trim();

            if (HermesUrlValidation.RequiresApiKey(hermesUrl) && string.IsNullOrWhiteSpace(apiKey))
            {
                HermesTestResultText.Text = "API key is required for non-loopback Hermes URLs.";
                return;
            }

            var warning = HermesUrlValidation.GetRemoteSecurityWarning(hermesUrl);
            using var client = new HermesHttpClient(new Uri(hermesUrl!), apiKey);
            var result = await client.TestConnectionAsync();

            try
            {
                new HermesHealthStatusStore().Write(
                    App.Settings.LocalDataRoot,
                    new HermesHealthLastKnown
                    {
                        Ok = result.Success,
                        StatusCode = result.Success ? 200 : 0,
                        Summary = result.Success
                            ? TruncateForDiagnostics(result.HealthSummary ?? "ok", 200)
                            : TruncateForDiagnostics(result.Error ?? "failed", 200),
                        CheckedAtUtc = DateTime.UtcNow.ToString("O"),
                    });
            }
            catch (Exception)
            {
                // Diagnostics cache is best-effort.
            }

            var lines = new List<string>();
            if (result.Success)
            {
                lines.Add(saveOnSuccess ? "Connected and saved." : "Connected.");
                if (!string.IsNullOrWhiteSpace(result.HealthSummary))
                {
                    lines.Add("Health: " + result.HealthSummary);
                }

                if (!string.IsNullOrWhiteSpace(result.CapabilitiesSummary))
                {
                    lines.Add("Capabilities: " + result.CapabilitiesSummary);
                }

                if (saveOnSuccess)
                {
                    PersistHermesConnection(hermesUrl!, typedKey);
                }

                SetHermesStatusUi(
                    HermesHandshakeState.Connected,
                    saveOnSuccess ? $"Connected · {hermesUrl}" : $"Reachable · {hermesUrl}");
            }
            else
            {
                lines.Add("Failed: " + (result.Error ?? "unknown error"));
                if (!string.IsNullOrWhiteSpace(result.HealthSummary))
                {
                    lines.Add("Health: " + result.HealthSummary);
                }

                if (!string.IsNullOrWhiteSpace(result.CapabilitiesSummary))
                {
                    lines.Add("Capabilities: " + result.CapabilitiesSummary);
                }

                SetHermesStatusUi(HermesHandshakeState.Failed, "Not connected");
            }

            if (!string.IsNullOrWhiteSpace(warning ?? result.SecurityWarning))
            {
                lines.Add(warning ?? result.SecurityWarning!);
            }

            HermesTestResultText.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            HermesTestResultText.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            TestHermesButton.IsEnabled = true;
            ConnectHermesButton.IsEnabled = true;
        }
    }

    private void PersistHermesConnection(string hermesUrl, string typedKey)
    {
        var s = App.Settings;
        s.HermesBaseUrl = hermesUrl;
        if (!string.IsNullOrWhiteSpace(typedKey))
        {
            App.SettingsStore.WriteHermesApiKey(s, typedKey.Trim());
        }

        App.SettingsStore.Save(s);
        App.Settings = s;
        LoadFromSettings();
    }

    private void CopyCoreEnvForHermesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = App.Settings;
            var bind = string.IsNullOrWhiteSpace(CoreHostBindBox.Text)
                ? s.CoreHostBindAddress
                : CoreHostBindBox.Text.Trim();
            var coreBase = string.IsNullOrWhiteSpace(CoreHostUrlBox.Text)
                ? s.CoreHostBaseUrl
                : CoreHostUrlBox.Text.Trim();
            var url = HermesPairing.BuildReachableCoreUrl(bind, coreBase);
            var key = string.IsNullOrWhiteSpace(CoreHostApiKeyBox.Password)
                ? App.SettingsStore.ReadCoreHostApiKey(s)
                : CoreHostApiKeyBox.Password.Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                SetAdvancedOrHermesStatus(
                    "No Core API key yet. Set a LAN bind and Save (auto-creates a key), or paste a Core Host API key first.");
                return;
            }

            var snippet = HermesPairing.BuildCoreEnvSnippet(url, key);
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(snippet);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            var loopback =
                Orbit.Core.Host.PathSafety.IsLoopbackAddress(bind) ||
                url.Contains("127.0.0.1", StringComparison.Ordinal) ||
                url.Contains("localhost", StringComparison.OrdinalIgnoreCase);

            SetAdvancedOrHermesStatus(loopback
                ? "Copied — but Core URL is loopback. Remote Hermes cannot reach it. Set Core Host bind to your LAN or Tailscale IP, Save, then copy again.\n\n" + snippet
                : "Copied ORBIT_CORE_URL / ORBIT_API_KEY for ~/.hermes/.env. Paste on the Hermes host and reload MCP.\n\n" + snippet);
        }
        catch (Exception ex)
        {
            SetAdvancedOrHermesStatus("Copy failed: " + ex.Message);
        }
    }

    private async void OpenHermesDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apiUrl = App.Settings.HermesBaseUrl;
            if (HermesManualPanel.Visibility == Visibility.Visible
                && !string.IsNullOrWhiteSpace(HermesUrlBox.Text))
            {
                apiUrl = HermesUrlBox.Text.Trim();
            }
            var dash = HermesPairing.DeriveDashboardUrl(apiUrl);
            if (dash is null)
            {
                SetAdvancedOrHermesStatus(
                    "Set a valid Hermes API URL first (e.g. http://127.0.0.1:8642). Dashboard is usually the same host on port 9119.");
                return;
            }

            await Windows.System.Launcher.LaunchUriAsync(new Uri(dash));
            SetAdvancedOrHermesStatus(
                $"Opened {dash}\n\n" +
                "Use the Hermes dashboard to connect your AI provider, Telegram, and other Hermes features. " +
                "Local Docker login is in hermes-local\\dashboard-login.txt under LocalAppData\\Orbit. " +
                "Orbit Agent chat still uses the API URL + API_SERVER_KEY (Connect & save).");
        }
        catch (Exception ex)
        {
            SetAdvancedOrHermesStatus("Could not open dashboard: " + ex.Message);
        }
    }

    private void OpenHermesDashboardInApp_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(HermesDashboardPage));
    }

    private void HermesModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_hermesModeUiReady)
        {
            return;
        }

        var mode = HermesModeCombo.SelectedIndex == 1
            ? HermesConnectMode.Manual
            : HermesConnectMode.ThisPc;
        ApplyHermesModeUi(mode);

        var s = App.Settings;
        if (s.HermesConnectMode != mode)
        {
            s.HermesConnectMode = mode;
            App.SettingsStore.Save(s);
            App.Settings = s;
        }

        _ = RefreshHermesStatusAsync();
    }

    private void ApplyHermesModeUi(HermesConnectMode mode)
    {
        var manual = mode == HermesConnectMode.Manual;
        HermesThisPcPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        HermesManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        HermesInstallText.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshHermesInstallStatus()
    {
        try
        {
            var install = HermesNativePairer.Detect();
            HermesInstallText.Text = install.LooksInstalled
                ? $"Install: {install.HermesHome}"
                : $"Install not found at {install.HermesHome}. Run the Hermes Windows installer first.";
        }
        catch (Exception ex)
        {
            HermesInstallText.Text = "Install check failed: " + ex.Message;
        }
    }

    private async Task RefreshHermesStatusAsync()
    {
        SetHermesStatusUi(HermesHandshakeState.Connecting, "Checking…");
        try
        {
            var s = App.Settings;
            var key = App.SettingsStore.ReadHermesApiKey(s);
            var result = await HermesHandshake.ProbeAsync(s.HermesBaseUrl, key).ConfigureAwait(true);
            SetHermesStatusUi(result.State, result.StatusLine);
            if (!string.IsNullOrWhiteSpace(result.Detail) && result.State != HermesHandshakeState.Connected)
            {
                HermesTestResultText.Visibility = Visibility.Visible;
                HermesTestResultText.Text = result.Detail;
            }
        }
        catch (Exception ex)
        {
            SetHermesStatusUi(HermesHandshakeState.Failed, "Not connected");
            HermesTestResultText.Visibility = Visibility.Visible;
            HermesTestResultText.Text = ex.Message;
        }
    }

    private void SetHermesStatusUi(HermesHandshakeState state, string statusLine)
    {
        HermesStatusText.Text = statusLine;
        HermesStatusDot.Fill = state switch
        {
            HermesHandshakeState.Connected =>
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            HermesHandshakeState.Connecting =>
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            HermesHandshakeState.NotInstalled =>
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            HermesHandshakeState.Failed =>
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            _ =>
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorNeutralBrush"],
        };
    }

    private async void ConnectHermesThisPcButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectHermesThisPcButton.IsEnabled = false;
        HermesTestResultText.Visibility = Visibility.Visible;
        HermesTestResultText.Text = string.Empty;
        SetHermesStatusUi(HermesHandshakeState.Connecting, "Connecting…");

        var progress = new Progress<string>(msg =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SetHermesStatusUi(HermesHandshakeState.Connecting, msg);
                HermesTestResultText.Text = msg;
            });
        });

        try
        {
            var s = App.Settings;
            var coreKey = EnsureCoreHostApiKey(s);
            var bind = string.IsNullOrWhiteSpace(CoreHostBindBox.Text)
                ? s.CoreHostBindAddress
                : CoreHostBindBox.Text.Trim();
            var orbitCoreUrl = HermesPairing.BuildReachableCoreUrl(bind, s.CoreHostBaseUrl);
            var preferredHermesKey = App.SettingsStore.ReadHermesApiKey(s);
            var docsRoot = ResolveDocsHermesRootForApp();

            var result = await HermesHandshake.ConnectThisPcAsync(
                    orbitCoreUrl,
                    coreKey,
                    preferredHermesApiKey: preferredHermesKey,
                    docsHermesRoot: docsRoot,
                    progress: progress)
                .ConfigureAwait(true);

            if (result.Connected
                && !string.IsNullOrWhiteSpace(result.ApiBaseUrl)
                && !string.IsNullOrWhiteSpace(result.ApiServerKey))
            {
                ApplyNativePairToSettings(result.ApiBaseUrl!, result.ApiServerKey!);
            }

            SetHermesStatusUi(result.State, result.StatusLine);
            HermesTestResultText.Text = result.Detail;
            RefreshHermesInstallStatus();
        }
        catch (Exception ex)
        {
            SetHermesStatusUi(HermesHandshakeState.Failed, "Handshake failed");
            HermesTestResultText.Text = ex.Message;
        }
        finally
        {
            ConnectHermesThisPcButton.IsEnabled = true;
        }
    }

    private string EnsureCoreHostApiKey(OrbitSettings s)
    {
        var coreKey = string.IsNullOrWhiteSpace(CoreHostApiKeyBox.Password)
            ? App.SettingsStore.ReadCoreHostApiKey(s)
            : CoreHostApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(coreKey))
        {
            coreKey = HermesPairing.GenerateApiServerKey();
            App.SettingsStore.WriteCoreHostApiKey(s, coreKey);
            App.SettingsStore.Save(s);
            App.Settings = s;
        }

        return coreKey;
    }

    private static string? ResolveDocsHermesRootForApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "hermes");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "SOUL.md")))
            {
                return candidate;
            }
        }

        return null;
    }

    private void ApplyNativePairToSettings(string apiBaseUrl, string apiServerKey)
    {
        var s = App.Settings;
        s.HermesBaseUrl = apiBaseUrl;
        s.HermesConnectMode = HermesConnectMode.ThisPc;
        App.SettingsStore.WriteHermesApiKey(s, apiServerKey);
        App.SettingsStore.Save(s);
        App.Settings = s;
        HermesUrlBox.Text = s.HermesBaseUrl;
        HermesApiKeyBox.Password = string.Empty;
        LoadFromSettings();
        ApplyHermesModeUi(s.HermesConnectMode);
    }

    private void PrepareLocalHermesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = App.Settings;
            var coreKey = string.IsNullOrWhiteSpace(CoreHostApiKeyBox.Password)
                ? App.SettingsStore.ReadCoreHostApiKey(s)
                : CoreHostApiKeyBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(coreKey))
            {
                coreKey = HermesPairing.GenerateApiServerKey();
                App.SettingsStore.WriteCoreHostApiKey(s, coreKey);
            }

            // Prefer host LAN/Tailscale bind so Docker Hermes can reach Core;
            // host.docker.internal often fails when Core is bound only to a LAN IP.
            var bind = string.IsNullOrWhiteSpace(CoreHostBindBox.Text)
                ? s.CoreHostBindAddress
                : CoreHostBindBox.Text.Trim();
            var orbitCoreUrl = HermesPairing.BuildReachableCoreUrl(bind, s.CoreHostBaseUrl);
            if (orbitCoreUrl.Contains("127.0.0.1", StringComparison.Ordinal)
                || orbitCoreUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                orbitCoreUrl = "http://host.docker.internal:8741";
            }

            var bundle = HermesLocalBundleWriter.Write(s.LocalDataRoot, orbitCoreUrl, coreKey);

            s.HermesBaseUrl = HermesPairing.LocalDefaultBaseUrl;
            App.SettingsStore.WriteHermesApiKey(s, bundle.ApiServerKey);
            App.SettingsStore.Save(s);
            App.Settings = s;
            HermesUrlBox.Text = s.HermesBaseUrl;
            HermesApiKeyBox.Password = string.Empty;
            LoadFromSettings();

            SetAdvancedOrHermesStatus(
                $"Local Hermes folder ready:\n{bundle.Directory}\n\n" +
                "1. docker compose up -d\n" +
                $"2. Open dashboard: {bundle.DashboardUrl}\n" +
                $"   Login: {bundle.DashboardUsername} / {bundle.DashboardPassword}\n" +
                "   (also saved in dashboard-login.txt)\n" +
                "3. In the dashboard: AI provider + optional Telegram\n" +
                "4. Back here: Connect & save (API key already stored)");
        }
        catch (Exception ex)
        {
            SetAdvancedOrHermesStatus("Prepare failed: " + ex.Message);
        }
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
                    var folder = Path.GetDirectoryName(result.ZipPath);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{result.ZipPath}\"",
                            UseShellExecute = true,
                        });
                    }
                }
                catch
                {
                    // path is still in the message
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsActionText.Text = "Export failed: " + ex.Message;
            Orbit.Infrastructure.Diagnostics.OrbitSupportLog.WriteErrorEvent(
                "support_export_ui",
                ex.Message);
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private static string? TruncateForDiagnostics(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max] + "…";

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        var mode = HermesModeCombo.SelectedIndex == 1
            ? HermesConnectMode.Manual
            : HermesConnectMode.ThisPc;

        string? hermesUrl;
        string? hermesWarning = null;
        if (mode == HermesConnectMode.ThisPc)
        {
            hermesUrl = string.IsNullOrWhiteSpace(s.HermesBaseUrl)
                ? HermesPairing.LocalDefaultBaseUrl
                : s.HermesBaseUrl.Trim();
            if (!HermesUrlValidation.TryValidate(hermesUrl, out hermesUrl, out var thisPcError))
            {
                StatusText.Text = thisPcError;
                StatusText.Visibility = Visibility.Visible;
                return;
            }
        }
        else
        {
            var typedKey = HermesApiKeyBox.Password;
            var effectiveKey = string.IsNullOrWhiteSpace(typedKey)
                ? App.SettingsStore.ReadHermesApiKey(s)
                : typedKey.Trim();

            if (!HermesUrlValidation.TryValidateForSave(
                    HermesUrlBox.Text,
                    effectiveKey,
                    out hermesUrl,
                    out var error,
                    out hermesWarning))
            {
                StatusText.Text = error;
                StatusText.Visibility = Visibility.Visible;
                return;
            }

            if (!string.IsNullOrWhiteSpace(typedKey))
            {
                App.SettingsStore.WriteHermesApiKey(s, typedKey.Trim());
            }
        }

        if (!HermesUrlValidation.TryValidate(CoreHostUrlBox.Text, out var hostUrl, out var hostError))
        {
            StatusText.Text = "Core Host URL: " + hostError;
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        var theme = ThemeCombo.SelectedIndex switch
        {
            1 => ThemePreference.Light,
            2 => ThemePreference.Dark,
            _ => ThemePreference.System,
        };

        s.ThemePreference = theme;
        s.HermesConnectMode = mode;
        s.HermesBaseUrl = hermesUrl!;
        s.CoreHostBaseUrl = hostUrl!;
        s.CoreHostBindAddress = string.IsNullOrWhiteSpace(CoreHostBindBox.Text)
            ? OrbitSettingsDefaults.CoreHostBindAddress
            : CoreHostBindBox.Text.Trim();
        // OneDriveSnapshotFolder is set via Choose folder / Clear — keep current App.Settings value.
        s.OneDriveSnapshotFolder = App.Settings.OneDriveSnapshotFolder;
        if (!string.IsNullOrWhiteSpace(s.OneDriveSnapshotFolder)
            && !SnapshotService.TryValidateSyncFolderWritable(s.OneDriveSnapshotFolder, out var syncFolderError))
        {
            StatusText.Text = syncFolderError ?? "Backup folder is not writable.";
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        s.CalendarIcsPath = string.IsNullOrWhiteSpace(CalendarIcsBox.Text) ? null : CalendarIcsBox.Text.Trim();
        s.MicrosoftGraphClientId = string.IsNullOrWhiteSpace(GraphClientIdBox.Text) ? null : GraphClientIdBox.Text.Trim();
        s.MicrosoftGraphTenantId = string.IsNullOrWhiteSpace(GraphTenantBox.Text) ? "common" : GraphTenantBox.Text.Trim();
        s.DeveloperMode = DeveloperToggle.IsOn;
        s.SourceRepoRoot = string.IsNullOrWhiteSpace(SourceRepoRootBox.Text) ? null : SourceRepoRootBox.Text.Trim();
        s.BackgroundHostEnabled = HostToggle.IsOn;

        try
        {
            var typedCoreKey = CoreHostApiKeyBox.Password;
            var existingCoreKey = App.SettingsStore.ReadCoreHostApiKey(s);
            if (!string.IsNullOrWhiteSpace(typedCoreKey))
            {
                App.SettingsStore.WriteCoreHostApiKey(s, typedCoreKey.Trim());
            }
            else if (string.IsNullOrWhiteSpace(existingCoreKey)
                     && !Orbit.Core.Host.PathSafety.IsLoopbackAddress(s.CoreHostBindAddress))
            {
                // LAN bind requires a key — mint one so Host can start without a separate setup step.
                var minted = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant();
                App.SettingsStore.WriteCoreHostApiKey(s, minted);
            }

            App.SettingsStore.Save(s);
            App.Settings = s;
            if (App.MainWindow is not null)
            {
                Services.ThemeService.ApplyToWindow(App.MainWindow, theme);
            }

            StatusText.Text = string.IsNullOrWhiteSpace(hermesWarning) ? "Saved." : "Saved. " + hermesWarning;
            StatusText.Visibility = Visibility.Visible;
            LoadFromSettings();
            ApplyHermesModeUi(s.HermesConnectMode);
            _ = RefreshHostConnectionAsync();
            _ = RefreshHermesStatusAsync();
            _ = RefreshCalendarSourcesAsync();
            _ = RefreshSyncAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private async void ChooseBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
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
        catch (Exception ex)
        {
            SyncStatusText.Text = "Folder picker failed: " + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            SyncStatusText.Text = "Folder picker cancelled.";
            return;
        }

        if (!SnapshotService.TryValidateSyncFolderWritable(path, out var error))
        {
            SyncStatusText.Text = error ?? "Folder is not writable.";
            return;
        }

        var previous = App.Settings.OneDriveSnapshotFolder;
        App.Settings.OneDriveSnapshotFolder = path.Trim();
        if (!string.Equals(previous, App.Settings.OneDriveSnapshotFolder, StringComparison.OrdinalIgnoreCase))
        {
            App.Settings.SkipEmptyBackupContinue = false;
        }

        App.SettingsStore.Save(App.Settings);
        OneDrivePathText.Text = "Backup folder: " + App.Settings.OneDriveSnapshotFolder;
        SyncStatusText.Text = "Backup folder saved.";
        await RefreshSyncAsync();
    }

    private async void ClearBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.OneDriveSnapshotFolder = null;
        App.Settings.SkipEmptyBackupContinue = false;
        App.SettingsStore.Save(App.Settings);
        OneDrivePathText.Text = "Backup folder: (not set)";
        SyncStatusText.Text = "Backup folder cleared.";
        await RefreshSyncAsync();
    }

    private async void SnapshotNowButton_Click(object sender, RoutedEventArgs e)
    {
        SyncStatusText.Text = "Creating backup…";
        SnapshotNowButton.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(App.Settings.OneDriveSnapshotFolder))
            {
                SyncStatusText.Text = "Choose a backup folder first.";
                return;
            }

            if (!SnapshotService.TryValidateSyncFolderWritable(App.Settings.OneDriveSnapshotFolder, out var error))
            {
                SyncStatusText.Text = error ?? "Backup folder is not writable.";
                return;
            }

            App.SettingsStore.Save(App.Settings);

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.CreateSyncSnapshotAsync();
            SyncStatusText.Text = result ?? "Backup finished.";
            await RefreshSyncAsync();
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "Backup failed: " + ex.Message;
        }
        finally
        {
            SnapshotNowButton.IsEnabled = true;
        }
    }

    private async void RefreshSnapshotsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshSyncAsync();

    private async void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SyncSnapshotListItem item
            || string.IsNullOrWhiteSpace(item.SnapshotId))
        {
            SyncStatusText.Text = "Select a snapshot to restore.";
            return;
        }

        SyncStatusText.Text = $"Restoring {item.SnapshotId}…";
        RestoreSnapshotButton.IsEnabled = false;
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var result = await client.RestoreSyncSnapshotAsync(item.SnapshotId);
            SyncStatusText.Text = result ?? "Restore finished.";
            App.Settings.SkipEmptyBackupContinue = false;
            App.SettingsStore.Save(App.Settings);
            await RefreshSyncAsync();
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "Restore failed: " + ex.Message;
        }
        finally
        {
            RestoreSnapshotButton.IsEnabled = true;
        }
    }

    private async Task RefreshSyncAsync()
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var status = await client.GetSyncStatusAsync();
            var snapshots = await client.ListSyncSnapshotsAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (status is null)
                {
                    SyncStatusText.Text = "Sync status: host unavailable.";
                    SyncConflictText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SyncStatusText.Text = CoreHostClient.FormatSyncStatusSummary(status);
                    if (!string.IsNullOrWhiteSpace(status.DeviceId))
                    {
                        SyncDeviceText.Text = "Device id: " + status.DeviceId;
                    }

                    if (!string.IsNullOrWhiteSpace(status.SyncFolder))
                    {
                        OneDrivePathText.Text = "Backup folder: " + status.SyncFolder;
                    }

                    if (!string.IsNullOrWhiteSpace(status.ConflictMessage)
                        || string.Equals(status.Kind, "Conflict", StringComparison.OrdinalIgnoreCase))
                    {
                        SyncConflictText.Text = status.ConflictMessage ?? status.Message ?? "Sync conflict.";
                        SyncConflictText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        SyncConflictText.Visibility = Visibility.Collapsed;
                    }
                }

                SnapshotsList.Items.Clear();
                if (snapshots is null || snapshots.Count == 0)
                {
                    SnapshotsList.Items.Add(new SyncSnapshotListItem
                    {
                        SnapshotId = string.Empty,
                        Display = "(no snapshots yet)",
                    });
                }
                else
                {
                    foreach (var snap in snapshots)
                    {
                        SnapshotsList.Items.Add(snap);
                    }
                }
            });
        }
        catch (Exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncStatusText.Text = "Sync status: host unavailable (capture still works offline).";
                SyncConflictText.Visibility = Visibility.Collapsed;
            });
        }
    }

    private async void CalendarSyncButton_Click(object sender, RoutedEventArgs e)
    {
        CalendarStatusText.Visibility = Visibility.Visible;
        CalendarStatusText.Text = "Syncing calendars…";
        CalendarSyncButton.IsEnabled = false;

        try
        {
            // Persist ICS path first so host options / subscribe stay aligned.
            var path = string.IsNullOrWhiteSpace(CalendarIcsBox.Text) ? null : CalendarIcsBox.Text.Trim();
            App.Settings.CalendarIcsPath = path;
            App.SettingsStore.Save(App.Settings);

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            if (!string.IsNullOrWhiteSpace(path))
            {
                await client.SubscribeCalendarIcsAsync(path);
            }

            var result = await client.SyncCalendarAsync();
            CalendarStatusText.Text = result ?? "Sync finished.";
            await RefreshCalendarSourcesAsync();
        }
        catch (Exception ex)
        {
            CalendarStatusText.Text = "Sync failed: " + ex.Message;
        }
        finally
        {
            CalendarSyncButton.IsEnabled = true;
        }
    }

    private void DeveloperToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateDeveloperFieldsVisibility();

    private void UpdateDeveloperFieldsVisibility()
    {
        SourceRepoRootBox.Visibility = DeveloperToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshCalendarSourcesAsync()
    {
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var sources = await client.ListCalendarSourcesAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                CalendarSourcesPanel.Children.Clear();
                if (sources is null)
                {
                    CalendarSourcesText.Visibility = Visibility.Visible;
                    CalendarSourcesText.Text = "Calendar sources: host unavailable.";
                    return;
                }

                CalendarSourcesText.Visibility = Visibility.Collapsed;
                if (sources.Count == 0)
                {
                    CalendarSourcesPanel.Children.Add(new TextBlock
                    {
                        Text = "No calendar sources yet — set an ICS path and Sync, or sign in with Microsoft.",
                        Opacity = 0.75,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    });
                    return;
                }

                foreach (var src in sources)
                {
                    var toggle = new ToggleSwitch
                    {
                        Header = src.DisplayLabel,
                        OnContent = "Included",
                        OffContent = "Excluded",
                        Tag = src.Id,
                    };
                    toggle.IsOn = src.Enabled;
                    toggle.Toggled += CalendarSourceToggle_Toggled;
                    CalendarSourcesPanel.Children.Add(toggle);
                }
            });
        }
        catch (Exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                CalendarSourcesPanel.Children.Clear();
                CalendarSourcesText.Visibility = Visibility.Visible;
                CalendarSourcesText.Text = "Calendar sources: host unavailable.";
            });
        }
    }

    private async void CalendarSourceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: string sourceId } toggle || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var ok = await client.SetCalendarSourceEnabledAsync(sourceId, toggle.IsOn);
            CalendarStatusText.Visibility = Visibility.Visible;
            CalendarStatusText.Text = ok
                ? (toggle.IsOn ? "Calendar included in Pulse." : "Calendar excluded from Pulse.")
                : "Could not update calendar include setting.";
            if (!ok)
            {
                toggle.Toggled -= CalendarSourceToggle_Toggled;
                toggle.IsOn = !toggle.IsOn;
                toggle.Toggled += CalendarSourceToggle_Toggled;
            }
        }
        catch (Exception)
        {
            CalendarStatusText.Visibility = Visibility.Visible;
            CalendarStatusText.Text = "Could not update calendar include setting.";
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdatesActionText.Text = "Checking GitHub Releases…";
        try
        {
            var result = await UpdateUiService.CheckAndPersistAsync(App.Settings, App.SettingsStore);
            _lastUpdateCheck = result;
            UpdatesStatusText.Text = UpdateUiService.FormatStatus(App.Settings, result);
            InstallUpdateButton.IsEnabled = result.Succeeded && result.UpdateAvailable;
            UpdatesActionText.Text = result.Succeeded
                ? (result.UpdateAvailable ? "Update available." : "No update available.")
                : (result.Error ?? "Check failed.");

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
                    await RunInstallUpdateAsync(result);
                }
            }
        }
        catch (Exception ex)
        {
            UpdatesActionText.Text = "Check failed: " + ex.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateCheck is null || !_lastUpdateCheck.UpdateAvailable)
        {
            UpdatesActionText.Text = "Run Check now first.";
            return;
        }

        await RunInstallUpdateAsync(_lastUpdateCheck);
    }

    private void RefreshOutlookAddInStatus()
    {
        var status = OutlookLauncherSetup.GetStatus();
        OutlookAddInStatusText.Text = status.Summary;
        InstallOutlookAddInButton.IsEnabled = status.PayloadAvailable || status.InstalledFilesPresent;
        UninstallOutlookAddInButton.IsEnabled = status.IsRegistered || status.InstalledFilesPresent;
    }

    private void InstallOutlookAddInButton_Click(object sender, RoutedEventArgs e)
    {
        InstallOutlookAddInButton.IsEnabled = false;
        UninstallOutlookAddInButton.IsEnabled = false;
        try
        {
            var result = OutlookLauncherSetup.InstallOrUpdate();
            OutlookAddInActionText.Text = result.Message;
            OutlookAddInActionText.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshOutlookAddInStatus();
        }
    }

    private void UninstallOutlookAddInButton_Click(object sender, RoutedEventArgs e)
    {
        InstallOutlookAddInButton.IsEnabled = false;
        UninstallOutlookAddInButton.IsEnabled = false;
        try
        {
            var result = OutlookLauncherSetup.Uninstall();
            OutlookAddInActionText.Text = result.Message;
            OutlookAddInActionText.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshOutlookAddInStatus();
        }
    }

    private MicrosoftGraphAuthService CreateGraphAuth()
    {
        var s = App.Settings;
        return new MicrosoftGraphAuthService(
            s.MicrosoftGraphClientId ?? string.Empty,
            s.MicrosoftGraphTenantId,
            s.LocalDataRoot);
    }

    private async void GraphSignInButton_Click(object sender, RoutedEventArgs e)
    {
        // Persist client/tenant before interactive login so a crash mid-flow doesn't lose them.
        var s = App.Settings;
        s.MicrosoftGraphClientId = string.IsNullOrWhiteSpace(GraphClientIdBox.Text) ? null : GraphClientIdBox.Text.Trim();
        s.MicrosoftGraphTenantId = string.IsNullOrWhiteSpace(GraphTenantBox.Text) ? "common" : GraphTenantBox.Text.Trim();
        try
        {
            App.SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            ShowGraphStatus("Could not save Graph settings: " + ex.Message);
            return;
        }

        GraphSignInButton.IsEnabled = false;
        ShowGraphStatus("Opening Microsoft sign-in…");
        try
        {
            var auth = CreateGraphAuth();
            var account = await auth.SignInInteractiveAsync();
            var display = await auth.TryGetSignedInDisplayAsync() ?? account.Username;
            s.MicrosoftGraphSignedInUser = display;
            App.SettingsStore.Save(s);
            App.Settings = s;
            GraphAccountText.Text = $"Signed in as {display}";
            GraphSignOutButton.IsEnabled = true;
            ShowGraphStatus("Signed in. Mail sync comes next — Microsoft holds the credentials.");
        }
        catch (Exception ex)
        {
            ShowGraphStatus("Sign-in failed: " + ex.Message);
        }
        finally
        {
            GraphSignInButton.IsEnabled = true;
        }
    }

    private async void GraphSignOutButton_Click(object sender, RoutedEventArgs e)
    {
        GraphSignOutButton.IsEnabled = false;
        try
        {
            var auth = CreateGraphAuth();
            await auth.SignOutAsync();
            var s = App.Settings;
            s.MicrosoftGraphSignedInUser = null;
            App.SettingsStore.Save(s);
            App.Settings = s;
            GraphAccountText.Text = "Not signed in.";
            ShowGraphStatus("Signed out.");
        }
        catch (Exception ex)
        {
            ShowGraphStatus("Sign-out failed: " + ex.Message);
            GraphSignOutButton.IsEnabled = !string.IsNullOrWhiteSpace(App.Settings.MicrosoftGraphSignedInUser);
        }
    }

    private void ShowGraphStatus(string message)
    {
        GraphStatusText.Text = message;
        GraphStatusText.Visibility = Visibility.Visible;
    }

    private async Task RunInstallUpdateAsync(UpdateCheckResult result)
    {
        InstallUpdateButton.IsEnabled = false;
        try
        {
            UpdatesActionText.Text = "Preparing update…";
            var snap = await UpdateUiService.SnapshotBeforeApplyAsync(App.Settings, App.SettingsStore);
            UpdatesActionText.Text = snap.Message + Environment.NewLine + "Downloading installer…";
            var (_, openMsg) = await UpdateUiService.ApplyUpdateAsync(result);
            UpdatesActionText.Text = snap.Message + Environment.NewLine + openMsg;
        }
        catch (Exception ex)
        {
            UpdatesActionText.Text = "Install flow failed: " + ex.Message;
        }
        finally
        {
            InstallUpdateButton.IsEnabled = _lastUpdateCheck?.UpdateAvailable == true;
        }
    }
}
