using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace MyMail.Sync.Auth;

/// <summary>
/// Outlook.com / Microsoft 365 sign-in for Microsoft Graph.
/// Prefers WAM (the Windows account broker, same as Windows' own apps): it signs in with the
/// account already on the PC, supports MFA and passes device-based Conditional Access.
/// Falls back to the system browser when the broker is unavailable or its redirect URI is not registered.
/// </summary>
public sealed class MicrosoftAuth
{
    public static readonly string[] Scopes =
    [
        "https://graph.microsoft.com/Mail.ReadWrite",
        "https://graph.microsoft.com/Mail.Send",
        "https://graph.microsoft.com/User.Read",
    ];

    private readonly IPublicClientApplication _broker;
    private readonly IPublicClientApplication _browser;
    private readonly Func<IntPtr> _parentWindow;
    private readonly Task _cacheReady;

    public MicrosoftAuth(string clientId, string cacheDirectory, Func<IntPtr> parentWindow)
    {
        _parentWindow = parentWindow;
        _broker = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "MyMail" })
            .WithParentActivityOrWindow(parentWindow)
            .Build();
        _browser = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();
        _cacheReady = RegisterCachesAsync(cacheDirectory);
    }

    /// <summary>Interactive sign-in; returns the account's user name (its address).</summary>
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        await _cacheReady.ConfigureAwait(false);
        try
        {
            var result = await _broker.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithParentActivityOrWindow(_parentWindow())
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            return result.Account.Username;
        }
        catch (MsalClientException ex) when (ex.ErrorCode != MsalError.AuthenticationCanceledError)
        {
            // Broker not available on this Windows, or the app registration lacks the broker redirect URI.
            var result = await _browser.AcquireTokenInteractive(Scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            return result.Account.Username;
        }
    }

    /// <summary>Silent token for an existing account; throws <see cref="ReauthenticationRequiredException"/> when the user must sign in again.</summary>
    public async Task<string> GetAccessTokenAsync(string userName, CancellationToken ct)
    {
        await _cacheReady.ConfigureAwait(false);
        foreach (var app in new[] { _broker, _browser })
        {
            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault(a => string.Equals(a.Username, userName, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                continue;
            }

            try
            {
                var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                throw new ReauthenticationRequiredException(userName);
            }
        }

        throw new ReauthenticationRequiredException(userName);
    }

    public async Task SignOutAsync(string userName)
    {
        await _cacheReady.ConfigureAwait(false);
        foreach (var app in new[] { _broker, _browser })
        {
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                if (string.Equals(account.Username, userName, StringComparison.OrdinalIgnoreCase))
                {
                    await app.RemoveAsync(account).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task RegisterCachesAsync(string directory)
    {
        // The broker keeps its own tokens in Windows; this cache serves the browser fallback (DPAPI-encrypted).
        var properties = new StorageCreationPropertiesBuilder("msal.cache", directory).Build();
        var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
        helper.RegisterCache(_browser.UserTokenCache);
        helper.RegisterCache(_broker.UserTokenCache);
    }
}

public sealed class ReauthenticationRequiredException(string userName)
    : Exception($"La cuenta {userName} necesita que vuelvas a iniciar sesión.");
