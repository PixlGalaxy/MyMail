using MyMail.Core.Models;
using MyMail.Storage;

namespace MyMail.Tests;

public sealed class MailStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mymail-tests-" + Guid.NewGuid().ToString("N"));
    private MailStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new MailStore(Path.Combine(_directory, "test.db"));
        await _store.InitializeAsync();
        var account = new Account("acc", "Ana", "ana@example.com", MailProvider.Imap);
        await _store.SaveAccountAsync(new StoredAccount(account, new ServerSettings("imap", 993, "smtp", 587, "ana", AuthKind.Password), null));
        await _store.ReplaceFoldersAsync("acc", [new StoredFolder(new MailFolder("acc|INBOX", "acc", "Bandeja de entrada", FolderKind.Inbox), "INBOX")]);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Messages_are_listed_newest_first_with_unread_count()
    {
        await _store.UpsertMessagesAsync([Message("1", hoursAgo: 5, isRead: true), Message("2", hoursAgo: 1, isRead: false)]);

        var messages = await _store.GetMessagesAsync("acc|INBOX");
        var folders = await _store.GetFoldersAsync("acc");

        Assert.Equal(["acc|INBOX|2", "acc|INBOX|1"], messages.Select(m => m.Id));
        Assert.Equal(1, folders.Single().Folder.UnreadCount);
    }

    [Fact]
    public async Task Server_state_does_not_overwrite_a_queued_local_change()
    {
        await _store.UpsertMessagesAsync([Message("1", hoursAgo: 1, isRead: false)]);

        // The user marks it read; the change is queued but not yet on the server.
        await _store.SetMessageFlagsAsync("acc|INBOX|1", isRead: true);
        await _store.EnqueueAsync("acc", "acc|INBOX|1", MailStore.OpSetRead, true);

        // A sync that started earlier still reports it unread.
        await _store.ApplyServerStateAsync("acc|INBOX", new Dictionary<string, (bool, bool)> { ["1"] = (false, false) }, []);
        await _store.UpsertMessagesAsync([Message("1", hoursAgo: 1, isRead: false)]);
        Assert.True((await _store.GetMessageAsync("acc|INBOX|1"))!.IsRead);

        // Once the change is applied, server state wins again.
        var op = (await _store.GetPendingAsync("acc")).Single();
        await _store.CompleteAsync(op.Id);
        await _store.ApplyServerStateAsync("acc|INBOX", new Dictionary<string, (bool, bool)> { ["1"] = (false, true) }, []);
        var after = (await _store.GetMessageAsync("acc|INBOX|1"))!;
        Assert.False(after.IsRead);
        Assert.True(after.IsFlagged);
    }

    [Fact]
    public async Task Removed_remote_ids_are_deleted_and_bodies_cascade()
    {
        await _store.UpsertMessagesAsync([Message("1", hoursAgo: 1, isRead: true)]);
        await _store.SaveBodyAsync(new MailMessageBody("acc|INBOX|1", [], [], "hola"));

        await _store.ApplyServerStateAsync("acc|INBOX", new Dictionary<string, (bool, bool)>(), ["1"]);

        Assert.Empty(await _store.GetMessagesAsync("acc|INBOX"));
        Assert.Null(await _store.GetBodyAsync("acc|INBOX|1"));
    }

    [Fact]
    public async Task Replacing_folders_keeps_messages_and_sync_state_of_existing_folders()
    {
        await _store.UpsertMessagesAsync([Message("1", hoursAgo: 1, isRead: true)]);
        await _store.SetSyncStateAsync("acc|INBOX", "12345");

        await _store.ReplaceFoldersAsync("acc",
        [
            new StoredFolder(new MailFolder("acc|INBOX", "acc", "Bandeja de entrada", FolderKind.Inbox), "INBOX"),
            new StoredFolder(new MailFolder("acc|Sent", "acc", "Elementos enviados", FolderKind.Sent), "Sent"),
        ]);

        Assert.Single(await _store.GetMessagesAsync("acc|INBOX"));
        Assert.Equal("12345", await _store.GetSyncStateAsync("acc|INBOX"));
        Assert.Equal(2, (await _store.GetFoldersAsync("acc")).Count);
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_folders_messages_and_queue()
    {
        await _store.UpsertMessagesAsync([Message("1", hoursAgo: 1, isRead: true)]);
        await _store.EnqueueAsync("acc", "acc|INBOX|1", MailStore.OpDelete, true);

        await _store.DeleteAccountAsync("acc");

        Assert.Empty(await _store.GetAccountsAsync());
        Assert.Empty(await _store.GetFoldersAsync("acc"));
        Assert.Empty(await _store.GetMessagesAsync("acc|INBOX"));
        Assert.Empty(await _store.GetPendingAsync("acc"));
    }

    private static StoredMessage Message(string uid, int hoursAgo, bool isRead) => new(
        new MailMessageSummary(
            $"acc|INBOX|{uid}",
            "acc|INBOX",
            new MailAddress("Luis", "luis@example.com"),
            $"Asunto {uid}",
            "vista previa",
            DateTimeOffset.UtcNow.AddHours(-hoursAgo),
            isRead,
            IsFlagged: false,
            HasAttachments: false),
        uid);
}
