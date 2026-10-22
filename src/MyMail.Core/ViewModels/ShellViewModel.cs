using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyMail.Core.Models;
using MyMail.Core.Services;

namespace MyMail.Core.ViewModels;

/// <summary>
/// State of the main window: accounts → folders → message list → reading pane.
/// Each selection change starts an async load and cancels the previous one, so
/// fast clicking never queues work or shows stale data. Background sync results
/// arrive through <see cref="IMailService.Changed"/> and are merged in place.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IMailService _mail;
    private readonly IAccountService? _accountService;
    private readonly SynchronizationContext? _ui;
    private CancellationTokenSource? _foldersCts;
    private CancellationTokenSource? _messagesCts;
    private CancellationTokenSource? _bodyCts;

    public ShellViewModel(IMailService mail, IAccountService? accountService = null)
    {
        _mail = mail;
        _accountService = accountService;
        _ui = SynchronizationContext.Current;
        _mail.Changed += OnMailChanged;
    }

    public IAccountService? AccountService => _accountService;

    public ObservableCollection<Account> Accounts { get; } = [];

    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private Account? _selectedAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private FolderViewModel? _selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMessage))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyReadingPane))]
    [NotifyPropertyChangedFor(nameof(ShowReadingPane))]
    private MessageItemViewModel? _selectedMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRecipients))]
    private MailMessageBody? _selectedBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposing))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyReadingPane))]
    [NotifyPropertyChangedFor(nameof(ShowReadingPane))]
    private ComposeViewModel? _compose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private bool _accountsLoaded;

    [ObservableProperty]
    private bool _isLoadingMessages;

    [ObservableProperty]
    private bool _isLoadingBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>"Folder - Account", like the original title bar.</summary>
    public string WindowTitle =>
        SelectedFolder is null || SelectedAccount is null
            ? "MyMail"
            : $"{SelectedFolder.Name} - {SelectedAccount.ProviderLabel}";

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>First run: no accounts yet, show the "add account" welcome.</summary>
    public bool ShowWelcome => AccountsLoaded && Accounts.Count == 0;

    public bool HasSelectedMessage => SelectedMessage is not null;

    public bool IsComposing => Compose is not null;

    public bool ShowReadingPane => HasSelectedMessage && !IsComposing;

    public bool ShowEmptyReadingPane => !HasSelectedMessage && !IsComposing;

    public bool HasError => ErrorMessage is not null;

    public string? SelectedRecipients =>
        SelectedBody is null || SelectedBody.To.Count == 0 ? null : string.Join("; ", SelectedBody.To.Select(a => a.DisplayName));

    // ================================================================ commands

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await ReloadAccountsAsync();
        _mail.StartBackgroundSync();
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        try
        {
            await _mail.SyncAsync(SelectedAccount.Id, ct);
            if (SelectedFolder is not null)
            {
                await LoadMessagesAsync(SelectedFolder);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAccountAsync()
    {
        if (_accountService is null || SelectedAccount is null)
        {
            return;
        }

        try
        {
            await _accountService.RemoveAccountAsync(SelectedAccount.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudo quitar la cuenta: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ToggleReadAsync(MessageItemViewModel? message)
    {
        message ??= SelectedMessage;
        return message is null ? Task.CompletedTask : SetReadAsync(message, !message.IsRead);
    }

    [RelayCommand]
    private async Task ToggleFlagAsync(MessageItemViewModel? message)
    {
        message ??= SelectedMessage;
        if (message is null)
        {
            return;
        }

        var newValue = !message.IsFlagged;
        message.IsFlagged = newValue;
        try
        {
            await _mail.SetFlaggedAsync(message.Id, newValue);
        }
        catch (Exception ex)
        {
            message.IsFlagged = !newValue;
            ErrorMessage = $"No se pudo marcar el mensaje: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(MessageItemViewModel? message)
    {
        message ??= SelectedMessage;
        if (message is null)
        {
            return;
        }

        var index = Messages.IndexOf(message);
        Messages.Remove(message);
        if (SelectedMessage == message || SelectedMessage is null)
        {
            SelectedMessage = Messages.ElementAtOrDefault(Math.Min(index, Messages.Count - 1));
        }

        if (!message.IsRead)
        {
            AdjustUnread(-1);
        }

        try
        {
            await _mail.DeleteAsync(message.Id);
        }
        catch (Exception ex)
        {
            Messages.Insert(Math.Min(index, Messages.Count), message);
            if (!message.IsRead)
            {
                AdjustUnread(+1);
            }

            ErrorMessage = $"No se pudo eliminar el mensaje: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NewMessage()
    {
        if (SelectedAccount is not null)
        {
            Compose = CreateCompose();
        }
    }

    [RelayCommand]
    private void Reply() => StartReply(replyAll: false);

    [RelayCommand]
    private void ReplyAll() => StartReply(replyAll: true);

    [RelayCommand]
    private void Forward()
    {
        if (SelectedAccount is null || SelectedMessage is null)
        {
            return;
        }

        var compose = CreateCompose();
        compose.Subject = Prefixed("RV: ", SelectedMessage.Subject);
        compose.Body = QuotedOriginal();
        Compose = compose;
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // ================================================================ selection

    partial void OnSelectedAccountChanged(Account? value)
    {
        if (value is not null)
        {
            _ = LoadFoldersAsync(value);
        }
        else
        {
            Folders.Clear();
            Messages.Clear();
        }
    }

    partial void OnSelectedFolderChanged(FolderViewModel? value)
    {
        if (value is not null)
        {
            _ = LoadMessagesAsync(value);
        }
    }

    partial void OnSelectedMessageChanged(MessageItemViewModel? value)
    {
        if (value is not null)
        {
            Compose = null;
        }

        _ = LoadBodyAsync(value);
    }

    // ================================================================ loading

    private async Task ReloadAccountsAsync()
    {
        try
        {
            var accounts = await _mail.GetAccountsAsync();
            var selectedId = SelectedAccount?.Id;
            Accounts.Clear();
            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }

            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId) ?? Accounts.FirstOrDefault();
            AccountsLoaded = true;
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(ShowWelcome));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudieron cargar las cuentas: {ex.Message}";
        }
    }

    private async Task LoadFoldersAsync(Account account)
    {
        var ct = Restart(ref _foldersCts);
        try
        {
            var folders = await _mail.GetFoldersAsync(account.Id, ct);
            ct.ThrowIfCancellationRequested();

            // Same folders as before: only refresh unread counts, keeping the selection untouched.
            if (folders.Select(f => f.Id).SequenceEqual(Folders.Select(f => f.Id)))
            {
                foreach (var (vm, folder) in Folders.Zip(folders))
                {
                    vm.UnreadCount = folder.UnreadCount;
                }

                return;
            }

            var selectedId = SelectedFolder?.Id;
            Folders.Clear();
            foreach (var folder in folders)
            {
                Folders.Add(new FolderViewModel(folder));
            }

            SelectedFolder = Folders.FirstOrDefault(f => f.Id == selectedId)
                ?? Folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox)
                ?? Folders.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudieron cargar las carpetas: {ex.Message}";
        }
    }

    private async Task LoadMessagesAsync(FolderViewModel folder)
    {
        var ct = Restart(ref _messagesCts);
        IsLoadingMessages = true;
        try
        {
            var messages = await _mail.GetMessagesAsync(folder.Id, ct);
            ct.ThrowIfCancellationRequested();
            MergeMessages(messages);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudieron cargar los mensajes: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingMessages = false;
            }
        }
    }

    /// <summary>Applies a fresh list in place (insert/move/remove) so selection and scroll position survive.</summary>
    private void MergeMessages(IReadOnlyList<MailMessageSummary> incoming)
    {
        var incomingIds = incoming.Select(m => m.Id).ToHashSet();
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(Messages[i].Id))
            {
                Messages.RemoveAt(i);
            }
        }

        var existing = Messages.ToDictionary(m => m.Id);
        for (var i = 0; i < incoming.Count; i++)
        {
            var summary = incoming[i];
            if (existing.TryGetValue(summary.Id, out var vm))
            {
                vm.IsRead = summary.IsRead;
                vm.IsFlagged = summary.IsFlagged;
                var current = Messages.IndexOf(vm);
                if (current != i)
                {
                    Messages.Move(current, i);
                }
            }
            else
            {
                Messages.Insert(i, new MessageItemViewModel(summary));
            }
        }
    }

    private async Task LoadBodyAsync(MessageItemViewModel? message)
    {
        var ct = Restart(ref _bodyCts);
        SelectedBody = null;
        if (message is null)
        {
            IsLoadingBody = false;
            return;
        }

        IsLoadingBody = true;
        try
        {
            var body = await _mail.GetBodyAsync(message.Id, ct);
            ct.ThrowIfCancellationRequested();
            SelectedBody = body;
            if (!message.IsRead)
            {
                await SetReadAsync(message, true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudo abrir el mensaje: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingBody = false;
            }
        }
    }

    private async Task SetReadAsync(MessageItemViewModel message, bool isRead)
    {
        if (message.IsRead == isRead)
        {
            return;
        }

        message.IsRead = isRead;
        AdjustUnread(isRead ? -1 : +1);
        try
        {
            await _mail.SetReadAsync(message.Id, isRead);
        }
        catch (Exception ex)
        {
            message.IsRead = !isRead;
            AdjustUnread(isRead ? +1 : -1);
            ErrorMessage = $"No se pudo actualizar el mensaje: {ex.Message}";
        }
    }

    private void AdjustUnread(int delta)
    {
        if (SelectedFolder is not null)
        {
            SelectedFolder.UnreadCount = Math.Max(0, SelectedFolder.UnreadCount + delta);
        }
    }

    // ================================================================ background changes

    private void OnMailChanged(object? sender, MailChange change)
    {
        // Raised on a background thread: hop to the UI thread before touching bound state.
        if (_ui is null)
        {
            _ = HandleChangeAsync(change);
        }
        else
        {
            _ui.Post(_ => _ = HandleChangeAsync(change), null);
        }
    }

    private async Task HandleChangeAsync(MailChange change)
    {
        switch (change.Kind)
        {
            case MailChangeKind.Accounts:
                await ReloadAccountsAsync();
                break;
            case MailChangeKind.Folders when SelectedAccount is { } account && account.Id == change.AccountId:
                await LoadFoldersAsync(account);
                break;
            case MailChangeKind.Messages when SelectedFolder?.Id == change.FolderId:
                var messages = await _mail.GetMessagesAsync(change.FolderId!);
                if (SelectedFolder?.Id == change.FolderId)
                {
                    MergeMessages(messages);
                }

                break;
            case MailChangeKind.Error:
                ErrorMessage = change.Message;
                break;
        }
    }

    // ================================================================ compose helpers

    private ComposeViewModel CreateCompose(string? inReplyTo = null) =>
        new(_mail, Accounts.ToList(), SelectedAccount!, closed =>
        {
            if (Compose == closed)
            {
                Compose = null;
            }
        })
        {
            InReplyTo = inReplyTo,
        };

    private void StartReply(bool replyAll)
    {
        if (SelectedAccount is null || SelectedMessage is null)
        {
            return;
        }

        var me = SelectedAccount.Email;
        var compose = CreateCompose(SelectedBody?.MessageIdHeader);
        compose.To = SelectedMessage.Summary.From.ToString();
        if (replyAll && SelectedBody is not null)
        {
            var others = SelectedBody.To.Where(a => !a.Email.Equals(me, StringComparison.OrdinalIgnoreCase)).Select(a => a.ToString());
            compose.To = string.Join("; ", new[] { compose.To }.Concat(others));
            compose.Cc = string.Join("; ", SelectedBody.Cc.Where(a => !a.Email.Equals(me, StringComparison.OrdinalIgnoreCase)).Select(a => a.ToString()));
        }

        compose.Subject = Prefixed("RE: ", SelectedMessage.Subject);
        compose.Body = QuotedOriginal();
        Compose = compose;
    }

    private string QuotedOriginal()
    {
        var m = SelectedMessage!;
        var header = string.Join(
            "\n",
            "",
            "",
            "________________________________",
            $"De: {m.Summary.From}",
            $"Enviado: {m.Summary.Received.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)}",
            SelectedRecipients is { } to ? $"Para: {to}" : null,
            $"Asunto: {m.Subject}",
            "",
            SelectedBody?.Text ?? m.Preview);
        return header;
    }

    private static string Prefixed(string prefix, string subject) =>
        subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? subject : prefix + subject;

    private static CancellationToken Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }
}
