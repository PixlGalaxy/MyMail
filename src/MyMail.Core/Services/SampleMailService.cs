using System.Collections.Concurrent;
using MyMail.Core.Models;

namespace MyMail.Core.Services;

/// <summary>
/// In-memory data with simulated latency, used until the real providers exist.
/// The delays make it obvious if any call accidentally blocks the UI thread.
/// </summary>
public sealed class SampleMailService : IMailService
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(250);

    private readonly List<Account> _accounts =
    [
        new("acc-outlook", "Outlook", "usuario@outlook.com", MailProvider.Outlook),
        new("acc-gmail", "Gmail", "usuario@gmail.com", MailProvider.Gmail),
    ];

    private readonly ConcurrentDictionary<string, MailMessageSummary> _messages = new();

    public SampleMailService()
    {
        var people = new[]
        {
            new MailAddress("Ana Torres", "ana.torres@contoso.com"),
            new MailAddress("Equipo de GitHub", "noreply@github.com"),
            new MailAddress("Luis Ramírez", "luis@fabrikam.com"),
            new MailAddress("Marta Gómez", "marta.gomez@outlook.com"),
            new MailAddress("Banco Ejemplo", "avisos@banco.example"),
        };
        var subjects = new[]
        {
            ("Reunión del lunes", "Te confirmo la reunión del lunes a las 10:00. Llevo la presentación actualizada."),
            ("[MyMail] Nuevo issue abierto", "Se abrió un nuevo issue en tu repositorio: la lista de mensajes no se actualiza."),
            ("Fotos del viaje", "Te paso las fotos del fin de semana, ¡quedaron geniales!"),
            ("Presupuesto 2027", "Adjunto el borrador del presupuesto para revisión antes del viernes."),
            ("Tu estado de cuenta está disponible", "Ya puedes consultar tu estado de cuenta del mes de octubre."),
        };

        var now = DateTimeOffset.Now;
        foreach (var account in _accounts)
        {
            for (var i = 0; i < 25; i++)
            {
                var (subject, preview) = subjects[i % subjects.Length];
                var id = $"{account.Id}-msg-{i}";
                _messages[id] = new MailMessageSummary(
                    id,
                    $"{account.Id}-inbox",
                    people[i % people.Length],
                    subject,
                    preview,
                    now.AddHours(-i * 5),
                    IsRead: i % 3 != 0,
                    IsFlagged: i % 7 == 0,
                    HasAttachments: i % 4 == 3);
            }
        }
    }

    public event EventHandler<MailChange>? Changed
    {
        add { }
        remove { }
    }

    public void StartBackgroundSync()
    {
    }

    public Task SendAsync(OutgoingMessage message, CancellationToken ct = default) => Task.Delay(Latency, ct);

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return _accounts;
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(string accountId, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        var unread = _messages.Values.Count(m => m.FolderId == $"{accountId}-inbox" && !m.IsRead);
        return
        [
            new($"{accountId}-inbox", accountId, "Bandeja de entrada", FolderKind.Inbox, unread),
            new($"{accountId}-drafts", accountId, "Borradores", FolderKind.Drafts),
            new($"{accountId}-sent", accountId, "Elementos enviados", FolderKind.Sent),
            new($"{accountId}-archive", accountId, "Archivo", FolderKind.Archive),
            new($"{accountId}-junk", accountId, "Correo no deseado", FolderKind.Junk),
            new($"{accountId}-deleted", accountId, "Elementos eliminados", FolderKind.Deleted),
        ];
    }

    public async Task<IReadOnlyList<MailMessageSummary>> GetMessagesAsync(string folderId, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return _messages.Values
            .Where(m => m.FolderId == folderId)
            .OrderByDescending(m => m.Received)
            .ToList();
    }

    public async Task<MailMessageBody> GetBodyAsync(string messageId, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        var m = _messages[messageId];
        var text = $"Hola,\n\n{m.Preview}\n\nSaludos,\n{m.From.Name}";
        return new MailMessageBody(messageId, [new MailAddress("Usuario", "usuario@outlook.com")], [], text);
    }

    public Task SetReadAsync(string messageId, bool isRead, CancellationToken ct = default) =>
        UpdateAsync(messageId, m => m with { IsRead = isRead }, ct);

    public Task SetFlaggedAsync(string messageId, bool isFlagged, CancellationToken ct = default) =>
        UpdateAsync(messageId, m => m with { IsFlagged = isFlagged }, ct);

    public async Task DeleteAsync(string messageId, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        _messages.TryRemove(messageId, out _);
    }

    public Task SyncAsync(string accountId, CancellationToken ct = default) =>
        Task.Delay(TimeSpan.FromSeconds(1), ct);

    private async Task UpdateAsync(string messageId, Func<MailMessageSummary, MailMessageSummary> change, CancellationToken ct)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        if (_messages.TryGetValue(messageId, out var m))
        {
            _messages[messageId] = change(m);
        }
    }
}
