using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Orbit_App.Shell;

namespace Orbit_App.Services;

/// <summary>
/// Serial Outlook → Orbit push queue. Each handoff snapshots the current Outlook
/// selection immediately so rapid clicks keep the right messages even while a
/// prior memo/ingest is still running.
/// </summary>
public sealed class OutlookPushQueue
{
    private readonly ConcurrentQueue<QueuedBatch> _pending = new();
    private readonly object _handoffGate = new();
    private int _drainRunning;
    private int _queuedCount;
    private long _lastHandoffTicks;
    private int _captureInFlight;

    /// <summary>Collapse signal + peer-launch + activation into one capture per click.</summary>
    private static readonly long HandoffDebounceMs = 1200;

    private sealed record QueuedBatch(
        IReadOnlyList<OutlookSelectionPush.ExportedMail> Mails,
        string Source);

    public int PendingCount => Math.Max(0, Volatile.Read(ref _queuedCount));

    public bool IsBusy => Volatile.Read(ref _drainRunning) != 0 || PendingCount > 0;

    /// <summary>Handoff from Outlook ribbon / protocol / Ctrl+Shift+O.</summary>
    /// <returns>
    /// True when the handoff was accepted (including debounce no-ops), so callers
    /// may delete the signal file without retrying.
    /// </returns>
    public bool EnqueueHandoff(string source)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            Debug.WriteLine("OutlookPushQueue: MainWindow null, leaving signal for retry.");
            return false;
        }

        lock (_handoffGate)
        {
            var now = Environment.TickCount64;
            if (_lastHandoffTicks != 0 && now - _lastHandoffTicks < HandoffDebounceMs)
            {
                Debug.WriteLine($"OutlookPushQueue: debounced duplicate handoff ({source}).");
                return true;
            }

            _lastHandoffTicks = now;
        }

        var queued = window.DispatcherQueue.TryEnqueue(() =>
        {
            OrbitPushActivation.BringMainWindowToFront();
            _ = CaptureAndEnqueueAsync(source);
        });

        if (!queued)
        {
            Debug.WriteLine("OutlookPushQueue: DispatcherQueue.TryEnqueue failed.");
            lock (_handoffGate)
            {
                _lastHandoffTicks = 0;
            }
        }

        return queued;
    }

    private async Task CaptureAndEnqueueAsync(string source)
    {
        // Only serialize the Outlook SaveAs snapshot — release before memo/drain so
        // a later intentional click can still queue another message.
        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
        {
            Debug.WriteLine($"OutlookPushQueue: snapshot already in flight, skip ({source}).");
            return;
        }

        ShellPage? shell = null;
        try
        {
            shell = await WaitForShellAsync().ConfigureAwait(true);
            if (shell is null)
            {
                return;
            }

            shell.ShowDutyBanner(
                "Send to Orbit",
                IsBusy
                    ? "Capturing this mail into the queue…"
                    : "Got it from Outlook — capturing the selected mail…",
                InfoBarSeverity.Informational);

            OutlookSelectionPush.ExportResult export;
            try
            {
                export = await OutlookSelectionPush.ExportSelectedMsgFilesAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                shell.ShowDutyBanner("Outlook capture failed", ex.Message, InfoBarSeverity.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(export.Error) || export.Mails.Count == 0)
            {
                shell.ShowDutyBanner(
                    "Send to Orbit",
                    export.Error ?? "No Outlook message selected.",
                    InfoBarSeverity.Error);
                return;
            }

            _pending.Enqueue(new QueuedBatch(export.Mails, source));
            var depth = Interlocked.Increment(ref _queuedCount);

            var label = export.Mails.Count == 1
                ? $"“{Truncate(export.Mails[0].Subject ?? "mail", 80)}”"
                : $"{export.Mails.Count} messages";

            if (depth == 1 && Volatile.Read(ref _drainRunning) == 0)
            {
                shell.ShowDutyBanner(
                    "Send to Orbit",
                    $"Captured {label} — add a memo, then Orbit will ingest.",
                    InfoBarSeverity.Informational);
            }
            else
            {
                shell.ShowDutyBanner(
                    "Queued for Orbit",
                    $"{label} saved. {depth} in queue — memo prompts will follow in order.",
                    InfoBarSeverity.Informational);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _captureInFlight, 0);
        }

        if (shell is not null)
        {
            await DrainAsync(shell).ConfigureAwait(true);
        }
    }

    private static async Task<ShellPage?> WaitForShellAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            var shell = (App.MainWindow as MainWindow)?.Shell;
            if (shell is not null && shell.XamlRoot is not null)
            {
                return shell;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        return null;
    }

    private async Task DrainAsync(ShellPage shell)
    {
        if (Interlocked.CompareExchange(ref _drainRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (_pending.TryDequeue(out var batch))
            {
                Interlocked.Decrement(ref _queuedCount);
                await ProcessBatchAsync(shell, batch).ConfigureAwait(true);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _drainRunning, 0);
            OrbitPushSignal.TryDeleteRequest();
        }

        // Item may have arrived between empty dequeue and clearing the drain flag.
        if (!_pending.IsEmpty)
        {
            await DrainAsync(shell).ConfigureAwait(true);
        }
    }

    private async Task ProcessBatchAsync(ShellPage shell, QueuedBatch batch)
    {
        var remainingAfter = PendingCount;
        var subjectHint = batch.Mails.Count == 1
            ? batch.Mails[0].Subject
            : $"{batch.Mails.Count} messages";

        try
        {
            if (shell.XamlRoot is null)
            {
                DiscardBatch(batch);
                shell.ShowDutyBanner("Outlook push", "Orbit UI is not ready yet.", InfoBarSeverity.Error);
                return;
            }

            var prompt = await OutlookPushPrompt.ShowAsync(
                shell.XamlRoot,
                mailSummary: subjectHint,
                queuedRemaining: remainingAfter).ConfigureAwait(true);

            if (prompt is null)
            {
                DiscardBatch(batch);
                var more = PendingCount;
                shell.ShowDutyBanner(
                    "Send to Orbit cancelled",
                    more > 0
                        ? $"Skipped this mail. {more} still queued."
                        : "Nothing was pushed for this selection.",
                    InfoBarSeverity.Informational);
                return;
            }

            IReadOnlyList<string>? projectIds = null;
            if (!string.IsNullOrWhiteSpace(prompt.ProjectId))
            {
                projectIds = [prompt.ProjectId];
            }

            OrbitPushActivation.BringMainWindowToFront();

            // Don't block the next queued memo on a long Hermes wait.
            var waitForDuty = PendingCount == 0;
            var floorReloaded = 0;
            var result = await OutlookPushCoordinator.PushExportedAsync(
                App.Settings,
                App.SettingsStore,
                batch.Mails,
                projectIds,
                prompt.Memo,
                waitForDutyBriefing: waitForDuty,
                progress: (title, detail) =>
                    shell.ShowDutyBanner(title, Truncate(detail, 600), InfoBarSeverity.Informational),
                ct: default,
                onFloorReady: () =>
                {
                    if (Interlocked.Exchange(ref floorReloaded, 1) != 0)
                    {
                        return;
                    }

                    shell.ShowDutyBanner(
                        "Task is on the workbench",
                        "Hermes is still finishing the living brief and next action…",
                        InfoBarSeverity.Informational);
                    _ = shell.ReloadWorkbenchAfterIngestAsync();
                })
                .ConfigureAwait(true);

            var suffix = PendingCount > 0
                ? $" Next: {PendingCount} still queued."
                : string.Empty;

            shell.ShowDutyBanner(
                result.StatusLine,
                Truncate(result.Detail + suffix, 700),
                result.Ok
                    ? (string.IsNullOrWhiteSpace(result.Briefing) ? InfoBarSeverity.Informational : InfoBarSeverity.Success)
                    : InfoBarSeverity.Error);

            if (result.Ok && !string.IsNullOrWhiteSpace(result.LastEmailId))
            {
                await HintUnmatchedMailAsync(shell, result.LastEmailId).ConfigureAwait(true);
            }

            if (result.Ok)
            {
                await shell.ReloadWorkbenchAfterIngestAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            DiscardBatch(batch);
            shell.ShowDutyBanner("Outlook push failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _ = batch.Source;
        }
    }

    private static async Task HintUnmatchedMailAsync(ShellPage shell, string emailId)
    {
        try
        {
            using var core = new CoreHostClient(App.Settings, App.SettingsStore);
            var pending = await core.GetPendingSuggestionsAsync().ConfigureAwait(true);
            var unmatched = pending.Count(s =>
                string.Equals(s.SuggestionType, "disambiguate_email_claim", StringComparison.Ordinal)
                && (s.PayloadJson?.Contains(emailId, StringComparison.OrdinalIgnoreCase) ?? false));
            if (unmatched <= 0)
            {
                return;
            }

            shell.ShowDutyBanner(
                "Mail needs a project",
                unmatched == 1
                    ? "This message is still unmatched — open Pulse → Unmatched mail to pick a project (not Agent)."
                    : $"{unmatched} unmatched mail claims pending — open Pulse → Unmatched mail (not Agent).",
                InfoBarSeverity.Informational);
        }
        catch
        {
            // Banner hint is best-effort.
        }
    }

    private static void DiscardBatch(QueuedBatch batch)
    {
        foreach (var mail in batch.Mails)
        {
            try
            {
                if (File.Exists(mail.MsgPath))
                {
                    File.Delete(mail.MsgPath);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string Truncate(string text, int max)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
