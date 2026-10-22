using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyMail.Core.Models;
using MyMail.Storage;
using MyMail.Sync.Auth;

namespace MyMail.Sync.Graph;

/// <summary>
/// Microsoft Graph backend for Outlook.com and Microsoft 365. Works even where the tenant
/// disables IMAP or SMTP AUTH, and syncs folders with delta queries.
/// </summary>
public sealed class GraphBackend(StoredAccount account, MailStore store, MicrosoftAuth auth, HttpClient http) : IMailBackend
{
    private const string Me = "https://graph.microsoft.com/v1.0/me";
    private const string MessageFields = "subject,from,receivedDateTime,isRead,flag,hasAttachments,bodyPreview";

    /// <summary>How far back the first sync of a folder goes.</summary>
    private static readonly TimeSpan InitialWindow = TimeSpan.FromDays(30);

    private static readonly (string WellKnown, FolderKind Kind)[] WellKnownFolders =
    [
        ("inbox", FolderKind.Inbox),
        ("drafts", FolderKind.Drafts),
        ("sentitems", FolderKind.Sent),
        ("archive", FolderKind.Archive),
        ("junkemail", FolderKind.Junk),
        ("deleteditems", FolderKind.Deleted),
    ];

    public StoredAccount Account { get; set; } = account;

    private string AccountId => Account.Account.Id;

    public async Task VerifyAsync(CancellationToken ct) =>
        await GetJsonAsync($"{Me}/mailFolders/inbox?$select=id", ct).ConfigureAwait(false);

    /// <summary>Display name and primary address of the signed-in user.</summary>
    public async Task<(string DisplayName, string Email)> GetProfileAsync(CancellationToken ct)
    {
        var me = await GetJsonAsync($"{Me}?$select=displayName,mail,userPrincipalName", ct).ConfigureAwait(false);
        var email = (string?)me["mail"] ?? (string?)me["userPrincipalName"] ?? Account.Settings.UserName;
        return ((string?)me["displayName"] ?? email, email);
    }

    // ---------------------------------------------------------------- folders

