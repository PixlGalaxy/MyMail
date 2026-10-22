using CommunityToolkit.Mvvm.ComponentModel;
using MyMail.Core.Models;

namespace MyMail.Core.ViewModels;

public sealed partial class FolderViewModel : ObservableObject
{
    public FolderViewModel(MailFolder folder)
    {
        Folder = folder;
        _unreadCount = folder.UnreadCount;
    }

    public MailFolder Folder { get; }

    public string Id => Folder.Id;

    public string Name => Folder.Name;

    public FolderKind Kind => Folder.Kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Used by screen readers as the list item name.</summary>
    public override string ToString() => HasUnread ? $"{Name}, {UnreadCount} no leídos" : Name;
}
