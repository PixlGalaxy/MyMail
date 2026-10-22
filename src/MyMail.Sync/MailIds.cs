namespace MyMail.Sync;

/// <summary>
/// Local ids that carry their server coordinates:
/// folder = "{accountId}|{remoteFolderId}", message = "{folderId}|{remoteMessageId}".
/// Remote ids are IMAP full names / UIDs or Graph ids (which never contain '|').
/// Encoding them lets queued operations run even after the local row is gone (e.g. delete).
/// </summary>
public static class MailIds
{
    public static string Folder(string accountId, string remoteFolderId) => $"{accountId}|{remoteFolderId}";

    public static string Message(string folderId, string remoteMessageId) => $"{folderId}|{remoteMessageId}";

    public static (string AccountId, string RemoteFolderId) ParseFolder(string folderId)
    {
        var sep = folderId.IndexOf('|');
        return (folderId[..sep], folderId[(sep + 1)..]);
    }

    public static (string FolderId, string RemoteMessageId) ParseMessage(string messageId)
    {
        var sep = messageId.LastIndexOf('|');
        return (messageId[..sep], messageId[(sep + 1)..]);
    }

    public static string AccountOf(string folderOrMessageId) => folderOrMessageId[..folderOrMessageId.IndexOf('|')];
}
