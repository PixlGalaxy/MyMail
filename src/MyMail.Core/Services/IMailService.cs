using MyMail.Core.Models;

namespace MyMail.Core.Services;

/// <summary>
/// Entry point for all mail data. Every member is asynchronous and cancellable so
/// the UI thread never waits on network or disk.
/// Reads return the local cache immediately; fresh server data arrives later
/// through <see cref="Changed"/> (raised on a background thread).
/// </summary>
public interface IMailService
{
    event EventHandler<MailChange>? Changed;

    /// <summary>Starts periodic background sync for every account.</summary>
    void StartBackgroundSync();

    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(string accountId, CancellationToken ct = default);

    Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(string folderId, CancellationToken ct = default);

    Task<MailMessageBody> GetBodyAsync(string messageId, CancellationToken ct = default);

    Task SetReadAsync(string messageId, bool isRead, CancellationToken ct = default);

    Task SetFlaggedAsync(string messageId, bool isFlagged, CancellationToken ct = default);

    Task DeleteAsync(string messageId, CancellationToken ct = default);

    Task SendAsync(OutgoingMessage message, CancellationToken ct = default);

    Task SyncAsync(string accountId, CancellationToken ct = default);
}

/// <summary>Adding, verifying and removing accounts.</summary>
public interface IAccountService
{
    /// <summary>True when the OAuth client for the provider is configured (see docs/referencia/05).</summary>
    bool IsOAuthConfigured(MailProvider provider);

    /// <summary>Guesses server settings for an address (known providers, Thunderbird ISPDB, imap.&lt;domain&gt;).</summary>
    Task<ServerSettings?> DiscoverAsync(string email, CancellationToken ct = default);

    /// <summary>Signs in with the provider in the system browser and adds the account.</summary>
    Task<Account> AddOAuthAccountAsync(MailProvider provider, CancellationToken ct = default);

    /// <summary>Verifies IMAP and SMTP login, then saves the account.</summary>
    Task<Account> AddPasswordAccountAsync(
        string displayName,
        string email,
        string password,
        ServerSettings settings,
        MailProvider provider,
        CancellationToken ct = default);

    Task RemoveAccountAsync(string accountId, CancellationToken ct = default);
}
