using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orbit.Infrastructure.Email;

public sealed class OutlookMsgExportRequest
{
    public string? EntryId { get; init; }

    public string? InternetMessageId { get; init; }

    public string? Subject { get; init; }

    /// <summary>When true (default), fall back to Explorer/Inspector selection.</summary>
    public bool PreferSelection { get; init; } = true;
}

public sealed class OutlookMsgExportResult
{
    public bool Ok { get; init; }

    public string? MsgPath { get; init; }

    public string? Subject { get; init; }

    public string? Error { get; init; }
}

public interface IOutlookMsgExport
{
    Task<OutlookMsgExportResult> ExportAsync(OutlookMsgExportRequest request, CancellationToken ct = default);
}

/// <summary>
/// Out-of-process Classic Outlook COM: resolve a mail item and SaveAs .msg.
/// Prefer Internet-Message-ID / EntryID; optionally fall back to current selection.
/// </summary>
public sealed class OutlookMsgExport : IOutlookMsgExport
{
    private const int OlMail = 43;
    private const int OlMsg = 3;
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(45);
    private const string InternetMessageIdDasl = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";

    public Task<OutlookMsgExportResult> ExportAsync(OutlookMsgExportRequest request, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Fail("Outlook COM is only available on Windows."));
        }

#pragma warning disable CA1416
        return RunStaAsync(() => ExportCore(request), ct);
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static OutlookMsgExportResult ExportCore(OutlookMsgExportRequest request)
    {
        object? app = null;
        object? mail = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null)
            {
                return Fail("Outlook is not registered on this machine.");
            }

            app = TryGetActiveOutlook() ?? Activator.CreateInstance(type);
            if (app is null)
            {
                return Fail("Could not attach to Outlook.Application (is Classic Outlook running?).");
            }

            mail = ResolveMail(app, request);
            if (mail is null)
            {
                return Fail(
                    "Could not resolve the Outlook message. Keep the mail selected/open in Classic Outlook, or ensure Message-ID is available.");
            }

            var subject = GetProperty(mail, "Subject") as string;
            var dir = Path.Combine(Path.GetTempPath(), "OrbitOutlookPush");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".msg");

            try
            {
                Invoke(mail, "Save");
            }
            catch
            {
                // ignore — not always allowed
            }

            Invoke(mail, "SaveAs", path, OlMsg);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return Fail("Outlook SaveAs produced an empty .msg file.");
            }

            return new OutlookMsgExportResult
            {
                Ok = true,
                MsgPath = path,
                Subject = subject,
            };
        }
        catch (Exception ex)
        {
            return Fail("Outlook export failed: " + UnwrapComMessage(ex));
        }
        finally
        {
            ReleaseCom(mail);
            ReleaseCom(app);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object? ResolveMail(object app, OutlookMsgExportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.EntryId))
        {
            try
            {
                var ns = Invoke(app, "GetNamespace", "MAPI");
                if (ns is not null)
                {
                    var byEntry = Invoke(ns, "GetItemFromID", request.EntryId.Trim());
                    if (byEntry is not null && IsMail(byEntry))
                    {
                        return byEntry;
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        if (!string.IsNullOrWhiteSpace(request.InternetMessageId))
        {
            var found = FindByInternetMessageId(app, request.InternetMessageId!);
            if (found is not null)
            {
                return found;
            }
        }

        if (request.PreferSelection)
        {
            var selected = GetSelectedMailItems(app);
            if (selected.Count > 0)
            {
                // Caller owns release of the chosen item; release extras.
                for (var i = 1; i < selected.Count; i++)
                {
                    ReleaseCom(selected[i]);
                }

                return selected[0];
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static object? FindByInternetMessageId(object app, string internetMessageId)
    {
        var candidates = NormalizeMessageIdCandidates(internetMessageId);
        if (candidates.Count == 0)
        {
            return null;
        }

        object? ns = null;
        try
        {
            ns = Invoke(app, "GetNamespace", "MAPI");
            if (ns is null)
            {
                return null;
            }

            // olFolderInbox = 6
            var inbox = Invoke(ns, "GetDefaultFolder", 6);
            if (inbox is null)
            {
                return null;
            }

            try
            {
                foreach (var id in candidates)
                {
                    var escaped = id.Replace("'", "''", StringComparison.Ordinal);
                    var filter = $"@SQL=\"{InternetMessageIdDasl}\" = '{escaped}'";
                    object? items = null;
                    object? restricted = null;
                    try
                    {
                        items = GetProperty(inbox, "Items");
                        if (items is null)
                        {
                            continue;
                        }

                        restricted = Invoke(items, "Restrict", filter);
                        if (restricted is null)
                        {
                            continue;
                        }

                        var count = Convert.ToInt32(GetProperty(restricted, "Count") ?? 0);
                        if (count < 1)
                        {
                            continue;
                        }

                        var item = GetProperty(restricted, "Item", 1) ?? Invoke(restricted, "Item", 1);
                        if (item is not null && IsMail(item))
                        {
                            return item;
                        }
                    }
                    finally
                    {
                        ReleaseCom(restricted);
                        ReleaseCom(items);
                    }
                }
            }
            finally
            {
                ReleaseCom(inbox);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(ns);
        }

        return null;
    }

    private static List<string> NormalizeMessageIdCandidates(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var list = new List<string> { trimmed };
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
        {
            list.Add(trimmed[1..^1]);
        }
        else
        {
            list.Add("<" + trimmed + ">");
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static OutlookMsgExportResult Fail(string error) =>
        new() { Ok = false, Error = error };

    private static object? TryGetActiveOutlook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var clsid = Type.GetTypeFromProgID("Outlook.Application")?.GUID ?? Guid.Empty;
            if (clsid == Guid.Empty)
            {
                return null;
            }

            var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
            return hr == 0 ? obj : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static List<object> GetSelectedMailItems(object app)
    {
        var list = new List<object>();

        try
        {
            var inspector = GetProperty(app, "ActiveInspector");
            if (inspector is not null)
            {
                var current = GetProperty(inspector, "CurrentItem");
                if (current is not null && IsMail(current))
                {
                    list.Add(current);
                    return list;
                }
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var explorer = GetProperty(app, "ActiveExplorer");
            if (explorer is null)
            {
                return list;
            }

            var selection = GetProperty(explorer, "Selection");
            if (selection is null)
            {
                return list;
            }

            var count = Convert.ToInt32(GetProperty(selection, "Count") ?? 0);
            for (var i = 1; i <= count; i++)
            {
                object? item = null;
                try
                {
                    item = GetProperty(selection, "Item", i);
                }
                catch
                {
                    try
                    {
                        item = Invoke(selection, "Item", i);
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (item is not null && IsMail(item))
                {
                    list.Add(item);
                }
            }
        }
        catch
        {
            // empty
        }

        return list;
    }

    private static bool IsMail(object item)
    {
        try
        {
            return Convert.ToInt32(GetProperty(item, "Class")) == OlMail;
        }
        catch
        {
            return false;
        }
    }

    private static object? GetProperty(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target: target,
            args: args.Length == 0 ? null : args);

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target: target,
            args: args);

    private static void ReleaseCom(object? com)
    {
        if (com is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(com))
        {
            try
            {
                Marshal.FinalReleaseComObject(com);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static string UnwrapComMessage(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner })
        {
            return inner.Message;
        }

        return ex.Message;
    }

    [SupportedOSPlatform("windows")]
    private static Task<T> RunStaAsync<T>(Func<T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Orbit-OutlookMsgExport-STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return WaitWithTimeout(tcs.Task, ExportTimeout, ct);
    }

    private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken ct)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (finished == task)
        {
            return await task.ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Outlook did not respond in time. Keep Classic Outlook open and try again.");
    }
}
