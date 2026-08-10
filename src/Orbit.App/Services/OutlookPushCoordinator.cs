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
        Action<string, string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Invoke("Reading Outlook…", "Grabbing the selected message.");
        var export = await OutlookSelectionPush.ExportSelectedMsgFilesAsync(ct).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(export.Error) || export.Mails.Count == 0)
        {
            return new PushResult(
                false,
                export.Error ?? "No Outlook message selected.",
                "Select mail in Classic Outlook, then press Ctrl+Shift+O.",
                0,
                null,
                null);
        }

        var pushStarted = DateTimeOffset.UtcNow;
        var ok = 0;
        string? lastId = null;
        string? lastSubject = null;
        var failNotes = new List<string>();
        foreach (var mail in export.Mails)
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
                var ingested = await client.IngestEmailAsync(dest, projectIds, ct).ConfigureAwait(true);
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

        progress?.Invoke(
            $"Pushed “{lastSubject ?? "mail"}” — Hermes is working…",
            "Reading the email, matching projects, updating the workbench.");

        var briefing = await WaitForDutyBriefingAsync(
            settings,
            store,
            lastId,
            lastSubject,
            pushStarted,
            progress,
            ct).ConfigureAwait(true);

        if (failNotes.Count > 0)
        {
            // keep going
        }

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
            "Mail is in Orbit. Hermes has not finished this run yet — leave the Workbench open; the banner will update when you push again, or check Settings → Hermes.",
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
        int timeoutSeconds = 120)
    {
        var floor = notBeforeUtc ?? DateTimeOffset.UtcNow.AddSeconds(-5);
        try
        {
            using var client = new CoreHostClient(settings, store);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var dash = await client.GetOperatorDashboardAsync(ct).ConfigureAwait(true);
                if (dash is null)
                {
                    await Task.Delay(1500, ct).ConfigureAwait(true);
                    continue;
                }

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

                    if (!string.IsNullOrWhiteSpace(run.BriefingSummary)
                        && (run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                            || run.Status.Equals("skipped", StringComparison.OrdinalIgnoreCase)
                            || run.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)))
                    {
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

                    // Running run matched this email — keep waiting (opened at ingest for webhook path too).
                    if (run.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
                        && RunMatchesEmail(run, emailId, subject))
                    {
                        progress?.Invoke(
                            $"Hermes is organizing “{subject ?? "mail"}”…",
                            "Still working — stay on the Workbench.");
                    }
                }

                await Task.Delay(1500, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // fall through
        }

        return null;
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
