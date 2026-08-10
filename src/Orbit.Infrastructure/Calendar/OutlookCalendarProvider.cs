using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orbit.Infrastructure.Calendar;

/// <summary>
/// Classic Outlook COM/Interop best-effort reader. Never throws to callers —
/// returns <see cref="CalendarProviderResult.Available"/> = false when Outlook
/// is missing, not running, inaccessible, or times out (profile UI).
/// </summary>
public sealed class OutlookCalendarProvider : ICalendarProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public string ProviderId => CalendarProviders.Outlook;

    public async Task<CalendarProviderResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unavailable("Outlook COM is only available on Windows.");
        }

        try
        {
            // WaitAsync abandons a blocked COM call after timeout (token alone cannot interrupt COM).
#pragma warning disable CA1416 // OperatingSystem.IsWindows guard above
            var work = Task.Run(() => ReadWindows(CancellationToken.None), CancellationToken.None);
#pragma warning restore CA1416
            return await work.WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Unavailable("Outlook COM timed out (not running or waiting for profile UI).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("Outlook COM timed out (not running or waiting for profile UI).");
        }
        catch (OperationCanceledException)
        {
            return Unavailable("Outlook COM read cancelled.");
        }
        catch (Exception ex)
        {
            return Unavailable("Outlook read failed: " + ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static CalendarProviderResult ReadWindows(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null)
            {
                return Unavailable("Outlook COM ProgID not registered on this machine.");
            }

            object? app = null;
            try
            {
                app = Activator.CreateInstance(type);
                if (app is null)
                {
                    return Unavailable("Could not create Outlook.Application.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var ns = Invoke(app, "GetNamespace", "MAPI");
                if (ns is null)
                {
                    return Unavailable("Outlook MAPI namespace unavailable.");
                }

                // Do not call Logon — it can block on profile/UI prompts.

                var sources = new List<CalendarSourceSnapshot>();
                var stores = GetProperty(ns, "Stores");
                if (stores is null)
                {
                    return Unavailable("Outlook stores collection unavailable (is Outlook signed in?).");
                }

                var storeCount = Convert.ToInt32(GetProperty(stores, "Count") ?? 0);
                for (var i = 1; i <= storeCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    object? store = null;
                    try
                    {
                        store = Invoke(stores, "Item", i);
                        if (store is null)
                        {
                            continue;
                        }

                        var storeName = Convert.ToString(GetProperty(store, "DisplayName")) ?? $"Store {i}";
                        object? calendar = null;
                        try
                        {
                            calendar = Invoke(store, "GetDefaultFolder", 9); // olFolderCalendar = 9
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (calendar is null)
                        {
                            continue;
                        }

                        var calName = Convert.ToString(GetProperty(calendar, "Name")) ?? "Calendar";
                        var events = ReadAppointments(calendar, cancellationToken);
                        sources.Add(new CalendarSourceSnapshot
                        {
                            ExternalKey = $"outlook:{storeName}:{calName}",
                            Name = $"{storeName} / {calName}",
                            MailboxName = storeName,
                            CalendarName = calName,
                            AccountHint = storeName,
                            Events = events,
                        });
                    }
                    finally
                    {
                        ReleaseCom(store);
                    }
                }

                if (sources.Count == 0)
                {
                    return Unavailable("Outlook opened but no accessible calendars were found.");
                }

                return new CalendarProviderResult
                {
                    Available = true,
                    StatusMessage = $"Read {sources.Count} Outlook calendar(s).",
                    Sources = sources,
                };
            }
            finally
            {
                ReleaseCom(app);
            }
        }
        catch (COMException ex)
        {
            return Unavailable("Outlook COM unavailable: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable("Outlook read failed: " + ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<CalendarEventSnapshot> ReadAppointments(object calendarFolder, CancellationToken ct)
    {
        var list = new List<CalendarEventSnapshot>();
        object? items = null;
        try
        {
            items = GetProperty(calendarFolder, "Items");
            if (items is null)
            {
                return list;
            }

            try
            {
                Invoke(items, "Sort", "[Start]", Type.Missing);
            }
            catch (Exception)
            {
                // sort optional
            }

            var count = Convert.ToInt32(GetProperty(items, "Count") ?? 0);
            var max = Math.Min(count, 200);
            var windowStart = DateTime.UtcNow.AddDays(-1);
            var windowEnd = DateTime.UtcNow.AddDays(30);

            for (var i = 1; i <= max; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = Invoke(items, "Item", i);
                    if (item is null)
                    {
                        continue;
                    }

                    var classId = Convert.ToInt32(GetProperty(item, "Class") ?? 0);
                    if (classId != 26) // olAppointment = 26
                    {
                        continue;
                    }

                    var start = AsDateTimeOffset(GetProperty(item, "Start"));
                    var end = AsDateTimeOffset(GetProperty(item, "End"));
                    if (start is not null && (start < windowStart || start > windowEnd))
                    {
                        continue;
                    }

                    var subject = Convert.ToString(GetProperty(item, "Subject")) ?? "(untitled)";
                    var entryId = Convert.ToString(GetProperty(item, "EntryID"))
                        ?? $"outlook:{subject}:{start?.ToString("O")}";
                    var location = Convert.ToString(GetProperty(item, "Location"));
                    var body = Convert.ToString(GetProperty(item, "Body"));
                    var organizer = Convert.ToString(GetProperty(item, "Organizer"));
                    if (!string.IsNullOrWhiteSpace(body) && body.Length > 4000)
                    {
                        body = body[..4000];
                    }

                    list.Add(new CalendarEventSnapshot
                    {
                        ExternalUid = entryId,
                        Title = subject,
                        StartsAt = start,
                        EndsAt = end,
                        Location = string.IsNullOrWhiteSpace(location) ? null : location,
                        Body = string.IsNullOrWhiteSpace(body) ? null : body,
                        Organizer = string.IsNullOrWhiteSpace(organizer) ? null : organizer,
                    });
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        finally
        {
            ReleaseCom(items);
        }

        return list;
    }

    private static CalendarProviderResult Unavailable(string message) =>
        new()
        {
            Available = false,
            StatusMessage = message,
            Sources = [],
        };

    private static object? Invoke(object target, string name, params object?[] args)
    {
        return target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target: target,
            args: args);
    }

    private static object? GetProperty(object target, string name)
    {
        return target.GetType().InvokeMember(
            name,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target: target,
            args: null);
    }

    private static DateTimeOffset? AsDateTimeOffset(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is DateTime dt)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUniversalTime();
        }

        if (value is DateTimeOffset dto)
        {
            return dto.ToUniversalTime();
        }

        if (DateTime.TryParse(Convert.ToString(value), out var parsed))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local)).ToUniversalTime();
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseCom(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try
            {
                Marshal.FinalReleaseComObject(com);
            }
            catch (Exception)
            {
                // best-effort
            }
        }
    }
}
