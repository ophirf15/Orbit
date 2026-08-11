using System.Text;

namespace Orbit.Infrastructure.Diagnostics;

/// <summary>
/// Append-only support log under %LocalAppData%\Orbit\logs for prod handoff.
/// No secrets — callers must redact API keys and email bodies.
/// </summary>
public static class OrbitSupportLog
{
    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "logs");

    public static string AppLogPath => Path.Combine(LogDirectory, "orbit-app.log");

    public static string HostLogPath => Path.Combine(LogDirectory, "orbit-host.log");

    public static string ErrorsJsonlPath => Path.Combine(LogDirectory, "errors.jsonl");

    public static void Write(string category, string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [")
                .Append(category.Trim())
                .Append("] ")
                .Append(Sanitize(message));
            if (ex is not null)
            {
                line.Append(" | ").Append(Sanitize(ex.GetType().Name)).Append(": ").Append(Sanitize(ex.Message));
            }

            line.AppendLine();
            File.AppendAllText(AppLogPath, line.ToString());
        }
        catch
        {
            // never throw from logging
        }
    }

    public static void WriteHost(string category, string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = DateTimeOffset.Now.ToString("O")
                       + " [" + category.Trim() + "] "
                       + Sanitize(message)
                       + (ex is null ? string.Empty : " | " + Sanitize(ex.GetType().Name) + ": " + Sanitize(ex.Message))
                       + Environment.NewLine;
            File.AppendAllText(HostLogPath, line);
        }
        catch
        {
            // ignore
        }
    }

    public static void WriteErrorEvent(string code, string message, string? detail = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var payload = new Dictionary<string, string?>
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["code"] = code,
                ["message"] = Sanitize(message),
                ["detail"] = string.IsNullOrWhiteSpace(detail) ? null : Sanitize(detail),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            File.AppendAllText(ErrorsJsonlPath, json + Environment.NewLine);
            Write("error", code + ": " + message);
        }
        catch
        {
            // ignore
        }
    }

    public static string ReadTail(string path, int maxChars = 64_000)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= maxChars)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }

            stream.Seek(-maxChars, SeekOrigin.End);
            using var tailReader = new StreamReader(stream);
            _ = tailReader.ReadLine(); // drop partial first line
            return tailReader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "(could not read log: " + ex.Message + ")";
        }
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var t = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        // Strip anything that looks like a bearer token or long hex key.
        if (t.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            t = System.Text.RegularExpressions.Regex.Replace(
                t,
                @"Bearer\s+\S+",
                "Bearer ***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return t.Length <= 2000 ? t : t[..2000] + "…";
    }
}
