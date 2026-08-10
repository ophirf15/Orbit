using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orbit_App.Services;

/// <summary>
/// Out-of-process Classic Outlook push: read Explorer/Inspector selection via COM,
/// SaveAs .msg, return temp paths for Core ingest.
/// Outlook COM must run on an STA thread — Task.Run (MTA) hangs after selection.
/// </summary>
public static class OutlookSelectionPush
{
    private const int OlMail = 43;
    private const int OlMsg = 3;
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(45);

    public sealed record ExportedMail(string MsgPath, string? Subject);

    public sealed record ExportResult(IReadOnlyList<ExportedMail> Mails, string? Error);

    [SupportedOSPlatform("windows")]
    public static Task<ExportResult> ExportSelectedMsgFilesAsync(CancellationToken ct = default) =>
        RunStaAsync(() => ExportSelectedMsgFilesCore(), ct);

    [SupportedOSPlatform("windows")]
    private static ExportResult ExportSelectedMsgFilesCore()
    {
        var exported = new List<ExportedMail>();
        object? app = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null)
            {
                return new ExportResult([], "Outlook is not registered on this machine.");
            }

            // Prefer the running Classic Outlook instance.
            app = TryGetActiveOutlook() ?? Activator.CreateInstance(type);
            if (app is null)
            {
                return new ExportResult([], "Could not attach to Outlook.Application (is Classic Outlook running?).");
            }

            var mails = GetSelectedMailItems(app);
            if (mails.Count == 0)
            {
                return new ExportResult(
                    [],
                    "No mail selected in Classic Outlook. Select one or more messages in the list (or open one), then try again.");
            }

            var dir = Path.Combine(Path.GetTempPath(), "OrbitOutlookPush");
            Directory.CreateDirectory(dir);

            var saveFailures = new List<string>();
            foreach (var mail in mails)
            {
                string? subject = null;
                try
                {
                    subject = GetProperty(mail, "Subject") as string;
                    var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".msg");

                    // Some items need Save before SaveAs when dirty.
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
                        saveFailures.Add(subject ?? "(no subject)");
                        continue;
                    }

                    exported.Add(new ExportedMail(path, subject));
                }
                catch (Exception ex)
                {
                    saveFailures.Add((subject ?? "(no subject)") + ": " + ex.Message);
                }
                finally
                {
                    ReleaseCom(mail);
                }
            }

            if (exported.Count == 0)
            {
                var detail = saveFailures.Count == 0
                    ? "Outlook SaveAs produced no .msg files."
                    : "Outlook SaveAs failed: " + string.Join("; ", saveFailures.Take(3));
                return new ExportResult([], detail);
            }

            return new ExportResult(exported, null);
        }
        catch (Exception ex)
        {
            foreach (var item in exported)
            {
                TryDelete(item.MsgPath);
            }

            return new ExportResult([], "Outlook push failed: " + UnwrapComMessage(ex));
        }
        finally
        {
            ReleaseCom(app);
        }
    }

    private static object? TryGetActiveOutlook()
    {
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

        // Inspector (open mail window) first
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
        if (com is not null && Marshal.IsComObject(com))
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
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
            Name = "Orbit-Outlook-STA",
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
            "Outlook did not respond in time. Keep Classic Outlook open, select a mail, and try again.");
    }
}
