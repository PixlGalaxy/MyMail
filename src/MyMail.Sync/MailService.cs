using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Identity.Client;
using MyMail.Core.Models;
using MyMail.Core.Services;
using MyMail.Storage;
using MyMail.Sync.Auth;
using MyMail.Sync.Graph;
using MyMail.Sync.Imap;

namespace MyMail.Sync;

/// <summary>
/// Real mail service: reads come from the SQLite cache, a background loop per account
/// replays queued changes and syncs, and <see cref="Changed"/> tells the UI what to reload.
/// </summary>
public sealed class MailService(MailStore store, Credentials credentials, HttpClient http) : IMailService, IAccountService, IAsyncDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FolderListInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, IMailBackend> _backends = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new();
    private readonly ConcurrentDictionary<string, Task> _folderSyncs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingLocks = new();
    private readonly CancellationTokenSource _shutdown = new();

    public event EventHandler<MailChange>? Changed;

    // ================================================================ IMailService

    public void StartBackgroundSync() => _ = Task.Run(async () =>
    {
        foreach (var account in await store.GetAccountsAsync().ConfigureAwait(false))
        {
            StartLoop(account.Account.Id);
        }
    });

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default) =>
        (await store.GetAccountsAsync().ConfigureAwait(false)).Select(a => a.Account).ToList();

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(string accountId, CancellationToken ct = default) =>
        (await store.GetFoldersAsync(accountId).ConfigureAwait(false)).Select(f => f.Folder).ToList();

    public async Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(string folderId, CancellationToken ct = default)
    {
        var cached = await store.GetMessagesAsync(folderId).ConfigureAwait(false);
        // Cache first; fresh data arrives later through Changed.
        _ = SyncFolderInBackgroundAsync(folderId);
        return cached;
    }

    public async Task<MailMessageBody> GetBodyAsync(string messageId, CancellationToken ct = default)
    {
        if (await store.GetBodyAsync(messageId).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        var backend = await BackendAsync(MailIds.AccountOf(messageId)).ConfigureAwait(false);
        try
        {
            var body = await backend.FetchBodyAsync(messageId, ct).ConfigureAwait(false);
            await store.SaveBodyAsync(body).ConfigureAwait(false);
            return body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Describe(ex, backend.Account.Account.Email), ex);
        }
    }

    public Task SetReadAsync(string messageId, bool isRead, CancellationToken ct = default) =>
        ChangeAsync(messageId, MailStore.OpSetRead, isRead, () => store.SetMessageFlagsAsync(messageId, isRead: isRead));

    public Task SetFlaggedAsync(string messageId, bool isFlagged, CancellationToken ct = default) =>
        ChangeAsync(messageId, MailStore.OpSetFlagged, isFlagged, () => store.SetMessageFlagsAsync(messageId, isFlagged: isFlagged));

    public Task DeleteAsync(string messageId, CancellationToken ct = default) =>
        ChangeAsync(messageId, MailStore.OpDelete, true, () => store.DeleteMessageAsync(messageId));

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        var backend = await BackendAsync(message.AccountId).ConfigureAwait(false);
        try
        {
            await backend.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Describe(ex, backend.Account.Account.Email), ex);
        }
    }

    public async Task SyncAsync(string accountId, CancellationToken ct = default)
    {
        var backend = await BackendAsync(accountId).ConfigureAwait(false);
        try
        {
            await ApplyPendingAsync(backend, ct).ConfigureAwait(false);
            await backend.SyncFoldersAsync(ct).ConfigureAwait(false);
            Raise(new MailChange(MailChangeKind.Folders, accountId));
            foreach (var folder in await store.GetFoldersAsync(accountId).ConfigureAwait(false))
            {
                if (folder.Folder.Kind == FolderKind.Inbox)
                {
                    await SyncFolderAsync(folder.Folder.Id, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Describe(ex, backend.Account.Account.Email), ex);
        }
    }

    // ================================================================ IAccountService

    public bool IsOAuthConfigured(MailProvider provider) => provider switch
    {
        MailProvider.Outlook => credentials.Options.HasMicrosoft,
        MailProvider.Gmail => credentials.Options.HasGoogle,
        _ => true,
    };

    public Task<ServerSettings?> DiscoverAsync(string email, CancellationToken ct = default) =>
        new ServerDiscovery(http).DiscoverAsync(email, ct);

    public async Task<Account> AddOAuthAccountAsync(MailProvider provider, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        StoredAccount stored;
        IMailBackend backend;
        try
        {
            switch (provider)
            {
                case MailProvider.Outlook:
                {
                    var userName = await credentials.Microsoft.SignInAsync(ct).ConfigureAwait(false);
                    var settings = new ServerSettings("graph.microsoft.com", 443, "graph.microsoft.com", 443, userName, AuthKind.OAuthMicrosoft);
                    stored = new StoredAccount(new Account(id, userName, userName, MailProvider.Outlook), settings, null);
                    var graph = new GraphBackend(stored, store, credentials.Microsoft, http);
                    var (name, email) = await graph.GetProfileAsync(ct).ConfigureAwait(false);
                    stored = stored with { Account = new Account(id, name, email, MailProvider.Outlook) };
                    graph.Account = stored;
                    backend = graph;
                    break;
                }

                case MailProvider.Gmail:
                {
                    var (email, name) = await credentials.Google.SignInAsync(id, ct).ConfigureAwait(false);
                    var settings = new ServerSettings("imap.gmail.com", 993, "smtp.gmail.com", 465, email, AuthKind.OAuthGoogle);
                    stored = new StoredAccount(new Account(id, name ?? email, email, MailProvider.Gmail), settings, null);
                    backend = new ImapBackend(stored, store, credentials);
                    break;
                }

                default:
                    throw new NotSupportedException($"{provider} no usa OAuth.");
            }

            await backend.VerifyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotSupportedException)
        {
            throw new InvalidOperationException(Describe(ex, provider.ToString()), ex);
        }

        return await RegisterAsync(stored, backend).ConfigureAwait(false);
    }

    public async Task<Account> AddPasswordAccountAsync(
        string displayName,
        string email,
        string password,
        ServerSettings settings,
        MailProvider provider,
        CancellationToken ct = default)
    {
        if (ServerDiscovery.KnownSettings(email)?.Auth is AuthKind.OAuthMicrosoft or AuthKind.OAuthGoogle)
        {
            throw new InvalidOperationException("Esta dirección inicia sesión con su proveedor: usa el botón de Outlook o de Gmail.");
        }

        var id = Guid.NewGuid().ToString("N");
        var stored = new StoredAccount(
            new Account(id, string.IsNullOrWhiteSpace(displayName) ? email : displayName, email, provider),
            settings with { Auth = AuthKind.Password },
            SecretProtector.ProtectString(password));
        var backend = new ImapBackend(stored, store, credentials);
        try
        {
            await backend.VerifyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await backend.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(Describe(ex, email), ex);
        }

        return await RegisterAsync(stored, backend).ConfigureAwait(false);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken ct = default)
    {
        if (_loops.TryRemove(accountId, out var loop))
        {
            await loop.CancelAsync().ConfigureAwait(false);
        }

        if (_backends.TryRemove(accountId, out var backend))
        {
            await backend.DisposeAsync().ConfigureAwait(false);
            try
            {
                switch (backend.Account.Settings.Auth)
                {
                    case AuthKind.OAuthMicrosoft:
                        await credentials.Microsoft.SignOutAsync(backend.Account.Settings.UserName).ConfigureAwait(false);
                        break;
                    case AuthKind.OAuthGoogle:
                        await credentials.Google.SignOutAsync(accountId).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception)
            {
                // Removing local tokens is best effort; the account row goes away regardless.
            }
        }

        await store.DeleteAccountAsync(accountId).ConfigureAwait(false);
        Raise(new MailChange(MailChangeKind.Accounts, accountId));
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var backend in _backends.Values)
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ================================================================ internals

    private async Task<Account> RegisterAsync(StoredAccount stored, IMailBackend backend)
    {
        await store.SaveAccountAsync(stored).ConfigureAwait(false);
        _backends[stored.Account.Id] = backend;
        Raise(new MailChange(MailChangeKind.Accounts, stored.Account.Id));
        StartLoop(stored.Account.Id);
        return stored.Account;
    }

    private async Task<IMailBackend> BackendAsync(string accountId)
    {
        if (_backends.TryGetValue(accountId, out var existing))
        {
            return existing;
        }

        var stored = (await store.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault(a => a.Account.Id == accountId)
            ?? throw new InvalidOperationException("La cuenta ya no existe.");
        IMailBackend created = stored.Settings.Auth == AuthKind.OAuthMicrosoft
            ? new GraphBackend(stored, store, credentials.Microsoft, http)
            : new ImapBackend(stored, store, credentials);
        if (_backends.TryAdd(accountId, created))
        {
            return created;
        }

        await created.DisposeAsync().ConfigureAwait(false);
        return _backends[accountId];
    }

    private void StartLoop(string accountId)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (_loops.TryAdd(accountId, cts))
        {
            _ = LoopAsync(accountId, cts.Token);
        }
        else
        {
            cts.Dispose();
        }
    }

    /// <summary>Per-account loop: push local changes, refresh folders now and then, refresh the inbox.</summary>
    private async Task LoopAsync(string accountId, CancellationToken ct)
    {
        var foldersSyncedAt = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            string email = accountId;
            try
            {
                var backend = await BackendAsync(accountId).ConfigureAwait(false);
                email = backend.Account.Account.Email;
                await ApplyPendingAsync(backend, ct).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow - foldersSyncedAt > FolderListInterval)
                {
                    await backend.SyncFoldersAsync(ct).ConfigureAwait(false);
                    foldersSyncedAt = DateTimeOffset.UtcNow;
                    Raise(new MailChange(MailChangeKind.Folders, accountId));
                }

                var inbox = (await store.GetFoldersAsync(accountId).ConfigureAwait(false))
                    .FirstOrDefault(f => f.Folder.Kind == FolderKind.Inbox);
                if (inbox is not null)
                {
                    await SyncFolderAsync(inbox.Folder.Id, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Raise(new MailChange(MailChangeKind.Error, accountId, Message: Describe(ex, email)));
            }

            try
            {
                await Task.Delay(SyncInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Syncs one folder; concurrent requests for the same folder share a single run.</summary>
    private Task SyncFolderAsync(string folderId, CancellationToken ct)
    {
        return _folderSyncs.GetOrAdd(folderId, _ => RunAsync());

        async Task RunAsync()
        {
            try
            {
                var accountId = MailIds.AccountOf(folderId);
                var backend = await BackendAsync(accountId).ConfigureAwait(false);
                if (await backend.SyncFolderAsync(folderId, ct).ConfigureAwait(false))
                {
                    Raise(new MailChange(MailChangeKind.Messages, accountId, folderId));
                    Raise(new MailChange(MailChangeKind.Folders, accountId));
                }
            }
            finally
            {
                _folderSyncs.TryRemove(folderId, out _);
            }
        }
    }

    private async Task SyncFolderInBackgroundAsync(string folderId)
    {
        try
        {
            await Task.Yield();
            await SyncFolderAsync(folderId, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var accountId = MailIds.AccountOf(folderId);
            Raise(new MailChange(MailChangeKind.Error, accountId, folderId, Describe(ex, accountId)));
        }
    }

    /// <summary>Optimistic change: update the cache now, queue it for the server, push it in the background.</summary>
    private async Task ChangeAsync(string messageId, string kind, bool value, Func<Task> applyLocally)
    {
        var accountId = MailIds.AccountOf(messageId);
        await applyLocally().ConfigureAwait(false);
        await store.EnqueueAsync(accountId, messageId, kind, value).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            try
            {
                var backend = await BackendAsync(accountId).ConfigureAwait(false);
                await ApplyPendingAsync(backend, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Stays queued; the account loop retries it later.
                Raise(new MailChange(MailChangeKind.Error, accountId, Message: Describe(ex, accountId)));
            }
        });
    }

    private async Task ApplyPendingAsync(IMailBackend backend, CancellationToken ct)
    {
        var gate = _pendingLocks.GetOrAdd(backend.Account.Account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await backend.ApplyPendingAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Raise(MailChange change) => Changed?.Invoke(this, change);

    /// <summary>Turns protocol/auth exceptions into messages a person can act on.</summary>
    public static string Describe(Exception ex, string who) => ex switch
    {
        ReauthenticationRequiredException => ex.Message,
        MsalServiceException ms when ms.Message.Contains("AADSTS65001") || ms.Message.Contains("AADSTS90094") || ms.Message.Contains("AADSTS90008") =>
            $"Tu organización requiere que un administrador apruebe MyMail antes de usarla ({ms.ErrorCode}). " +
            "El administrador del tenant debe dar consentimiento a la app en Microsoft Entra.",
        MsalServiceException ms when ms.Message.Contains("AADSTS53003") || ms.Message.Contains("AADSTS53000") || ms.Message.Contains("AADSTS53001") =>
            "Una directiva de acceso condicional de tu organización bloqueó el inicio de sesión desde este dispositivo o app. " +
            $"El administrador puede revisar los registros de inicio de sesión de Entra ({ms.ErrorCode}).",
        MsalServiceException ms when ms.Message.Contains("AADSTS700016") || ms.Message.Contains("AADSTS50011") =>
            $"El registro de la app de MyMail en Entra no está bien configurado ({ms.ErrorCode}). Revisa docs/referencia/05.",
        MsalException ms => $"No se pudo iniciar sesión en Microsoft: {ms.Message}",
        MailKit.Security.AuthenticationException => $"No se pudo iniciar sesión en {who}. Revisa el usuario y la contraseña (algunos proveedores exigen una contraseña de aplicación).",
        GraphException { Status: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } g =>
            $"Microsoft rechazó el acceso al correo de {who}: {g.Message}",
        GraphException g => $"Error de Microsoft Graph: {g.Message}",
        HttpRequestException or SocketException or IOException => $"Sin conexión con el servidor de {who}.",
        _ => $"Error con {who}: {ex.Message}",
    };
}
