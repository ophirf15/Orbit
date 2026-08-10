using System.Diagnostics;
using Orbit.Core.Host;

namespace Orbit.Core.Host.Auth;

public sealed class ApiKeyMiddleware
{
    public const string RequestIdHeader = "X-Request-Id";
    public const string AuditIdHeader = "X-Audit-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly string? _apiKey;
    private readonly bool _requireKey;

    public ApiKeyMiddleware(RequestDelegate next, HostOptions options)
    {
        _next = next;
        _apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey.Trim();
        // Require bearer when a key is configured, or whenever bind is non-loopback (key must exist).
        _requireKey = _apiKey is not null || !PathSafety.IsLoopbackAddress(options.BindAddress);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = Guid.NewGuid().ToString("N");
        }

        var auditId = context.Request.Headers[AuditIdHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(auditId))
        {
            auditId = requestId;
        }

        context.Response.Headers[RequestIdHeader] = requestId;
        context.Response.Headers[AuditIdHeader] = auditId;
        context.Items["RequestId"] = requestId;
        context.Items["AuditCorrelationId"] = auditId;

        if (_requireKey && !HostEndpoints.IsAnonymousPath(context.Request.Path.Value))
        {
            var auth = context.Request.Headers.Authorization.ToString();
            var expected = "Bearer " + _apiKey;
            if (string.IsNullOrWhiteSpace(_apiKey)
                || !string.Equals(auth, expected, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    ApiErrors.Create(ApiErrorCodes.Unauthorized, "Missing or invalid bearer token.", requestId));
                return;
            }
        }

        await _next(context);
    }

    public static string GetRequestId(HttpContext context) =>
        context.Items["RequestId"] as string
        ?? Activity.Current?.Id
        ?? Guid.NewGuid().ToString("N");
}
