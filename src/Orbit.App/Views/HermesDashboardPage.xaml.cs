using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orbit.Core.Settings;
using Windows.System;

namespace Orbit_App.Views;

public sealed partial class HermesDashboardPage : Page
{
    private string? _dashboardUrl;

    public HermesDashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dashboardUrl = HermesPairing.DeriveDashboardUrl(App.Settings.HermesBaseUrl)
                        ?? HermesPairing.LocalDefaultDashboardUrl;
        HintText.Text =
            $"Dashboard: {_dashboardUrl}. Local basic-auth may be in LocalAppData\\Orbit\\hermes-local\\dashboard-login.txt.";

        try
        {
            await DashboardView.EnsureCoreWebView2Async();
            DashboardView.Source = new Uri(_dashboardUrl);
        }
        catch (Exception ex)
        {
            HintText.Text = $"WebView2 failed ({ex.Message}). Use Open in browser.";
        }
    }

    private async void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = _dashboardUrl ?? HermesPairing.LocalDefaultDashboardUrl;
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DashboardView.Reload();
        }
        catch
        {
            // ignore
        }
    }
}
