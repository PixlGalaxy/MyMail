using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MyMail.Core.Models;
using MyMail.Storage;
using MailFolder = MyMail.Core.Models.MailFolder;
using MailStore = MyMail.Storage.MailStore;

namespace MyMail.Sync.Imap;

/// <summary>IMAP + SMTP backend (Gmail, Yahoo, iCloud, generic servers).</summary>
public sealed class ImapBackend(StoredAccount account, MailStore store, Credentials credentials) : IMailBackend
{
    /// <summary>How many of the newest messages a folder caches on its first sync.</summary>
    public const int InitialWindow = 200;

    private const MessageSummaryItems HeaderItems =
        MessageSummaryItems.UniqueId
        | MessageSummaryItems.Envelope
        | MessageSummaryItems.Flags
        | MessageSummaryItems.InternalDate
        | MessageSummaryItems.BodyStructure
        | MessageSummaryItems.PreviewText;

    private readonly ImapSession _session = new(account, credentials);

    public StoredAccount Account
    {
        get => _session.Account;
        set => _session.Account = value;
    }

    private string AccountId => Account.Account.Id;

    public async Task VerifyAsync(CancellationToken ct)
    {
        using (var imap = await ImapSession.ConnectAsync(Account, credentials, ct).ConfigureAwait(false))
        {
            await imap.DisconnectAsync(true, ct).ConfigureAwait(false);
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(Account.Settings.SmtpHost, Account.Settings.SmtpPort, SecureSocketOptions.Auto, ct).ConfigureAwait(false);
        await credentials.AuthenticateAsync(smtp, Account, ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- folders

    public Task SyncFoldersAsync(CancellationToken ct) =>
        _session.RunAsync(async (client, token) =>
        {
            var personal = client.PersonalNamespaces.FirstOrDefault() ?? new FolderNamespace('/', "");
            var all = await client.GetFoldersAsync(personal, cancellationToken: token).ConfigureAwait(false);
            if (!all.Any(f => f.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                all = [client.Inbox, .. all];
            }

            var folders = all
                .Where(f => !f.Attributes.HasFlag(FolderAttributes.NoSelect) && !f.Attributes.HasFlag(FolderAttributes.NonExistent))
                .Select(f =>
                {
                    var kind = KindOf(f, client.Inbox);
                    var folder = new MailFolder(MailIds.Folder(AccountId, f.FullName), AccountId, DisplayName(f, kind), kind);
                    return new StoredFolder(folder, f.FullName);
                })
                .GroupBy(f => f.Folder.Id)
                .Select(g => g.First())
                .OrderBy(f => FolderNames.Rank(f.Folder.Kind))
                .ThenBy(f => f.Folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            await store.ReplaceFoldersAsync(AccountId, folders).ConfigureAwait(false);
        }, ct);

    // ---------------------------------------------------------------- headers

    public Task<bool> SyncFolderAsync(string folderId, CancellationToken ct) =>
        _session.RunAsync(async (client, token) =>
        {
            var (_, fullName) = MailIds.ParseFolder(folderId);
            var folder = await client.GetFolderAsync(fullName, token).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, token).ConfigureAwait(false);
            try
            {
                // UIDs are only meaningful within one UIDVALIDITY; if it changed, start over.
                var validity = folder.UidValidity.ToString(CultureInfo.InvariantCulture);
                var changed = false;
                if (await store.GetSyncStateAsync(folderId).ConfigureAwait(false) is { } previous && previous != validity)
                {
                    await store.ClearFolderAsync(folderId).ConfigureAwait(false);
                    changed = true;
                }

                await store.SetSyncStateAsync(folderId, validity).ConfigureAwait(false);
                var cached = (await store.GetRemoteStatesAsync(folderId).ConfigureAwait(false))
                    .ToDictionary(kv => uint.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value);

                if (folder.Count == 0)
                {
                    if (cached.Count > 0)
                    {
                        await store.ApplyServerStateAsync(folderId, new Dictionary<string, (bool, bool)>(), cached.Keys.Select(Uid).ToList())
                            .ConfigureAwait(false);
                        changed = true;
                    }

                    return changed;
                }

                IList<IMessageSummary> fresh;
                if (cached.Count == 0)
                {
                    var start = Math.Max(0, folder.Count - InitialWindow);
                    fresh = await folder.FetchAsync(start, -1, HeaderItems, token).ConfigureAwait(false);
                }
                else
                {
                    var maxUid = cached.Keys.Max();
                    var range = new UniqueIdRange(new UniqueId(maxUid + 1), UniqueId.MaxValue);
                    // "n:*" always returns the last message even when it is older than n, so filter.
                    fresh = (await folder.FetchAsync(range, HeaderItems, token).ConfigureAwait(false))
                        .Where(s => s.UniqueId.Id > maxUid)
                        .ToList();

                    var known = await folder
                        .FetchAsync(new UniqueIdRange(new UniqueId(cached.Keys.Min()), new UniqueId(maxUid)), MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, token)
                        .ConfigureAwait(false);
                    var server = known.ToDictionary(s => s.UniqueId.Id, FlagsOf);
                    var removed = cached.Keys.Where(uid => !server.ContainsKey(uid)).Select(Uid).ToList();
                    var updated = server
                        .Where(kv => cached.TryGetValue(kv.Key, out var local) && local != kv.Value)
                        .ToDictionary(kv => Uid(kv.Key), kv => kv.Value);
                    if (removed.Count > 0 || updated.Count > 0)
                    {
                        await store.ApplyServerStateAsync(folderId, updated, removed).ConfigureAwait(false);
                        changed = true;
                    }
                }

                if (fresh.Count > 0)
                {
                    await store.UpsertMessagesAsync(fresh.Select(s => ToStored(folderId, s)).ToList()).ConfigureAwait(false);
                    changed = true;
                }

                return changed;
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }, ct);

    // ---------------------------------------------------------------- bodies

    public Task<MailMessageBody> FetchBodyAsync(string messageId, CancellationToken ct) =>
        _session.RunAsync(async (client, token) =>
        {
            var (folderId, remoteId) = MailIds.ParseMessage(messageId);
            var (_, fullName) = MailIds.ParseFolder(folderId);
            var folder = await client.GetFolderAsync(fullName, token).ConfigureAwait(false);
            // Read-only uses BODY.PEEK, so opening a message does not mark it read behind the user's back.
            await folder.OpenAsync(FolderAccess.ReadOnly, token).ConfigureAwait(false);
            try
            {
                var message = await folder.GetMessageAsync(ParseUid(remoteId), token).ConfigureAwait(false);
                return await MimeConversion.ToBodyAsync(messageId, message, token).ConfigureAwait(false);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }, ct);

    // ---------------------------------------------------------------- queued changes

    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        var pending = await store.GetPendingAsync(AccountId).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        var trash = (await store.GetFoldersAsync(AccountId).ConfigureAwait(false))
            .FirstOrDefault(f => f.Folder.Kind == FolderKind.Deleted);

        foreach (var op in pending)
        {
            try
            {
                await _session.RunAsync((client, token) => ApplyAsync(client, op, trash, token), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException)
            {
                // The server rejected it for good (e.g. message already gone): drop it instead of retrying forever.
            }

            await store.CompleteAsync(op.Id).ConfigureAwait(false);
        }
    }

    private static async Task ApplyAsync(ImapClient client, PendingOperation op, StoredFolder? trash, CancellationToken ct)
    {
        var (folderId, remoteId) = MailIds.ParseMessage(op.MessageId);
        var (_, fullName) = MailIds.ParseFolder(folderId);
        var folder = await client.GetFolderAsync(fullName, ct).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        try
        {
            var id = ParseUid(remoteId);
            switch (op.Kind)
            {
                case MailStore.OpSetRead:
                    await SetFlagAsync(folder, id, MessageFlags.Seen, op.Value, ct).ConfigureAwait(false);
                    break;
                case MailStore.OpSetFlagged:
                    await SetFlagAsync(folder, id, MessageFlags.Flagged, op.Value, ct).ConfigureAwait(false);
                    break;
                case MailStore.OpDelete when trash is not null && trash.RemoteId != fullName:
                    var target = await client.GetFolderAsync(trash.RemoteId, ct).ConfigureAwait(false);
                    await folder.MoveToAsync(id, target, ct).ConfigureAwait(false);
                    break;
                case MailStore.OpDelete:
                    await SetFlagAsync(folder, id, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
                    if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                    {
                        await folder.ExpungeAsync([id], ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await folder.ExpungeAsync(ct).ConfigureAwait(false);
                    }

                    break;
            }
        }
        finally
        {
            await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task SetFlagAsync(IMailFolder folder, UniqueId id, MessageFlags flag, bool value, CancellationToken ct) =>
        value
            ? folder.AddFlagsAsync(id, flag, true, ct)
            : folder.RemoveFlagsAsync(id, flag, true, ct);

    // ---------------------------------------------------------------- sending

    public async Task SendAsync(OutgoingMessage outgoing, CancellationToken ct)
    {
        var message = MimeConversion.ToMime(Account.Account, outgoing);

        using (var smtp = new SmtpClient())
        {
            await smtp.ConnectAsync(Account.Settings.SmtpHost, Account.Settings.SmtpPort, SecureSocketOptions.Auto, ct).ConfigureAwait(false);
            await credentials.AuthenticateAsync(smtp, Account, ct).ConfigureAwait(false);
            await smtp.SendAsync(message, ct).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
        }

        // Gmail files sent mail itself; other IMAP servers need an explicit copy.
        if (Account.Account.Provider != MailProvider.Gmail)
        {
            var sent = (await store.GetFoldersAsync(AccountId).ConfigureAwait(false))
                .FirstOrDefault(f => f.Folder.Kind == FolderKind.Sent);
            if (sent is not null)
            {
                await _session.RunAsync(async (client, token) =>
                {
                    var folder = await client.GetFolderAsync(sent.RemoteId, token).ConfigureAwait(false);
                    await folder.AppendAsync(message, MessageFlags.Seen, token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    // ---------------------------------------------------------------- mapping

    private static string Uid(uint uid) => uid.ToString(CultureInfo.InvariantCulture);

    private static UniqueId ParseUid(string remoteId) => new(uint.Parse(remoteId, CultureInfo.InvariantCulture));

    private StoredMessage ToStored(string folderId, IMessageSummary s)
    {
        var from = s.Envelope?.From?.Mailboxes.FirstOrDefault();
        var (isRead, isFlagged) = FlagsOf(s);
        var remoteId = Uid(s.UniqueId.Id);
        var summary = new MailMessageSummary(
            MailIds.Message(folderId, remoteId),
            folderId,
            new MailAddress(from?.Name ?? "", from?.Address ?? ""),
            string.IsNullOrWhiteSpace(s.Envelope?.Subject) ? "(Sin asunto)" : s.Envelope!.Subject,
            MimeConversion.CleanPreview(s.PreviewText),
            s.InternalDate ?? s.Envelope?.Date ?? DateTimeOffset.Now,
            isRead,
            isFlagged,
            s.Attachments?.Any() ?? false);
        return new StoredMessage(summary, remoteId);
    }

    private static (bool IsRead, bool IsFlagged) FlagsOf(IMessageSummary s)
    {
        var flags = s.Flags ?? MessageFlags.None;
        return (flags.HasFlag(MessageFlags.Seen), flags.HasFlag(MessageFlags.Flagged));
    }

    private static FolderKind KindOf(IMailFolder f, IMailFolder inbox)
    {
        if (f.FullName.Equals(inbox.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return FolderKind.Inbox;
        }

        var a = f.Attributes;
        if (a.HasFlag(FolderAttributes.Sent)) return FolderKind.Sent;
        if (a.HasFlag(FolderAttributes.Drafts)) return FolderKind.Drafts;
        if (a.HasFlag(FolderAttributes.Trash)) return FolderKind.Deleted;
        if (a.HasFlag(FolderAttributes.Junk)) return FolderKind.Junk;
        if (a.HasFlag(FolderAttributes.Archive)) return FolderKind.Archive;

        // Servers without SPECIAL-USE: fall back to common names.
        return FolderNames.KindFromName(f.Name);
    }

    private static string DisplayName(IMailFolder f, FolderKind kind) =>
        kind == FolderKind.Custom
            ? f.FullName.Replace("[Gmail]" + f.DirectorySeparator, "").Replace(f.DirectorySeparator.ToString(), " / ")
            : FolderNames.For(kind);
}
