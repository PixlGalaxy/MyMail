using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyMail.Core.Models;

namespace MyMail.App;

/// <summary>Lucide codepoints used from code. Keep in sync with Themes/Icons.xaml.</summary>
public static class Icons
{
    public const string Inbox = "";
    public const string Drafts = "";
    public const string Send = "";
    public const string Archive = "";
    public const string Delete = "";
    public const string Junk = "";
    public const string Folder = "";
    public const string Mail = "";
    public const string MailOpen = "";
    public const string Flag = "";

    public static string ForFolder(FolderKind kind) => kind switch
    {
        FolderKind.Inbox => Inbox,
        FolderKind.Drafts => Drafts,
        FolderKind.Sent => Send,
        FolderKind.Archive => Archive,
        FolderKind.Deleted => Delete,
        FolderKind.Junk => Junk,
        _ => Folder,
    };
}

/// <summary>Small helpers for x:Bind function bindings (avoids a converter per case).</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Unread items get the accent bar, like the original message list.</summary>
    public static Visibility UnreadBar(bool isRead) => isRead ? Visibility.Collapsed : Visibility.Visible;

    public static Windows.UI.Text.FontWeight UnreadWeight(bool isRead) =>
        isRead ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;

    public static Brush UnreadForeground(bool isRead) =>
        (Brush)Application.Current.Resources[isRead ? "TextFillColorPrimaryBrush" : "AccentTextFillColorPrimaryBrush"];

    public static string ReadToggleGlyph(bool isRead) => isRead ? Icons.Mail : Icons.MailOpen;

    public static string ReadToggleText(bool isRead) => isRead ? "Marcar como no leído" : "Marcar como leído";

    public static string FlagToggleText(bool isFlagged) => isFlagged ? "Quitar marca" : "Marcar";
}
