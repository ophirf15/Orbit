using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Orbit_App.Services;
using Orbit_App.Shell;
using Windows.Graphics;

namespace Orbit_App;

public sealed partial class MainWindow : Window
{
    public ShellPage? Shell => RootFrame.Content as ShellPage;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetWindowIcon();
        ConfigureResizablePresenter();

        RootFrame.Navigate(typeof(ShellPage));
        ThemeService.ApplyToWindow(this, App.Settings.ThemePreference);
    }

    private void SetWindowIcon()
    {
        // Relative "Assets/..." resolves against the process CWD (often wrong when
        // launched from Start). Prefer BaseDirectory so taskbar/titlebar stay Orbit.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void ConfigureResizablePresenter()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            presenter = AppWindow.Presenter as OverlappedPresenter
                ?? throw new InvalidOperationException("OverlappedPresenter was not available.");
        }

        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        AppWindow.Resize(new SizeInt32(1280, 800));
    }
}
