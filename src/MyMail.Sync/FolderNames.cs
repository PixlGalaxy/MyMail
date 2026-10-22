using MyMail.Core.Models;

namespace MyMail.Sync;

/// <summary>Display names and ordering of well-known folders (same order as the original app).</summary>
public static class FolderNames
{
    public static string For(FolderKind kind) => kind switch
    {
        FolderKind.Inbox => "Bandeja de entrada",
        FolderKind.Drafts => "Borradores",
        FolderKind.Sent => "Elementos enviados",
        FolderKind.Archive => "Archivo",
        FolderKind.Deleted => "Elementos eliminados",
        FolderKind.Junk => "Correo no deseado",
        _ => "",
    };

    public static int Rank(FolderKind kind) => kind switch
    {
        FolderKind.Inbox => 0,
        FolderKind.Drafts => 1,
        FolderKind.Sent => 2,
        FolderKind.Archive => 3,
        FolderKind.Custom => 4,
        FolderKind.Junk => 5,
        _ => 6,
    };

    /// <summary>Guess for servers that do not advertise SPECIAL-USE attributes.</summary>
    public static FolderKind KindFromName(string name) => name.ToLowerInvariant() switch
    {
        "sent" or "sent items" or "sent messages" or "enviados" or "elementos enviados" => FolderKind.Sent,
        "drafts" or "borradores" => FolderKind.Drafts,
        "trash" or "deleted" or "deleted items" or "deleted messages" or "papelera" or "elementos eliminados" => FolderKind.Deleted,
        "junk" or "spam" or "bulk mail" or "junk e-mail" or "junk email" or "correo no deseado" => FolderKind.Junk,
        "archive" or "archivo" => FolderKind.Archive,
        _ => FolderKind.Custom,
    };
}
