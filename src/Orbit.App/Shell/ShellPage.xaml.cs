using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orbit.Core.Shell;
using Orbit_App.Services;
using Orbit_App.Views;
using Windows.System;

namespace Orbit_App.Shell;

public sealed partial class ShellPage : Page
{
    private readonly IGlobalCaptureHotkeyRegistrar _globalCaptureHotkey = new NullGlobalCaptureHotkeyRegistrar();

    public ShellPage()
    {
        InitializeComponent();

        // VK_OEM_COMMA (0xBC) — not projected as OemComma on this WinUI TFM.
        var settingsAccel = new KeyboardAccelerator
        {
            Key = (VirtualKey)0xBC,
            Modifiers = VirtualKeyModifiers.Control,
        };
        settingsAccel.Invoked += OnOpenSettings;
        KeyboardAccelerators.Add(settingsAccel);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _globalCaptureHotkey.Register(FocusQuickCapture);
        await NavigateInitialAsync();
    }

    private async Task NavigateInitialAsync()
    {
        var initial = CommandCatalog.Pulse;
        try
        {
            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var orbit = await client.GetOrbitAsync();
            if (orbit is { IgnitionCompleted: false })
            {
                initial = CommandCatalog.Ignition;
            }
        }
        catch (Exception)
        {
            // Default to Pulse when host is unavailable.
        }

        NavigateTo(initial);
        SelectNavItem(initial);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _globalCaptureHotkey.Unregister();

    public void NavigateTo(string commandId, object? parameter = null)
    {
        var pageType = commandId switch
        {
            CommandCatalog.Pulse => typeof(WorkbenchPage),
            CommandCatalog.Ignition => typeof(IgnitionPage),
            CommandCatalog.Workbench => typeof(WorkbenchPage),
            CommandCatalog.Agent => typeof(AgentChatPage),
            CommandCatalog.Files => typeof(FilesPage),
            CommandCatalog.Search => typeof(SearchPage),
            CommandCatalog.People => typeof(PeoplePage),
            CommandCatalog.Emails => typeof(EmailsPage),
            CommandCatalog.Settings => typeof(SettingsPage),
            CommandCatalog.About => typeof(AboutPage),
            _ => typeof(WorkbenchPage),
        };

        if (parameter is not null || ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, parameter);
        }
        else if ((commandId == CommandCatalog.Pulse || commandId == CommandCatalog.Workbench)
                 && ContentFrame.Content is WorkbenchPage home)
        {
            home.ShowHome();
        }

        OrbitRuntimeContextProvider.Instance.SetRoute(
            commandId is CommandCatalog.Workbench or CommandCatalog.QuickCapture
                ? CommandCatalog.Pulse
                : commandId);
        SelectNavItem(
            commandId is CommandCatalog.Workbench or CommandCatalog.QuickCapture
                ? CommandCatalog.Pulse
                : commandId);
        CommandPaletteHost.Close();
    }

    public void OpenConcernBrief(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        if (ContentFrame.Content is WorkbenchPage home)
        {
            SelectNavItem(CommandCatalog.Pulse);
            home.OpenConcernBrief(taskId.Trim());
            return;
        }

        NavigateTo(CommandCatalog.Pulse, taskId.Trim());
        SelectNavItem(CommandCatalog.Pulse);
    }

    public void OpenCommandPalette() => CommandPaletteHost.Open();

    public void FocusQuickCapture()
    {
        if (ContentFrame.CurrentSourcePageType != typeof(WorkbenchPage))
        {
            ContentFrame.Navigate(typeof(WorkbenchPage));
            SelectNavItem(CommandCatalog.Pulse);
        }

        if (ContentFrame.Content is WorkbenchPage workbench)
        {
            workbench.FocusLimboCapture();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ContentFrame.Content is WorkbenchPage page)
                {
                    page.FocusLimboCapture();
                }
            });
        }
    }

    public void ToggleThemeFromCommand()
    {
        var next = ThemeService.ToggleLightDark(App.Settings.ThemePreference);
        App.Settings.ThemePreference = next;
        App.SettingsStore.Save(App.Settings);
        if (App.MainWindow is not null)
        {
            ThemeService.ApplyToWindow(App.MainWindow, next);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void SelectNavItem(string commandId)
    {
        foreach (var obj in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (obj is NavigationViewItem item && item.Tag as string == commandId)
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    private void OnOpenCommandPalette(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        OpenCommandPalette();
        args.Handled = true;
    }

    private void OnQuickCapture(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FocusQuickCapture();
        args.Handled = true;
    }

    private async void OnPushOutlook(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PushOutlookSelectionAsync();
    }

    public async Task PushOutlookSelectionAsync()
    {
        try
        {
            var result = await OutlookPushCoordinator.PushSelectedAsync(
                App.Settings,
                App.SettingsStore,
                progress: (title, detail) => ShowDutyBanner(title, TruncateBanner(detail, 600), InfoBarSeverity.Informational));

            ShowDutyBanner(
                result.StatusLine,
                TruncateBanner(result.Detail, 700),
                result.Ok
                    ? (string.IsNullOrWhiteSpace(result.Briefing) ? InfoBarSeverity.Informational : InfoBarSeverity.Success)
                    : InfoBarSeverity.Error);

            if (result.Ok)
            {
                if (ContentFrame.CurrentSourcePageType != typeof(WorkbenchPage))
                {
                    NavigateTo(CommandCatalog.Pulse);
                }

                if (ContentFrame.Content is WorkbenchPage workbench)
                {
                    await workbench.ReloadAfterExternalIngestAsync();
                }
            }
        }
        catch (Exception ex)
        {
            ShowDutyBanner("Outlook push failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    public void ShowDutyBanner(string title, string message, InfoBarSeverity severity)
    {
        DutyInfoBar.Title = title;
        DutyInfoBar.Message = message;
        DutyInfoBar.Severity = severity;
        DutyInfoBar.IsOpen = true;
    }

    private static string TruncateBanner(string text, int max)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private void OnOpenSettings(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo(CommandCatalog.Settings);
        args.Handled = true;
    }

    private void CommandPaletteHost_CommandInvoked(object sender, string commandId)
    {
        if (commandId == CommandCatalog.ToggleTheme)
        {
            ToggleThemeFromCommand();
            return;
        }

        if (commandId == CommandCatalog.QuickCapture)
        {
            FocusQuickCapture();
            return;
        }

        if (commandId == CommandCatalog.PushOutlook)
        {
            _ = PushOutlookSelectionAsync();
            return;
        }

        NavigateTo(commandId);
    }

    private void CommandBarSearch_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void Shell_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        MsgDropHelper.AcceptMsgDrag(e);
    }

    private async void Shell_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        e.Handled = true;
        var payload = await MsgDropHelper.TryGetMsgAsync(e.DataView);

        if (ContentFrame.Content is WorkbenchPage workbench)
        {
            await workbench.HandleEmailPayloadAsync(payload);
            return;
        }

        NavigateTo(CommandCatalog.Workbench);
        if (ContentFrame.Content is WorkbenchPage page)
        {
            await page.HandleEmailPayloadAsync(payload);
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (ContentFrame.Content is WorkbenchPage deferred)
            {
                await deferred.HandleEmailPayloadAsync(payload);
            }
        });
    }
}
