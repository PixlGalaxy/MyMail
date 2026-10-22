using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MyMail.Storage;

namespace MyMail.Sync.Imap;

/// <summary>
/// One IMAP connection per account. MailKit clients are not thread-safe, so every
/// use goes through <see cref="RunAsync{T}"/>, which serializes access and reconnects as needed.
/// </summary>
public sealed class ImapSession(StoredAccount account, Credentials credentials) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImapClient? _client;

    public StoredAccount Account { get; set; } = account;

    public async Task<T> RunAsync<T>(Func<ImapClient, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            try
            {
                return await work(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or ImapProtocolException)
            {
                // Dropped connection: reconnect once and retry.
                await DisconnectAsync().ConfigureAwait(false);
                client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await work(client, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<ImapClient, CancellationToken, Task> work, CancellationToken ct) =>
        RunAsync<bool>(async (c, t) =>
        {
            await work(c, t).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Connects and authenticates a fresh client (used to verify new accounts too).</summary>
    public static async Task<ImapClient> ConnectAsync(StoredAccount account, Credentials credentials, CancellationToken ct)
    {
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(account.Settings.ImapHost, account.Settings.ImapPort, SecureSocketOptions.Auto, ct)
                .ConfigureAwait(false);
            await credentials.AuthenticateAsync(client, account, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImapClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true })
        {
            return _client;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _client = await ConnectAsync(Account, credentials, ct).ConfigureAwait(false);
        return _client;
    }

    private async Task DisconnectAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(true).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort: the connection may already be gone.
        }

        _client.Dispose();
        _client = null;
    }
}
