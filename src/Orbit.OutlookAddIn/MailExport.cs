using System.Runtime.InteropServices;

namespace Orbit.OutlookAddIn;

/// <summary>Late-bound Outlook selection/export (avoids PIA in the Connect type graph).</summary>
internal static class MailSelection
{
    public static IReadOnlyList<object> GetSelectedMailItems(object outlookApplication)
    {
        var list = new List<object>();

        try
        {
            dynamic app = outlookApplication;
            dynamic? inspector = app.ActiveInspector();
            if (inspector is not null)
            {
                object? current = inspector.CurrentItem;
                if (current is not null && IsMailItem(current))
                {
                    list.Add(current);
                    return list;
                }
            }
        }
        catch
        {
            // fall through to explorer selection
        }

        try
        {
            dynamic app = outlookApplication;
            dynamic? explorer = app.ActiveExplorer();
            dynamic? selection = explorer?.Selection;
            if (selection is null)
            {
                return list;
            }

            int count = selection.Count;
            for (var i = 1; i <= count; i++)
            {
                object? item = selection[i];
                if (item is not null && IsMailItem(item))
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

    private static bool IsMailItem(object item)
    {
        try
        {
            // olMail = 43
            dynamic d = item;
            return (int)d.Class == 43;
        }
        catch
        {
            return false;
        }
    }
}

internal static class MailExport
{
    private const int OlMsg = 3; // OlSaveAsType.olMSG

    public static string SaveMsgTemp(object mailItem)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OrbitOutlook");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".msg");
        dynamic mail = mailItem;
        mail.SaveAs(path, OlMsg);
        if (!File.Exists(path))
        {
            throw new IOException("Outlook SaveAs did not create a .msg file.");
        }

        return path;
    }

    public static string? SafeSubject(object mailItem)
    {
        try
        {
            dynamic mail = mailItem;
            return mail.Subject as string;
        }
        catch
        {
            return null;
        }
    }
}

internal static class AddInLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orbit");
            Directory.CreateDirectory(dir);
            var line = DateTime.Now.ToString("O") + " " + message + Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, "outlook-addin.log"), line);
            }
        }
        catch
        {
            // never throw from logging
        }
    }
}
