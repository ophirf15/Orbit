using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orbit_App.Services;

/// <summary>
/// First-run / empty-local gate: continue from OneDrive snapshots via existing restore API.
/// </summary>
public static class BackupContinuePrompt
{
    public enum Choice
    {
        Continue,
        StartFresh,
        Cancelled,
    }

    public static async Task<Choice> ShowAsync(
        XamlRoot xamlRoot,
        SyncStatusInfo status,
        CancellationToken ct = default)
    {
        _ = ct;
        var last = CoreHostClient.FormatLastBackup(status.LastSnapshotAt);
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "This PC has an empty Orbit database, but your backup folder already has snapshots from another machine.",
        });
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = string.IsNullOrWhiteSpace(status.LatestCloudSnapshotId)
                ? last
                : $"{last}\nSnapshot: {status.LatestCloudSnapshotId}",
        });
        if (!string.IsNullOrWhiteSpace(status.SyncFolder))
        {
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Text = "Folder: " + status.SyncFolder,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Continue from OneDrive backup?",
            Content = body,
            PrimaryButtonText = "Continue from backup",
            SecondaryButtonText = "Start fresh",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => Choice.Continue,
            ContentDialogResult.Secondary => Choice.StartFresh,
            _ => Choice.Cancelled,
        };
    }
}
