using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;

namespace Orbit_App.Services;

/// <summary>Copies/materializes a dropped .msg and posts it to Core Host ingest.</summary>
public static class EmailIngestUi
{
    public static async Task<(CoreHostClient.EmailIngestResult? Result, string? Error)> TryIngestAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        MsgDropHelper.MsgDropPayload payload,
        IReadOnlyList<string>? projectIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var inbox = Path.Combine(settings.GeneratedFilesRoot, "inbox");
        Directory.CreateDirectory(inbox);
        var dest = Path.Combine(inbox, $"{Guid.NewGuid():N}.msg");

        if (payload.Bytes is { Length: > 0 })
        {
            await File.WriteAllBytesAsync(dest, payload.Bytes, ct);
        }
        else if (!string.IsNullOrWhiteSpace(payload.SourcePath))
        {
            File.Copy(payload.SourcePath, dest, overwrite: true);
        }
        else
        {
            return (null, "Drop did not include a .msg file.");
        }

        using var client = new CoreHostClient(settings, store);
        var result = await client.IngestEmailAsync(dest, projectIds, ct);
        if (result is null)
        {
            return (null, client.LastEmailIngestError ?? "Email ingest failed. Is Core Host running?");
        }

        return (result, null);
    }

    public static async Task<CoreHostClient.EmailIngestResult?> IngestAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        MsgDropHelper.MsgDropPayload payload,
        IReadOnlyList<string>? projectIds = null,
        CancellationToken ct = default)
    {
        var (result, _) = await TryIngestAsync(settings, store, payload, projectIds, ct);
        return result;
    }

    public static string BuildCaptureText(CoreHostClient.EmailIngestResult email)
    {
        var from = email.Participants
            .Where(p => p.Role.Contains("from", StringComparison.OrdinalIgnoreCase)
                        || p.Role.Contains("sender", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.DisplayName ?? p.Address)
            .FirstOrDefault()
            ?? email.Participants.Select(p => p.DisplayName ?? p.Address).FirstOrDefault()
            ?? "unknown";

        return
            $"Email: {email.Subject ?? "(no subject)"}\n" +
            $"From: {from}\n" +
            $"Sent: {email.SentAt ?? "—"}\n\n" +
            $"{email.BodyPreview ?? ""}".Trim();
    }
}
