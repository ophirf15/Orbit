using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Infrastructure.Diagnostics;

/// <summary>
/// Last-known Hermes health probe result (App writes; Host diagnostics reads).
/// Never stores API keys or full response bodies.
/// </summary>
public sealed class HermesHealthLastKnown
{
    public bool Ok { get; init; }

    public int StatusCode { get; init; }

    public string? Summary { get; init; }

    public string CheckedAtUtc { get; init; } = DateTime.UtcNow.ToString("O");
}

public sealed class HermesHealthStatusStore
{
    public const string RelativePath = "diagnostics/hermes-health-last.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string GetPath(string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        return Path.Combine(localDataRoot, RelativePath);
    }

    public void Write(string localDataRoot, HermesHealthLastKnown status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var path = GetPath(localDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var safe = new HermesHealthLastKnown
        {
            Ok = status.Ok,
            StatusCode = status.StatusCode,
            Summary = Truncate(Sanitize(status.Summary), 240),
            CheckedAtUtc = string.IsNullOrWhiteSpace(status.CheckedAtUtc)
                ? DateTime.UtcNow.ToString("O")
                : status.CheckedAtUtc,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(safe, JsonOptions));
    }

    public HermesHealthLastKnown? Read(string localDataRoot)
    {
        var path = GetPath(localDataRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HermesHealthLastKnown>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Sanitize(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        // Strip anything that looks like a bearer token / key fragment.
        var s = summary;
        if (s.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            || s.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || s.Contains("apikey", StringComparison.OrdinalIgnoreCase))
        {
            return "(redacted summary)";
        }

        return s.Trim();
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}
