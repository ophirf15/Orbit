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
        await MaybeContinueFromBackupAsync();
        await NavigateInitialAsync();
    }

    private async Task MaybeContinueFromBackupAsync()
    {
        if (App.Settings.SkipEmptyBackupContinue
            || string.IsNullOrWhiteSpace(App.Settings.OneDriveSnapshotFolder)
            || XamlRoot is null)
        {
            return;
        }

        try
        {
            if (App.HostConnection is not null)
            {
                await App.HostConnection.EnsureConnectedAsync();
            }

            using var client = new CoreHostClient(App.Settings, App.SettingsStore);
            var status = await client.GetSyncStatusAsync();
            if (status is null
                || !status.ContinueFromBackupAvailable
                || string.IsNullOrWhiteSpace(status.LatestCloudSnapshotId))
            {
                return;
            }

            // Divergent dirty local must never be overwritten here (ADR 0016).
            if (!string.IsNullOrWhiteSpace(status.ConflictMessage)
                || string.Equals(status.Kind, "Conflict", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var choice = await BackupContinuePrompt.ShowAsync(XamlRoot, status);
            switch (choice)
            {
                case BackupContinuePrompt.Choice.Continue:
                {
                    var result = await client.RestoreSyncSnapshotAsync(status.LatestCloudSnapshotId!);
                    App.Settings.SkipEmptyBackupContinue = false;
                    App.SettingsStore.Save(App.Settings);
                    if (string.IsNullOrWhiteSpace(result)
                        || result.StartsWith("Restore failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var fail = new ContentDialog
                        {
                            XamlRoot = XamlRoot,
                            Title = "Restore failed",
                            Content = string.IsNullOrWhiteSpace(result)
                                ? "Host returned no restore result."
                                : result,
                            CloseButtonText = "OK",
                        };
                        await fail.ShowAsync();
                    }
                    else
                    {
                        // Ensure workbench/pulse reload against the replaced DB.
                        try
                        {
                            if (App.HostConnection is not null)
                            {
                                await App.HostConnection.EnsureConnectedAsync();
                            }
                        }
                        catch (Exception)
                        {
                            // Navigation still proceeds; user can Refresh.
                        }
                    }

                    break;
                }
                case BackupContinuePrompt.Choice.StartFresh:
                    App.Settings.SkipEmptyBackupContinue = true;
                    App.SettingsStore.Save(App.Settings);
                    break;
                case BackupContinuePrompt.Choice.Cancelled:
                    break;
                default:
                    break;
            }
        }
        catch (Exception)
        {
            // Host/offline — leave empty workbench; Settings can restore later.
        }
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

    private void OnPushOutlook(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        App.OutlookPush.EnqueueHandoff("hotkey");
    }

    /// <summary>In-app entry points share the same snapshot queue as the Outlook button.</summary>
    public Task PushOutlookSelectionAsync(bool promptForMemo = true)
    {
        _ = promptForMemo;
        App.OutlookPush.EnqueueHandoff("in-app");
        return Task.CompletedTask;
    }

    public async Task ReloadWorkbenchAfterIngestAsync()
    {
        if (ContentFrame.CurrentSourcePageType != typeof(WorkbenchPage))
        {
            NavigateTo(CommandCatalog.Pulse);
        }

        if (ContentFrame.Content is WorkbenchPage workbench)
        {
            await workbench.ReloadAfterExternalIngestAsync().ConfigureAwait(true);
        }
    }

    public void ShowDutyBanner(string title, string message, InfoBarSeverity severity)
    {
        DutyInfoBar.Title = title;
        DutyInfoBar.Message = message;
        DutyInfoBar.Severity = severity;
        DutyInfoBar.IsOpen = true;
    }

    private void OnOpenSettings(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        NavigateTo(CommandCatalog.Settings);
        args.Handled = true;
    }

    private void CommandPaletteHost_CommandInvoked(object sender, ShellCommand command)
    {
        if (command.Id == CommandCatalog.ToggleTheme)
        {
            ToggleThemeFromCommand();
            return;
        }

        if (command.Id == CommandCatalog.QuickCapture)
        {
            FocusQuickCapture();
            return;
        }

        if (command.Id == CommandCatalog.PushOutlook)
        {
            _ = PushOutlookSelectionAsync();
            return;
        }

        if (command.Id == CommandCatalog.SearchRun)
        {
            NavigateTo(CommandCatalog.Search, command.Payload ?? string.Empty);
            return;
        }

        if (command.Id == CommandCatalog.SearchHit)
        {
            OpenSearchHit(command.Payload);
            return;
        }

        NavigateTo(command.Id);
    }

    private void OpenSearchHit(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            NavigateTo(CommandCatalog.Search);
            return;
        }

        var parts = payload.Split('|');
        var entityType = parts.Length > 0 ? parts[0] : string.Empty;
        var entityId = parts.Length > 1 ? parts[1] : string.Empty;
        var projectId = parts.Length > 2 ? parts[2] : string.Empty;

        if (string.Equals(entityType, "task", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "subtask", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "concern", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(entityId))
            {
                OpenConcernBrief(entityId);
                return;
            }
        }

        if (string.Equals(entityType, "project", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entityId))
        {
            NavigateTo(CommandCatalog.Pulse, entityId);
            return;
        }

        // Files / emails / unknown — open Search with a useful query.
        var query = !string.IsNullOrWhiteSpace(entityId) ? entityId : projectId;
        NavigateTo(CommandCatalog.Search, string.IsNullOrWhiteSpace(query) ? null : query);
    }

    private void CommandBarSearch_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void Shell_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (MsgDropHelper.LooksLikeOrbitTreeDrag(e.DataView))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

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
