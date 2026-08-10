namespace Orbit.Core.Host;

public sealed class ApiErrorEnvelope
{
    public ApiErrorBody Error { get; init; } = new();
}

public sealed class ApiErrorBody
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;
}

public static class ApiErrorCodes
{
    public const string NotImplemented = "not_implemented";
    public const string PathDenied = "path_denied";
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
    public const string Conflict = "conflict";
    public const string NotFound = "not_found";
}

public static class ApiErrors
{
    public static ApiErrorEnvelope Create(string code, string message, string requestId) =>
        new()
        {
            Error = new ApiErrorBody
            {
                Code = code,
                Message = message,
                RequestId = requestId,
            },
        };
}
