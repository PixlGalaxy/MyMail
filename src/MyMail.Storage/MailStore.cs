using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMail.Core.Models;

namespace MyMail.Storage;

/// <summary>A stored account: what the UI sees plus how to connect.</summary>
public sealed record StoredAccount(Account Account, ServerSettings Settings, byte[]? ProtectedSecret);

/// <summary>
/// A cached message row. <paramref name="RemoteId"/> is the server id
/// (IMAP UID or Graph message id).
/// </summary>
public sealed record StoredMessage(MailMessageSummary Summary, string RemoteId);

/// <summary>
/// A folder. <paramref name="RemoteId"/> is the IMAP full name or Graph folder id;
/// <paramref name="SyncState"/> is backend-specific (IMAP UIDVALIDITY, Graph delta link).
/// </summary>
public sealed record StoredFolder(MailFolder Folder, string RemoteId, string? SyncState = null);

public sealed record PendingOperation(long Id, string AccountId, string MessageId, string Kind, bool Value);

/// <summary>
/// Local SQLite cache. Microsoft.Data.Sqlite's async API completes synchronously,
/// so every public method hops to the thread pool to keep the UI thread free.
/// </summary>
public sealed class MailStore
{
    public const string OpSetRead = "read";
    public const string OpSetFlagged = "flag";
    public const string OpDelete = "delete";

    private readonly string _connectionString;