    public async Task SyncFoldersAsync(CancellationToken ct)
    {
        var kinds = new Dictionary<string, FolderKind>();
        foreach (var (wellKnown, kind) in WellKnownFolders)
        {
            try
            {
                var f = await GetJsonAsync($"{Me}/mailFolders/{wellKnown}?$select=id", ct).ConfigureAwait(false);
                kinds[(string)f["id"]!] = kind;
            }
            catch (GraphException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                // e.g. no archive folder yet.
            }
        }

        var folders = new List<StoredFolder>();
        await CollectFoldersAsync($"{Me}/mailFolders?$top=100&$select=id,displayName,childFolderCount", null, depth: 0);

        async Task CollectFoldersAsync(string url, string? parentName, int depth)
        {
            await foreach (var item in PagesAsync(url, ct).ConfigureAwait(false))
            {
                var id = (string)item["id"]!;
                var name = (string?)item["displayName"] ?? "";
                var fullName = parentName is null ? name : $"{parentName} / {name}";
                var kind = kinds.TryGetValue(id, out var k) ? k : FolderKind.Custom;
                var display = kind == FolderKind.Custom ? fullName : FolderNames.For(kind);
                folders.Add(new StoredFolder(new MailFolder(MailIds.Folder(AccountId, id), AccountId, display, kind), id));

                if ((int?)item["childFolderCount"] > 0 && depth < 3)
                {
                    await CollectFoldersAsync($"{Me}/mailFolders/{id}/childFolders?$top=100&$select=id,displayName,childFolderCount", fullName, depth + 1)
                        .ConfigureAwait(false);
                }
            }
        }

        var ordered = folders
            .OrderBy(f => FolderNames.Rank(f.Folder.Kind))
            .ThenBy(f => f.Folder.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        await store.ReplaceFoldersAsync(AccountId, ordered).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- headers (delta query)

    public async Task<bool> SyncFolderAsync(string folderId, CancellationToken ct)
    {
        var (_, remoteFolderId) = MailIds.ParseFolder(folderId);
        var url = await store.GetSyncStateAsync(folderId).ConfigureAwait(false);
        if (url is null)
        {
            var since = DateTimeOffset.UtcNow.Subtract(InitialWindow).ToString("yyyy-MM-ddTHH:mm:ssZ");
            url = $"{Me}/mailFolders/{remoteFolderId}/messages/delta?$select={MessageFields}&$filter=receivedDateTime+ge+{since}";
        }

        var upserts = new List<StoredMessage>();
        var removed = new List<string>();
        try
        {
            while (url is not null)
            {
                var page = await GetJsonAsync(url, ct, maxPageSize: 100).ConfigureAwait(false);
                foreach (var item in page["value"]?.AsArray() ?? [])
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (item["@removed"] is not null)
                    {
                        removed.Add((string)item["id"]!);
                    }
                    else
                    {
                        upserts.Add(ToStored(folderId, item));
                    }
                }

                if ((string?)page["@odata.deltaLink"] is { } deltaLink)
                {
                    await store.UpsertMessagesAsync(upserts).ConfigureAwait(false);
                    if (removed.Count > 0)
                    {
                        await store.ApplyServerStateAsync(folderId, new Dictionary<string, (bool, bool)>(), removed).ConfigureAwait(false);
                    }

                    await store.SetSyncStateAsync(folderId, deltaLink).ConfigureAwait(false);
                    return upserts.Count > 0 || removed.Count > 0;
                }

                url = (string?)page["@odata.nextLink"];
            }
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.Gone)
        {
            // Delta token expired: start over with a fresh window.
            await store.SetSyncStateAsync(folderId, null).ConfigureAwait(false);
            await store.ClearFolderAsync(folderId).ConfigureAwait(false);
            return await SyncFolderAsync(folderId, ct).ConfigureAwait(false);
        }

        return false;
    }

    // ---------------------------------------------------------------- bodies

    public async Task<MailMessageBody> FetchBodyAsync(string messageId, CancellationToken ct)
    {
        var (_, remoteId) = MailIds.ParseMessage(messageId);
        var message = await GetJsonAsync(
            $"{Me}/messages/{remoteId}?$select=body,toRecipients,ccRecipients,internetMessageId",
            ct,
            preferHtml: true).ConfigureAwait(false);

        var body = message["body"];
        var isHtml = string.Equals((string?)body?["contentType"], "html", StringComparison.OrdinalIgnoreCase);
        var content = (string?)body?["content"] ?? "";
        var html = isHtml ? await InlineImagesAsync(remoteId, content, ct).ConfigureAwait(false) : null;

        return new MailMessageBody(
            messageId,
            Recipients(message["toRecipients"]),
            Recipients(message["ccRecipients"]),
            isHtml ? MimeConversion.HtmlToText(content) ?? "" : content,
            html,
            (string?)message["internetMessageId"]);
    }

    /// <summary>Replaces cid: references with data: URIs from the message's inline attachments.</summary>
    private async Task<string> InlineImagesAsync(string remoteId, string html, CancellationToken ct)
    {
        if (!html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var attachments = await GetJsonAsync($"{Me}/messages/{remoteId}/attachments?$filter=isInline eq true", ct).ConfigureAwait(false);
        foreach (var a in attachments["value"]?.AsArray() ?? [])
        {
            if ((string?)a?["contentId"] is { Length: > 0 } contentId && (string?)a["contentBytes"] is { } bytes)
            {
                html = html.Replace("cid:" + contentId, $"data:{(string?)a["contentType"]};base64,{bytes}", StringComparison.OrdinalIgnoreCase);
            }
        }

        return html;
    }

    // ---------------------------------------------------------------- queued changes

    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        var pending = await store.GetPendingAsync(AccountId).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        var deletedFolder = (await store.GetFoldersAsync(AccountId).ConfigureAwait(false))
            .FirstOrDefault(f => f.Folder.Kind == FolderKind.Deleted)?.Folder.Id;

        foreach (var op in pending)
        {
            var (folderId, remoteId) = MailIds.ParseMessage(op.MessageId);
            try
            {
                switch (op.Kind)
                {
                    case MailStore.OpSetRead:
                        await SendJsonAsync(HttpMethod.Patch, $"{Me}/messages/{remoteId}", new { isRead = op.Value }, ct).ConfigureAwait(false);
                        break;
                    case MailStore.OpSetFlagged:
                        await SendJsonAsync(HttpMethod.Patch, $"{Me}/messages/{remoteId}",
                            new { flag = new { flagStatus = op.Value ? "flagged" : "notFlagged" } }, ct).ConfigureAwait(false);
                        break;
                    case MailStore.OpDelete when folderId == deletedFolder:
                        await SendJsonAsync(HttpMethod.Delete, $"{Me}/messages/{remoteId}", null, ct).ConfigureAwait(false);
                        break;
                    case MailStore.OpDelete:
                        await SendJsonAsync(HttpMethod.Post, $"{Me}/messages/{remoteId}/move", new { destinationId = "deleteditems" }, ct)
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (GraphException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                // Already gone or not applicable: drop it instead of retrying forever.
            }

            await store.CompleteAsync(op.Id).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- sending

    public Task SendAsync(OutgoingMessage outgoing, CancellationToken ct)
    {
        var payload = new
        {
            message = new
            {
                subject = outgoing.Subject,
                body = new { contentType = "Text", content = outgoing.Body },
                toRecipients = outgoing.To.Select(GraphRecipient).ToArray(),
                ccRecipients = outgoing.Cc.Select(GraphRecipient).ToArray(),
            },
            saveToSentItems = true,
        };
        return SendJsonAsync(HttpMethod.Post, $"{Me}/sendMail", payload, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---------------------------------------------------------------- mapping

    private static object GraphRecipient(MailAddress a) => new { emailAddress = new { name = a.Name, address = a.Email } };

    private static List<MailAddress> Recipients(JsonNode? node) =>
        (node?.AsArray() ?? [])
            .Select(r => new MailAddress((string?)r?["emailAddress"]?["name"] ?? "", (string?)r?["emailAddress"]?["address"] ?? ""))
            .ToList();

    private static StoredMessage ToStored(string folderId, JsonNode item)
    {
        var remoteId = (string)item["id"]!;
        var from = item["from"]?["emailAddress"];
        var subject = (string?)item["subject"];
        var summary = new MailMessageSummary(
            MailIds.Message(folderId, remoteId),
            folderId,
            new MailAddress((string?)from?["name"] ?? "", (string?)from?["address"] ?? ""),
            string.IsNullOrWhiteSpace(subject) ? "(Sin asunto)" : subject,
            MimeConversion.CleanPreview((string?)item["bodyPreview"]),
            DateTimeOffset.TryParse((string?)item["receivedDateTime"], out var received) ? received : DateTimeOffset.Now,
            (bool?)item["isRead"] ?? false,
            (string?)item["flag"]?["flagStatus"] == "flagged",
            (bool?)item["hasAttachments"] ?? false);
        return new StoredMessage(summary, remoteId);
    }

    // ---------------------------------------------------------------- HTTP

    private async IAsyncEnumerable<JsonNode> PagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = url;
        while (next is not null)
        {
            var page = await GetJsonAsync(next, ct).ConfigureAwait(false);
            foreach (var item in page["value"]?.AsArray() ?? [])
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            next = (string?)page["@odata.nextLink"];
        }
    }

    private async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct, int? maxPageSize = null, bool preferHtml = false)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (maxPageSize is { } size)
            {
                request.Headers.Add("Prefer", $"odata.maxpagesize={size}");
            }

            if (preferHtml)
            {
                request.Headers.Add("Prefer", "outlook.body-content-type=\"html\"");
            }

            return request;
        }, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false))!;
    }

    private async Task SendJsonAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return request;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Sends with a fresh token, honoring Retry-After once on throttling.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await auth.GetAccessTokenAsync(Account.Settings.UserName, ct).ConfigureAwait(false);
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (attempt == 0 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = response.StatusCode;
            response.Dispose();
            throw GraphException.From(status, text);
        }
    }
}

public sealed class GraphException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string Code { get; } = code;

    public static GraphException From(HttpStatusCode status, string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            return new GraphException(status, (string?)error?["code"] ?? "", (string?)error?["message"] ?? body);
        }
        catch (JsonException)
        {
            return new GraphException(status, "", $"Microsoft Graph respondió {(int)status}.");
        }
    }
}
