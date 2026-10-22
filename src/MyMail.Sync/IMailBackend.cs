using MyMail.Core.Models;
using MyMail.Storage;

namespace MyMail.Sync;

/// <summary>
/// Server protocol for one account, writing through to the local store.
/// IMAP/SMTP for Gmail, Yahoo, iCloud and generic servers; Microsoft Graph for
/// Outlook.com and Microsoft 365 (where IMAP/SMTP AUTH are often disabled by the tenant).
/// </summary>
public interface IMailBackend : IAsyncDisposable
{
    StoredAccount Account { get; set; }

    /// <summary>Signs in and does a cheap round trip, so a new account fails fast with a clear error.</summary>
    Task VerifyAsync(CancellationToken ct);

    Task SyncFoldersAsync(CancellationToken ct);

    /// <summary>Fetches new headers and refreshes flags/deletions of cached ones. Returns true if anything changed.</summary>
    Task<bool> SyncFolderAsync(string folderId, CancellationToken ct);

    Task<MailMessageBody> FetchBodyAsync(string messageId, CancellationToken ct);

    /// <summary>Replays queued read/flag/delete changes on the server, oldest first.</summary>
    Task ApplyPendingAsync(CancellationToken ct);

    Task SendAsync(OutgoingMessage message, CancellationToken ct);
}
