using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Calendar;

public sealed class CalendarSyncResult
{
    public int SourcesUpserted { get; init; }

    public int EventsUpserted { get; init; }

    public int LinksCreated { get; init; }

    public int AttentionUpdated { get; init; }

    public IReadOnlyList<string> ProviderStatuses { get; init; } = [];
}

/// <summary>
/// Upserts provider snapshots into calendar_sources / calendar_events,
/// then runs meeting→project linking and attention scoring.
/// </summary>
public sealed class CalendarSyncService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly MeetingProjectLinker _linker;
    private readonly AttentionScorer _attention;
    private readonly Func<IReadOnlyList<ICalendarProvider>> _providersFactory;

    public CalendarSyncService(
        SqliteConnectionFactory factory,
        MeetingProjectLinker? linker = null,
        AttentionScorer? attention = null,
        Func<IReadOnlyList<ICalendarProvider>>? providersFactory = null)
    {
        _factory = factory;
        _linker = linker ?? new MeetingProjectLinker(factory);
        _attention = attention ?? new AttentionScorer(factory);
        _providersFactory = providersFactory ?? BuildDefaultProviders;
    }

    public CalendarSyncResult Sync(CancellationToken cancellationToken = default) =>
        SyncAsync(cancellationToken).GetAwaiter().GetResult();

    public async Task<CalendarSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var statuses = new List<string>();
        var sourcesUpserted = 0;
        var eventsUpserted = 0;

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        var providers = new List<ICalendarProvider>(_providersFactory());
        var knownIcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ics in ListEnabledIcsUris(connection, tx))
        {
            var key = IcsCalendarProvider.NormalizeKey(ics);
            if (!knownIcs.Add(key))
            {
                continue;
            }

            providers.Add(new IcsCalendarProvider(ics));
        }

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
            statuses.Add($"{provider.ProviderId}: {(result.Available ? "ok" : "unavailable")} — {result.StatusMessage}");

            if (!result.Available)
            {
                MarkProviderSourcesUnavailable(connection, tx, provider.ProviderId, result.StatusMessage);
                continue;
            }

            foreach (var source in result.Sources)
            {
                var sourceId = UpsertSource(connection, tx, provider.ProviderId, source, result.StatusMessage);
                sourcesUpserted++;
                foreach (var ev in source.Events)
                {
                    UpsertEvent(connection, tx, sourceId, ev);
                    eventsUpserted++;
                }
            }
        }

        tx.Commit();

        var links = _linker.LinkAll();
        var attention = _attention.RescoreAll();

        return new CalendarSyncResult
        {
            SourcesUpserted = sourcesUpserted,
            EventsUpserted = eventsUpserted,
            LinksCreated = links,
            AttentionUpdated = attention,
            ProviderStatuses = statuses,
        };
    }

    /// <summary>Subscribe an ICS file path or URL as an enabled calendar source (no sync yet).</summary>
    public string SubscribeIcs(string uriOrPath, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriOrPath);
        var uri = uriOrPath.Trim();
        var key = IcsCalendarProvider.NormalizeKey(uri);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? (IcsCalendarProvider.IsHttp(uri) ? "ICS feed" : Path.GetFileName(uri))
            : displayName.Trim();

        var snapshot = new CalendarSourceSnapshot
        {
            ExternalKey = key,
            Name = name,
            CalendarName = name,
            AccountHint = IcsCalendarProvider.IsHttp(uri) ? "url" : "file",
            ConfigUri = uri,
            Events = [],
        };

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        var id = UpsertSource(connection, tx, CalendarProviders.Ics, snapshot, "subscribed");
        tx.Commit();
        return id;
    }

    private static IReadOnlyList<string> ListEnabledIcsUris(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT config_uri
            FROM calendar_sources
            WHERE provider = 'ics'
              AND enabled = 1
              AND archived_at IS NULL
              AND config_uri IS NOT NULL;
            """;
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                list.Add(reader.GetString(0));
            }
        }

        return list;
    }

    private static void MarkProviderSourcesUnavailable(
        SqliteConnection connection,
        SqliteTransaction tx,
        string provider,
        string? message)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            UPDATE calendar_sources
            SET last_sync_at = $t,
                last_sync_status = 'unavailable',
                last_sync_error = $err,
                updated_at = $t
            WHERE provider = $p AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$err", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.ExecuteNonQuery();
    }

    private static string UpsertSource(
        SqliteConnection connection,
        SqliteTransaction tx,
        string provider,
        CalendarSourceSnapshot source,
        string? statusMessage)
    {
        var now = DateTime.UtcNow.ToString("O");
        var existingId = FindSourceId(connection, tx, provider, source.ExternalKey);
        var id = existingId ?? StableId($"{provider}|{source.ExternalKey}");

        if (existingId is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO calendar_sources (
                  id, name, provider, account_hint, external_key, mailbox_name, calendar_name,
                  config_uri, enabled, last_sync_at, last_sync_status, last_sync_error,
                  created_at, updated_at)
                VALUES (
                  $id, $name, $provider, $hint, $key, $mailbox, $cal,
                  $uri, 1, $t, 'ok', NULL, $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$name", source.Name);
            insert.Parameters.AddWithValue("$provider", provider);
            insert.Parameters.AddWithValue("$hint", (object?)source.AccountHint ?? DBNull.Value);
            insert.Parameters.AddWithValue("$key", source.ExternalKey);
            insert.Parameters.AddWithValue("$mailbox", (object?)source.MailboxName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cal", (object?)source.CalendarName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$uri", (object?)source.ConfigUri ?? DBNull.Value);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE calendar_sources
                SET name = $name,
                    account_hint = $hint,
                    mailbox_name = $mailbox,
                    calendar_name = $cal,
                    config_uri = COALESCE($uri, config_uri),
                    last_sync_at = $t,
                    last_sync_status = 'ok',
                    last_sync_error = NULL,
                    updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$name", source.Name);
            update.Parameters.AddWithValue("$hint", (object?)source.AccountHint ?? DBNull.Value);
            update.Parameters.AddWithValue("$mailbox", (object?)source.MailboxName ?? DBNull.Value);
            update.Parameters.AddWithValue("$cal", (object?)source.CalendarName ?? DBNull.Value);
            update.Parameters.AddWithValue("$uri", (object?)source.ConfigUri ?? DBNull.Value);
            update.Parameters.AddWithValue("$t", now);
            update.ExecuteNonQuery();
        }

        _ = statusMessage;
        return id;
    }

    private static void UpsertEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourceId,
        CalendarEventSnapshot ev)
    {
        var now = DateTime.UtcNow.ToString("O");
        var existingId = FindEventId(connection, tx, sourceId, ev.ExternalUid);
        var id = existingId ?? StableId($"{sourceId}|{ev.ExternalUid}");
        var preview = string.IsNullOrWhiteSpace(ev.Body)
            ? null
            : (ev.Body.Length <= 500 ? ev.Body : ev.Body[..500]);

        if (existingId is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO calendar_events (
                  id, calendar_source_id, title, starts_at, ends_at, location,
                  external_uid, body_preview, organizer, attention_score,
                  created_at, updated_at)
                VALUES (
                  $id, $source, $title, $start, $end, $loc,
                  $uid, $body, $org, NULL, $t, $t);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$source", sourceId);
            insert.Parameters.AddWithValue("$title", ev.Title);
            insert.Parameters.AddWithValue("$start", (object?)ev.StartsAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$end", (object?)ev.EndsAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$loc", (object?)ev.Location ?? DBNull.Value);
            insert.Parameters.AddWithValue("$uid", ev.ExternalUid);
            insert.Parameters.AddWithValue("$body", (object?)preview ?? DBNull.Value);
            insert.Parameters.AddWithValue("$org", (object?)ev.Organizer ?? DBNull.Value);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE calendar_events
                SET title = $title,
                    starts_at = $start,
                    ends_at = $end,
                    location = $loc,
                    body_preview = $body,
                    organizer = $org,
                    updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$title", ev.Title);
            update.Parameters.AddWithValue("$start", (object?)ev.StartsAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            update.Parameters.AddWithValue("$end", (object?)ev.EndsAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            update.Parameters.AddWithValue("$loc", (object?)ev.Location ?? DBNull.Value);
            update.Parameters.AddWithValue("$body", (object?)preview ?? DBNull.Value);
            update.Parameters.AddWithValue("$org", (object?)ev.Organizer ?? DBNull.Value);
            update.Parameters.AddWithValue("$t", now);
            update.ExecuteNonQuery();
        }
    }

    private static string? FindSourceId(
        SqliteConnection connection,
        SqliteTransaction tx,
        string provider,
        string externalKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT id FROM calendar_sources
            WHERE provider = $p AND external_key = $k AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$k", externalKey);
        return cmd.ExecuteScalar() as string;
    }

    private static string? FindEventId(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourceId,
        string externalUid)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT id FROM calendar_events
            WHERE calendar_source_id = $s AND external_uid = $u AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", sourceId);
        cmd.Parameters.AddWithValue("$u", externalUid);
        return cmd.ExecuteScalar() as string;
    }

    internal static string StableId(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes).ToString("D");
    }

    private IReadOnlyList<ICalendarProvider> BuildDefaultProviders()
    {
        var list = new List<ICalendarProvider>
        {
            new OutlookCalendarProvider(),
            new GraphCalendarProvider(),
        };

        // Settings ICS path is subscribed into DB via API; sync reads enabled ICS rows.
        // Also honor HostOptions-backed path when injected via custom factory in DI.
        return list;
    }
}
