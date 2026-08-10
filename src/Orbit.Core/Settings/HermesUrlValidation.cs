using System.Net;

namespace Orbit.Core.Settings;

public static class HermesUrlValidation
{
    public static bool TryValidate(string? url, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Hermes base URL is required.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Hermes base URL must be an absolute http or https URL.";
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    public static bool IsLoopbackHost(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsLoopbackHost(uri);
    }

    public static bool IsLoopbackHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return IPAddress.IsLoopback(address);
        }

        return false;
    }

    /// <summary>Non-loopback Hermes URLs should carry an API key.</summary>
    public static bool RequiresApiKey(string? url) =>
        TryValidate(url, out var normalized, out _) && !IsLoopbackHost(normalized);

    /// <summary>True when the URL is remote (non-loopback) plain HTTP.</summary>
    public static bool IsInsecureRemoteHttp(string? url)
    {
        if (!TryValidate(url, out var normalized, out _))
        {
            return false;
        }

        var uri = new Uri(normalized!);
        return !IsLoopbackHost(uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a user-facing warning for remote plain HTTP, or null when none.
    /// </summary>
    public static string? GetRemoteSecurityWarning(string? url)
    {
        if (!IsInsecureRemoteHttp(url))
        {
            return null;
        }

        return "Warning: Hermes base URL is remote plain HTTP. Prefer Tailscale/VPN or HTTPS; require an API key.";
    }

    public static bool TryValidateForSave(
        string? url,
        string? apiKey,
        out string? normalized,
        out string? error,
        out string? warning)
    {
        warning = null;
        if (!TryValidate(url, out normalized, out error))
        {
            return false;
        }

        warning = GetRemoteSecurityWarning(normalized);
        if (RequiresApiKey(normalized) && string.IsNullOrWhiteSpace(apiKey))
        {
            error = "API key is required for non-loopback Hermes URLs.";
            return false;
        }

        return true;
    }
}
