using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Orbit.Infrastructure.Email;

public sealed class MicrosoftGraphAccount
{
    public required string Username { get; init; }

    public required string HomeAccountId { get; init; }
}

/// <summary>
/// Outlook-style Microsoft login via MSAL public client (system browser / broker).
/// Microsoft hosts the credentials UI; Orbit only receives tokens for Mail.Read.
/// </summary>
public sealed class MicrosoftGraphAuthService
{
    public static readonly string[] MailScopes =
    [
        "User.Read",
        "Mail.Read",
        "offline_access",
    ];

    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string _cacheDirectory;
    private readonly string _cacheFileName;
    private IPublicClientApplication? _app;
    private bool _cacheRegistered;

    public MicrosoftGraphAuthService(string clientId, string tenantId, string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        _clientId = clientId.Trim();
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId.Trim();
        _cacheDirectory = Path.Combine(localDataRoot, "msal");
        _cacheFileName = "msal_graph_cache.bin";
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_clientId)
        && !_clientId.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);

    public async Task<MicrosoftGraphAccount?> TryGetCachedAccountAsync(CancellationToken ct = default)
    {
        var app = await GetAppAsync(ct);
        var accounts = await app.GetAccountsAsync();
        var account = accounts.FirstOrDefault();
        if (account is null)
        {
            return null;
        }

        try
        {
            _ = await app.AcquireTokenSilent(MailScopes, account).ExecuteAsync(ct);
            return new MicrosoftGraphAccount
            {
                Username = account.Username ?? account.HomeAccountId.Identifier,
                HomeAccountId = account.HomeAccountId.Identifier,
            };
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    public async Task<MicrosoftGraphAccount> SignInInteractiveAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Set a Microsoft Graph public-client application (client) ID in Settings. "
                + "Register one in Entra ID → App registrations → Mobile and desktop → http://localhost.");
        }

        var app = await GetAppAsync(ct);
        var result = await app.AcquireTokenInteractive(MailScopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(ct);

        return new MicrosoftGraphAccount
        {
            Username = result.Account.Username ?? result.Account.HomeAccountId.Identifier,
            HomeAccountId = result.Account.HomeAccountId.Identifier,
        };
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var app = await GetAppAsync(ct);
        foreach (var account in await app.GetAccountsAsync())
        {
            await app.RemoveAsync(account);
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var app = await GetAppAsync(ct);
        var accounts = await app.GetAccountsAsync();
        var account = accounts.FirstOrDefault()
            ?? throw new InvalidOperationException("Not signed in to Microsoft.");

        try
        {
            var silent = await app.AcquireTokenSilent(MailScopes, account).ExecuteAsync(ct);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            var interactive = await app.AcquireTokenInteractive(MailScopes)
                .WithAccount(account)
                .ExecuteAsync(ct);
            return interactive.AccessToken;
        }
    }

    /// <summary>Lightweight Graph probe — confirms the token works without syncing mail yet.</summary>
    public async Task<string?> TryGetSignedInDisplayAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await GetAccessTokenAsync(ct);
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=displayName,mail,userPrincipalName");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            return root.TryGetProperty("displayName", out var name) ? name.GetString()
                : root.TryGetProperty("mail", out var mail) ? mail.GetString()
                : root.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IPublicClientApplication> GetAppAsync(CancellationToken ct)
    {
        if (_app is not null)
        {
            return _app;
        }

        Directory.CreateDirectory(_cacheDirectory);
        _app = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
            .WithRedirectUri("http://localhost")
            .WithClientName("Orbit")
            .WithClientVersion("1.0.0")
            .Build();

        if (!_cacheRegistered)
        {
            var storage = new StorageCreationPropertiesBuilder(_cacheFileName, _cacheDirectory)
                .Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storage);
            cacheHelper.RegisterCache(_app.UserTokenCache);
            _cacheRegistered = true;
        }

        return _app;
    }
}
