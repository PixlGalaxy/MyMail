using MailKit.Security;
using MyMail.Core.Models;
using MyMail.Storage;
using MyMail.Sync.Auth;

namespace MyMail.Sync;

/// <summary>Sign-in for every account kind: password or Google XOAUTH2 for IMAP/SMTP, MSAL tokens for Graph.</summary>
public sealed class Credentials(OAuthOptions options, string dataDirectory, Func<IntPtr> parentWindow)
{
    private MicrosoftAuth? _microsoft;
    private GoogleAuth? _google;

    public OAuthOptions Options => options;

    public MicrosoftAuth Microsoft => _microsoft ??= options.HasMicrosoft
        ? new MicrosoftAuth(options.Microsoft!.ClientId, dataDirectory, parentWindow)
        : throw new InvalidOperationException("Falta configurar el cliente OAuth de Microsoft (oauth.json).");

    public GoogleAuth Google => _google ??= options.HasGoogle
        ? new GoogleAuth(options.Google!.ClientId, options.Google.ClientSecret, Path.Combine(dataDirectory, "google-tokens"))
        : throw new InvalidOperationException("Falta configurar el cliente OAuth de Google (oauth.json).");

    /// <summary>Authenticates an IMAP or SMTP client with the account's password or OAuth token.</summary>
    public async Task AuthenticateAsync(MailKit.IMailService client, StoredAccount account, CancellationToken ct)
    {
        var settings = account.Settings;
        switch (settings.Auth)
        {
            case AuthKind.OAuthMicrosoft:
                throw new NotSupportedException("Las cuentas de Microsoft usan Microsoft Graph, no IMAP/SMTP.");
            case AuthKind.OAuthGoogle:
                var googleToken = await Google.GetAccessTokenAsync(account.Account.Id, ct).ConfigureAwait(false);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.UserName, googleToken), ct).ConfigureAwait(false);
                break;
            default:
                // Let MailKit pick the best mechanism the server offers (PLAIN, LOGIN, ...).
                var password = account.ProtectedSecret is null ? "" : SecretProtector.UnprotectString(account.ProtectedSecret);
                await client.AuthenticateAsync(settings.UserName, password, ct).ConfigureAwait(false);
                break;
        }
    }
}