    public MailStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
        }.ToString();
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMail");

    public Task InitializeAsync() => Task.Run(() =>
    {
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                email TEXT NOT NULL,
                provider INTEGER NOT NULL,
                settings_json TEXT NOT NULL,
                secret BLOB,
                sort INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS folders (
                id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                remote_id TEXT NOT NULL,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                sync_state TEXT,
                sort INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                folder_id TEXT NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                remote_id TEXT NOT NULL,
                from_name TEXT NOT NULL,
                from_email TEXT NOT NULL,
                subject TEXT NOT NULL,
                preview TEXT NOT NULL,
                received INTEGER NOT NULL,
                is_read INTEGER NOT NULL,
                is_flagged INTEGER NOT NULL,
                has_attachments INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_messages_folder ON messages(folder_id, received DESC);
            CREATE TABLE IF NOT EXISTS bodies (
                message_id TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
                text TEXT NOT NULL,
                html TEXT,
                to_json TEXT NOT NULL,
                cc_json TEXT NOT NULL,
                message_id_header TEXT);
            CREATE TABLE IF NOT EXISTS pending_ops (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id TEXT NOT NULL,
                message_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                value INTEGER NOT NULL);
            """);
    });

    // ---------------------------------------------------------------- accounts

    public Task<IReadOnlyList<StoredAccount>> GetAccountsAsync() => Task.Run<IReadOnlyList<StoredAccount>>(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT id, display_name, email, provider, settings_json, secret FROM accounts ORDER BY sort, rowid");
        using var r = cmd.ExecuteReader();
        var list = new List<StoredAccount>();
        while (r.Read())
        {
            var account = new Account(r.GetString(0), r.GetString(1), r.GetString(2), (MailProvider)r.GetInt32(3));
            var settings = JsonSerializer.Deserialize<ServerSettings>(r.GetString(4))!;
            var secret = r.IsDBNull(5) ? null : (byte[])r[5];
            list.Add(new StoredAccount(account, settings, secret));
        }

        return list;
    });

    public Task SaveAccountAsync(StoredAccount stored) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, """
            INSERT INTO accounts (id, display_name, email, provider, settings_json, secret, sort)
            VALUES ($id, $name, $email, $provider, $settings, $secret, (SELECT COUNT(*) FROM accounts))
            ON CONFLICT(id) DO UPDATE SET display_name = $name, email = $email, provider = $provider,
                settings_json = $settings, secret = $secret
            """);
        cmd.Parameters.AddWithValue("$id", stored.Account.Id);
        cmd.Parameters.AddWithValue("$name", stored.Account.DisplayName);
        cmd.Parameters.AddWithValue("$email", stored.Account.Email);
        cmd.Parameters.AddWithValue("$provider", (int)stored.Account.Provider);
        cmd.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(stored.Settings));
        cmd.Parameters.AddWithValue("$secret", (object?)stored.ProtectedSecret ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    public Task DeleteAccountAsync(string accountId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "DELETE FROM pending_ops WHERE account_id = $id; DELETE FROM accounts WHERE id = $id;");
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.ExecuteNonQuery();
    });

    // ---------------------------------------------------------------- folders

    public Task<IReadOnlyList<StoredFolder>> GetFoldersAsync(string accountId) => Task.Run<IReadOnlyList<StoredFolder>>(() =>
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT f.id, f.remote_id, f.name, f.kind, f.sync_state,
                   (SELECT COUNT(*) FROM messages m WHERE m.folder_id = f.id AND m.is_read = 0)
            FROM folders f WHERE f.account_id = $account ORDER BY f.sort, f.name
            """);
        cmd.Parameters.AddWithValue("$account", accountId);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredFolder>();
        while (r.Read())
        {
            var folder = new MailFolder(r.GetString(0), accountId, r.GetString(2), (FolderKind)r.GetInt32(3), r.GetInt32(5));
            list.Add(new StoredFolder(folder, r.GetString(1), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        return list;
    });

    /// <summary>Replaces the folder list of an account, keeping cached messages and sync state of folders that still exist.</summary>
    public Task ReplaceFoldersAsync(string accountId, IReadOnlyList<StoredFolder> folders) => Task.Run(() =>
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var keep = folders.Select(f => f.Folder.Id).ToHashSet();
        foreach (var existing in ReadIds(db, tx, "SELECT id FROM folders WHERE account_id = $p", accountId))
        {
            if (!keep.Contains(existing))
            {
                using var del = Command(db, "DELETE FROM folders WHERE id = $id", tx);
                del.Parameters.AddWithValue("$id", existing);
                del.ExecuteNonQuery();
            }
        }

        var sort = 0;
        foreach (var f in folders)
        {
            using var cmd = Command(db, """
                INSERT INTO folders (id, account_id, remote_id, name, kind, sort) VALUES ($id, $account, $remote, $name, $kind, $sort)
                ON CONFLICT(id) DO UPDATE SET remote_id = $remote, name = $name, kind = $kind, sort = $sort
                """, tx);
            cmd.Parameters.AddWithValue("$id", f.Folder.Id);
            cmd.Parameters.AddWithValue("$account", accountId);
            cmd.Parameters.AddWithValue("$remote", f.RemoteId);
            cmd.Parameters.AddWithValue("$name", f.Folder.Name);
            cmd.Parameters.AddWithValue("$kind", (int)f.Folder.Kind);
            cmd.Parameters.AddWithValue("$sort", sort++);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    });

    public Task<string?> GetSyncStateAsync(string folderId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT sync_state FROM folders WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", folderId);
        return cmd.ExecuteScalar() as string;
    });

    public Task SetSyncStateAsync(string folderId, string? state) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE folders SET sync_state = $s WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$s", (object?)state ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    /// <summary>Drops every cached message of a folder (e.g. IMAP UIDVALIDITY changed).</summary>
    public Task ClearFolderAsync(string folderId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "DELETE FROM messages WHERE folder_id = $id");
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.ExecuteNonQuery();
    });

    // ---------------------------------------------------------------- messages

    public Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(string folderId, int limit = 500) =>
        Task.Run<IReadOnlyList<MailMessageSummary>>(() =>
        {
            using var db = Open();
            using var cmd = Command(db, """
                SELECT id, folder_id, from_name, from_email, subject, preview, received, is_read, is_flagged, has_attachments
                FROM messages WHERE folder_id = $folder ORDER BY received DESC LIMIT $limit
                """);
            cmd.Parameters.AddWithValue("$folder", folderId);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<MailMessageSummary>();
            while (r.Read())
            {
                list.Add(ReadSummary(r));
            }

            return list;
        });

    public Task<MailMessageSummary?> GetMessageAsync(string messageId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT id, folder_id, from_name, from_email, subject, preview, received, is_read, is_flagged, has_attachments
            FROM messages WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$id", messageId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSummary(r) : null;
    });

    /// <summary>Cached remote ids of a folder with their flags, used for incremental sync.</summary>
    public Task<IReadOnlyDictionary<string, (bool IsRead, bool IsFlagged)>> GetRemoteStatesAsync(string folderId) =>
        Task.Run<IReadOnlyDictionary<string, (bool, bool)>>(() =>
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT remote_id, is_read, is_flagged FROM messages WHERE folder_id = $folder");
            cmd.Parameters.AddWithValue("$folder", folderId);
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, (bool, bool)>();
            while (r.Read())
            {
                map[r.GetString(0)] = (r.GetInt64(1) != 0, r.GetInt64(2) != 0);
            }

            return map;
        });

    public Task UpsertMessagesAsync(IReadOnlyList<StoredMessage> messages) => Task.Run(() =>
    {
        if (messages.Count == 0)
        {
            return;
        }

        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var m in messages)
        {
            var s = m.Summary;
            using var cmd = Command(db, """
                INSERT INTO messages (id, folder_id, remote_id, from_name, from_email, subject, preview, received, is_read, is_flagged, has_attachments)
                VALUES ($id, $folder, $remote, $fromName, $fromEmail, $subject, $preview, $received, $read, $flagged, $attach)
                ON CONFLICT(id) DO UPDATE SET is_read = $read, is_flagged = $flagged, subject = $subject,
                    preview = CASE WHEN $preview = '' THEN preview ELSE $preview END
                WHERE messages.id NOT IN (SELECT message_id FROM pending_ops)
                """, tx);
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$folder", s.FolderId);
            cmd.Parameters.AddWithValue("$remote", m.RemoteId);
            cmd.Parameters.AddWithValue("$fromName", s.From.Name);
            cmd.Parameters.AddWithValue("$fromEmail", s.From.Email);
            cmd.Parameters.AddWithValue("$subject", s.Subject);
            cmd.Parameters.AddWithValue("$preview", s.Preview);
            cmd.Parameters.AddWithValue("$received", s.Received.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$read", s.IsRead);
            cmd.Parameters.AddWithValue("$flagged", s.IsFlagged);
            cmd.Parameters.AddWithValue("$attach", s.HasAttachments);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    });

    /// <summary>Updates flags of existing rows and removes rows that are gone from the server (both by remote id).</summary>
    public Task ApplyServerStateAsync(
        string folderId,
        IReadOnlyDictionary<string, (bool IsRead, bool IsFlagged)> flags,
        IReadOnlyCollection<string> removed) => Task.Run(() =>
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var (remoteId, (isRead, isFlagged)) in flags)
        {
            // Rows with queued local changes keep their local state until the change reaches the server.
            using var cmd = Command(db, """
                UPDATE messages SET is_read = $r, is_flagged = $f
                WHERE folder_id = $folder AND remote_id = $remote AND id NOT IN (SELECT message_id FROM pending_ops)
                """, tx);
            cmd.Parameters.AddWithValue("$r", isRead);
            cmd.Parameters.AddWithValue("$f", isFlagged);
            cmd.Parameters.AddWithValue("$folder", folderId);
            cmd.Parameters.AddWithValue("$remote", remoteId);
            cmd.ExecuteNonQuery();
        }

        foreach (var remoteId in removed)
        {
            using var cmd = Command(db, "DELETE FROM messages WHERE folder_id = $folder AND remote_id = $remote", tx);
            cmd.Parameters.AddWithValue("$folder", folderId);
            cmd.Parameters.AddWithValue("$remote", remoteId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    });

    public Task SetMessageFlagsAsync(string messageId, bool? isRead = null, bool? isFlagged = null) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, """
            UPDATE messages SET is_read = COALESCE($r, is_read), is_flagged = COALESCE($f, is_flagged) WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$r", (object?)isRead ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)isFlagged ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    public Task DeleteMessageAsync(string messageId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "DELETE FROM messages WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    });

    // ---------------------------------------------------------------- bodies

    public Task<MailMessageBody?> GetBodyAsync(string messageId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT text, html, to_json, cc_json, message_id_header FROM bodies WHERE message_id = $id");
        cmd.Parameters.AddWithValue("$id", messageId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return new MailMessageBody(
            messageId,
            JsonSerializer.Deserialize<List<MailAddress>>(r.GetString(2)) ?? [],
            JsonSerializer.Deserialize<List<MailAddress>>(r.GetString(3)) ?? [],
            r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(4) ? null : r.GetString(4));
    });

    public Task SaveBodyAsync(MailMessageBody body) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, """
            INSERT OR REPLACE INTO bodies (message_id, text, html, to_json, cc_json, message_id_header)
            SELECT $id, $text, $html, $to, $cc, $mid WHERE EXISTS (SELECT 1 FROM messages WHERE id = $id)
            """);
        cmd.Parameters.AddWithValue("$id", body.MessageId);
        cmd.Parameters.AddWithValue("$text", body.Text);
        cmd.Parameters.AddWithValue("$html", (object?)body.Html ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", JsonSerializer.Serialize(body.To));
        cmd.Parameters.AddWithValue("$cc", JsonSerializer.Serialize(body.Cc));
        cmd.Parameters.AddWithValue("$mid", (object?)body.MessageIdHeader ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    });

    // ---------------------------------------------------------------- outbox of changes

    public Task EnqueueAsync(string accountId, string messageId, string kind, bool value) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "INSERT INTO pending_ops (account_id, message_id, kind, value) VALUES ($a, $m, $k, $v)");
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$m", messageId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    });

    public Task<IReadOnlyList<PendingOperation>> GetPendingAsync(string accountId) => Task.Run<IReadOnlyList<PendingOperation>>(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT id, message_id, kind, value FROM pending_ops WHERE account_id = $a ORDER BY id");
        cmd.Parameters.AddWithValue("$a", accountId);
        using var r = cmd.ExecuteReader();
        var list = new List<PendingOperation>();
        while (r.Read())
        {
            list.Add(new PendingOperation(r.GetInt64(0), accountId, r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));
        }

        return list;
    });

    public Task CompleteAsync(long operationId) => Task.Run(() =>
    {
        using var db = Open();
        using var cmd = Command(db, "DELETE FROM pending_ops WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", operationId);
        cmd.ExecuteNonQuery();
    });

    // ---------------------------------------------------------------- helpers

    private static MailMessageSummary ReadSummary(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        new MailAddress(r.GetString(2), r.GetString(3)),
        r.GetString(4),
        r.GetString(5),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
        r.GetInt64(7) != 0,
        r.GetInt64(8) != 0,
        r.GetInt64(9) != 0);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        Execute(db, "PRAGMA foreign_keys = ON;");
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = Command(db, sql);
        cmd.ExecuteNonQuery();
    }

    private static List<string> ReadIds(SqliteConnection db, SqliteTransaction tx, string sql, string parameter)
    {
        using var cmd = Command(db, sql, tx);
        cmd.Parameters.AddWithValue("$p", parameter);
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read())
        {
            ids.Add(r.GetString(0));
        }

        return ids;
    }
}
