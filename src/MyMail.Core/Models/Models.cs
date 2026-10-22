namespace MyMail.Core.Models;

public enum MailProvider
{
    Outlook,
    Gmail,
    Yahoo,
    ICloud,
    Imap,
}

public enum AuthKind
{
    Password,
    OAuthMicrosoft,
    OAuthGoogle,
}

public enum FolderKind
{
    Inbox,
    Drafts,
    Sent,
    Archive,
    Deleted,
    Junk,
    Custom,
}

public sealed record Account(string Id, string DisplayName, string Email, MailProvider Provider)
{
    /// <summary>Label shown in the account list, like the original ("Outlook", "Gmail", ...).</summary>
    public string ProviderLabel => Provider switch
    {
        MailProvider.Outlook => "Outlook",
        MailProvider.Gmail => "Gmail",
        MailProvider.Yahoo => "Yahoo",
        MailProvider.ICloud => "iCloud",
        _ => Email[(Email.IndexOf('@') + 1)..],
    };
}

/// <summary>Where and how to reach an account's mail servers.</summary>
public sealed record ServerSettings(
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort,
    string UserName,
    AuthKind Auth);

public sealed record MailFolder(string Id, string AccountId, string Name, FolderKind Kind, int UnreadCount = 0);

public sealed record MailAddress(string Name, string Email)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Email : Name;

    public string Initials
    {
        get
        {
            var parts = DisplayName.Split([' ', '.', '@'], StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} <{Email}>";
}

/// <summary>Message header as shown in the message list (body is loaded on demand).</summary>
public sealed record MailMessageSummary(
    string Id,
    string FolderId,
    MailAddress From,
    string Subject,
    string Preview,
    DateTimeOffset Received,
    bool IsRead,
    bool IsFlagged,
    bool HasAttachments);

public sealed record MailMessageBody(
    string MessageId,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    string Text,
    string? Html = null,
    string? MessageIdHeader = null);

public sealed record OutgoingMessage(
    string AccountId,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    string Subject,
    string Body,
    string? InReplyTo = null);

public enum MailChangeKind
{
    Accounts,
    Folders,
    Messages,
    Error,
}

public sealed record MailChange(MailChangeKind Kind, string? AccountId = null, string? FolderId = null, string? Message = null);
