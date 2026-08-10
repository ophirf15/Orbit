using System.Text;

namespace Orbit.Infrastructure.Hermes;

/// <summary>
/// Minimal .env read/upsert for Hermes HERMES_HOME (keeps comments and unknown keys).
/// </summary>
public static class HermesEnvFile
{
    public static IReadOnlyDictionary<string, string> Read(string envPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(envPath))
        {
            return map;
        }

        foreach (var raw in File.ReadAllLines(envPath))
        {
            if (!TryParseAssignment(raw, out var key, out var value))
            {
                continue;
            }

            map[key] = value;
        }

        return map;
    }

    public static string? Get(string envPath, string key)
    {
        var map = Read(envPath);
        return map.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Upserts keys in-place. Existing assignments are replaced; missing keys are appended
    /// under an Orbit-managed section. Returns true if the file content changed.
    /// </summary>
    public static bool Upsert(string envPath, IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envPath);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(envPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = File.Exists(envPath)
            ? File.ReadAllLines(envPath).ToList()
            : new List<string>
            {
                "# Hermes Agent Environment Configuration",
                "# Managed keys may be updated by Orbit — do not remove API_SERVER_* / ORBIT_*.",
                string.Empty,
            };

        var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryParseAssignment(lines[i], out var key, out _))
            {
                continue;
            }

            if (!pending.TryGetValue(key, out var next))
            {
                continue;
            }

            lines[i] = key + "=" + next;
            seen.Add(key);
            pending.Remove(key);
        }

        if (pending.Count > 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add("# --- Orbit (auto-managed) ---");
            foreach (var pair in pending.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(pair.Key + "=" + pair.Value);
                seen.Add(pair.Key);
            }
        }

        var nextText = Normalize(string.Join("\n", lines) + "\n");
        var previous = File.Exists(envPath) ? Normalize(File.ReadAllText(envPath)) : string.Empty;
        if (string.Equals(previous, nextText, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(envPath, nextText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static bool TryParseAssignment(string raw, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
        {
            return false;
        }

        var idx = line.IndexOf('=');
        key = line[..idx].Trim();
        if (key.Length == 0)
        {
            return false;
        }

        value = line[(idx + 1)..].Trim();
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return true;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
