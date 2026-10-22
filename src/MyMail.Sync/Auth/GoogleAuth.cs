using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using MyMail.Storage;

namespace MyMail.Sync.Auth;

/// <summary>
/// Gmail sign-in: desktop OAuth with PKCE and a loopback redirect in the system browser.
/// The "https://mail.google.com/" scope covers IMAP and SMTP via XOAUTH2.
/// </summary>
public sealed class GoogleAuth
{
    private static readonly string[] Scopes = ["https://mail.google.com/", "openid", "email", "profile"];

    private readonly ClientSecrets _secrets;
    private readonly IDataStore _store;

    public GoogleAuth(string clientId, string clientSecret, string tokenDirectory)
    {
        _secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        _store = new ProtectedDataStore(tokenDirectory);
    }

    /// <summary>Opens the system browser; tokens are stored under <paramref name="accountId"/>. Returns the address and name.</summary>
    public async Task<(string Email, string? Name)> SignInAsync(string accountId, CancellationToken ct)
    {
        var credential = await AuthorizeAsync(accountId, ct).ConfigureAwait(false);
        var payload = await GoogleJsonWebSignature.ValidateAsync(credential.Token.IdToken).ConfigureAwait(false);
        return (payload.Email, payload.Name);
    }

    public async Task<string> GetAccessTokenAsync(string accountId, CancellationToken ct)
    {
        // With a stored refresh token this never opens the browser.
        var credential = await AuthorizeAsync(accountId, ct).ConfigureAwait(false);
        return await credential.GetAccessTokenForRequestAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    public Task SignOutAsync(string accountId) => _store.DeleteAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>(accountId);

    private Task<UserCredential> AuthorizeAsync(string accountId, CancellationToken ct) =>
        GoogleWebAuthorizationBroker.AuthorizeAsync(_secrets, Scopes, accountId, ct, _store);

    /// <summary>Google token store encrypted with DPAPI (the default FileDataStore writes plain JSON).</summary>
    private sealed class ProtectedDataStore(string directory) : IDataStore
    {
        public Task StoreAsync<T>(string key, T value)
        {
            Directory.CreateDirectory(directory);
            var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
            return File.WriteAllBytesAsync(PathFor(key), SecretProtector.ProtectString(json));
        }

        public Task DeleteAsync<T>(string key)
        {
            File.Delete(PathFor(key));
            return Task.CompletedTask;
        }

        public async Task<T> GetAsync<T>(string key)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return default!;
            }

            var json = SecretProtector.UnprotectString(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            return NewtonsoftJsonSerializer.Instance.Deserialize<T>(json);
        }

        public Task ClearAsync()
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.bin"))
                {
                    File.Delete(file);
                }
            }

            return Task.CompletedTask;
        }

        // Keys are the user ids we pass (account ids); hash them into safe file names.
        private string PathFor(string key)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
            return Path.Combine(directory, hash[..32] + ".bin");
        }
    }
}
