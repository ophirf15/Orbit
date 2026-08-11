using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;

namespace Orbit_App.Services;

/// <summary>
/// One-gesture Outlook → Orbit ingest + duty wait. Stay on the workbench; no Agent hop.
/// </summary>
public static class OutlookPushCoordinator
{
    public sealed record PushResult(
        bool Ok,
        string StatusLine,
        string Detail,
        int PushedCount,
        string? LastEmailId,
        string? Briefing);

    public static async Task<PushResult> PushSelectedAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        IReadOnlyList<string>? projectIds = null,
        string? memo = null,
        Action<string, string>? progress = null,
        bool waitForDutyBriefing = true,
        Action? onFloorReady = null,
        CancellationToken ct = default)
    {
        progress?.Invoke("Reading Outlook…", "Grabbing the selected message.");
        var export = await OutlookSelectionPush.ExportSelectedMsgFilesAsync(ct).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(export.Error) || export.Mails.Count == 0)
        {
            return new PushResult(
                false,
                export.Error ?? "No Outlook message selected.",
                "Select mail in Classic Outlook, then press Ctrl+Shift+O or the Orbit button.",
                0,
                null,
                null);
        }

        return await PushExportedAsync(
            settings,
            store,
            export.Mails,
            projectIds,
            memo,
            waitForDutyBriefing,
            progress,
            ct,
            onFloorReady).ConfigureAwait(true);
    }

    /// <summary>Ingest already-captured .msg files (queue snapshots).</summary>
    public static async Task<PushResult> PushExportedAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        IReadOnlyList<OutlookSelectionPush.ExportedMail> mails,
        IReadOnlyList<string>? projectIds = null,
        string? memo = null,
        bool waitForDutyBriefing = true,
        Action<string, string>? progress = null,
        CancellationToken ct = default,
        Action? onFloorReady = null)
    {
        if (mails.Count == 0)
        {
            return new PushResult(
                false,
                "No Outlook message selected.",
                "Select mail in Classic Outlook, then press Ctrl+Shift+O or the Orbit button.",
                0,
                null,
                null);
        }

        var pushStarted = DateTimeOffset.UtcNow;
        var ok = 0;
        string? lastId = null;
        string? lastSubject = null;
        var failNotes = new List<string>();
        var trimmedMemo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();
        foreach (var mail in mails)
        {
            try
            {
                progress?.Invoke(
                    $"Loading “{mail.Subject ?? "mail"}”…",
                    "Saving into Orbit and waking Hermes.");
                var inbox = Path.Combine(settings.GeneratedFilesRoot, "inbox");
                Directory.CreateDirectory(inbox);
                var dest = Path.Combine(inbox, $"{Guid.NewGuid():N}.msg");
                File.Copy(mail.MsgPath, dest, overwrite: true);

                using var client = new CoreHostClient(settings, store);
                var ingested = await client.IngestEmailAsync(dest, projectIds, trimmedMemo, ct)
                    .ConfigureAwait(true);
                if (ingested is null)
                {
                    failNotes.Add(mail.Subject ?? Path.GetFileName(mail.MsgPath));
                    continue;
                }

                ok++;
                lastId = ingested.Id;
                lastSubject = ingested.Subject ?? mail.Subject;
            }
            catch (Exception ex)
            {
                failNotes.Add($"{mail.Subject ?? "mail"}: {ex.Message}");
            }
            finally
            {
                try
                {
                    File.Delete(mail.MsgPath);
                }
                catch
                {
                    // ignore temp cleanup
                }
            }
        }

        if (ok == 0)
        {
            return new PushResult(
                false,
                "Push failed.",
                failNotes.Count == 0 ? "Core Host may be down." : string.Join("; ", failNotes.Take(3)),
                0,
                null,
                null);
        }

        if (!waitForDutyBriefing)
        {
            return new PushResult(
                true,
                $"Pushed “{lastSubject ?? "mail"}” — Hermes is working in the background.",
                "Queued the next capture when ready. Hermes will organize this mail without blocking the queue.",
                ok,
                lastId,
                null);
        }

        progress?.Invoke(
            $"Pushed “{lastSubject ?? "mail"}” — Hermes is working…",
            HermesThinkingCopy.NextDutyStage(TimeSpan.Zero, 0));

        var briefing = await WaitForDutyBriefingAsync(
            settings,
            store,
            lastId,
            lastSubject,
            pushStarted,
            progress,
            ct,
            timeoutSeconds: 120,
            onFloorReady: onFloorReady).ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(briefing))
        {
            return new PushResult(
                true,
                $"Done — “{lastSubject ?? "mail"}” organized.",
                briefing!,
                ok,
                lastId,
                briefing);
        }

        return new PushResult(
            true,
            $"Pushed “{lastSubject ?? "mail"}” — still organizing.",
            "Mail is in Orbit, but no duty briefing arrived in time. " +
            "Stuck Hermes runs (if any) were cleared automatically — try pushing again. " +
            "Or Settings → Hermes → Clear stuck operator runs. Check Pulse for briefings.",
            ok,
            lastId,
            null);
    }

    public static async Task<string?> WaitForDutyBriefingAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        string? emailId,
        string? subject = null,
        DateTimeOffset? notBeforeUtc = null,
        Action<string, string>? progress = null,
        CancellationToken ct = default,
        int timeoutSeconds = 120,
        Action? onFloorReady = null)
    {
        var floor = notBeforeUtc ?? DateTimeOffset.UtcNow.AddSeconds(-5);
        var waitStarted = DateTimeOffset.UtcNow;
        var idleTick = 0;
        var floorSignaled = false;
        try
        {
            using var client = new CoreHostClient(settings, store);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var elapsed = DateTimeOffset.UtcNow - waitStarted;
                var dash = await client.GetOperatorDashboardAsync(ct).ConfigureAwait(true);
                if (dash is null)
                {
                    progress?.Invoke(
                        TitleForElapsed(subject, elapsed),
                        HermesThinkingCopy.DutyBannerDetail(elapsed, idleTick++, liveProgress: null));
                    await Task.Delay(1500, ct).ConfigureAwait(true);
                    continue;
                }

                var matchedRunning = false;
                foreach (var run in dash.RecentRuns)
                {
                    if (!run.TriggerKind.Contains("email", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!DateTimeOffset.TryParse(run.CreatedAt, out var created)
                        || created < floor.AddSeconds(-2))
                    {
                        continue;
                    }

                    if (!RunMatchesEmail(run, emailId, subject))
                    {
                        continue;
                    }

                    if (run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                        || run.Status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
                        || run.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(run.BriefingSummary))
                        {
                            return run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                                ? $"Done — “{subject ?? "mail"}” organized."
                                : $"[{run.Status}] Hermes finished without a briefing text.";
                        }

                        if (run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                            && (run.BriefingSummary.Contains("Nothing material to surface", StringComparison.OrdinalIgnoreCase)
                                || run.BriefingSummary.Equals("[SILENT]", StringComparison.OrdinalIgnoreCase)))
                        {
                            return $"Done — “{subject ?? "mail"}” noted. Nothing needed on the workbench.";
                        }

                        return run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                            ? run.BriefingSummary
                            : $"[{run.Status}] {run.BriefingSummary}";
                    }

                    if (run.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedRunning = true;
                        SignalFloorReadyOnce(ref floorSignaled, elapsed, onFloorReady);
                        var detail = HermesThinkingCopy.DutyBannerDetail(
                            elapsed,
                            idleTick,
                            liveProgress: run.BriefingSummary);
                        progress?.Invoke(TitleForElapsed(subject, elapsed), detail);
                    }
                }

                if (!matchedRunning)
                {
                    SignalFloorReadyOnce(ref floorSignaled, elapsed, onFloorReady);
                    progress?.Invoke(
                        TitleForElapsed(subject, elapsed),
                        HermesThinkingCopy.DutyBannerDetail(elapsed, idleTick, liveProgress: null));
                }

                idleTick++;
                await Task.Delay(1500, ct).ConfigureAwait(true);
            }

            try
            {
                var cleared = await client.ClearStuckOperatorRunsAsync(ct).ConfigureAwait(true);
                if (cleared > 0)
                {
                    progress?.Invoke(
                        $"Pushed “{subject ?? "mail"}” — cleared {cleared} stuck Hermes run(s).",
                        "Next push should not stall. Check Pulse if a briefing already posted.");
                }
            }
            catch
            {
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return null;
    }

    private static string TitleForElapsed(string? subject, TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 12
            ? $"“{subject ?? "mail"}” is in Orbit — Hermes finishing notes…"
            : $"Pushed “{subject ?? "mail"}” — Hermes is working…";

    private static void SignalFloorReadyOnce(ref bool signaled, TimeSpan elapsed, Action? onFloorReady)
    {
        if (signaled || onFloorReady is null || elapsed.TotalSeconds < 8)
        {
            return;
        }

        signaled = true;
        try
        {
            onFloorReady();
        }
        catch
        {
        }
    }

    private static bool RunMatchesEmail(OperatorRunVm run, string? emailId, string? subject)
    {
        var blob = $"{run.TriggerPayloadJson} {run.BriefingSummary}";
        if (!string.IsNullOrWhiteSpace(emailId)
            && blob.Contains(emailId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(subject)
            && blob.Contains(subject, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // New run without payload echo yet — accept newest running/completed email trigger after floor.
        return string.IsNullOrWhiteSpace(emailId) && string.IsNullOrWhiteSpace(subject);
    }
}
