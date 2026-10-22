using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyMail.Core.Models;
using MyMail.Core.Services;

namespace MyMail.Core.ViewModels;

/// <summary>New message / reply / forward, shown in place of the reading pane (as in the original).</summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    private readonly IMailService _mail;
    private readonly Action<ComposeViewModel> _close;

    public ComposeViewModel(IMailService mail, IReadOnlyList<Account> accounts, Account from, Action<ComposeViewModel> close)
    {
        _mail = mail;
        _close = close;
        Accounts = accounts;
        _from = from;
    }

    public IReadOnlyList<Account> Accounts { get; }

    /// <summary>Message-ID being answered, for threading.</summary>
    public string? InReplyTo { get; init; }

    [ObservableProperty]
    private Account _from;

    [ObservableProperty]
    private string _to = "";

    [ObservableProperty]
    private string _cc = "";

    [ObservableProperty]
    private string _subject = "";

    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;

    [RelayCommand]
    private async Task SendAsync(CancellationToken ct)
    {
        var to = ParseAddresses(To);
        var cc = ParseAddresses(Cc);
        if (to.Count == 0)
        {
            ErrorMessage = "Escribe al menos un destinatario en «Para».";
            return;
        }

        ErrorMessage = null;
        try
        {
            await _mail.SendAsync(new OutgoingMessage(From.Id, to, cc, Subject, Body, InReplyTo), ct);
            _close(this);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudo enviar: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Discard() => _close(this);

    /// <summary>Accepts "a@b.com; Name &lt;c@d.com&gt;, e@f.com".</summary>
    public static List<MailAddress> ParseAddresses(string text)
    {
        var list = new List<MailAddress>();
        foreach (var raw in text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = NamedAddress().Match(raw);
            if (match.Success)
            {
                list.Add(new MailAddress(match.Groups[1].Value.Trim().Trim('"'), match.Groups[2].Value.Trim()));
            }
            else if (raw.Contains('@'))
            {
                list.Add(new MailAddress("", raw));
            }
        }

        return list;
    }

    [GeneratedRegex(@"^(.*)<([^>]+)>$")]
    private static partial Regex NamedAddress();
}
