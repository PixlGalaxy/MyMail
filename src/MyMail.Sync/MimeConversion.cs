using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using MyMail.Core.Models;

namespace MyMail.Sync;

/// <summary>Conversions between MimeKit messages and MyMail models, shared by the backends.</summary>
public static partial class MimeConversion
{
    public static MimeMessage ToMime(Account account, OutgoingMessage outgoing)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        message.To.AddRange(outgoing.To.Select(a => new MailboxAddress(a.Name, a.Email)));
        message.Cc.AddRange(outgoing.Cc.Select(a => new MailboxAddress(a.Name, a.Email)));
        message.Subject = outgoing.Subject;
        if (outgoing.InReplyTo is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
            message.References.Add(inReplyTo);
        }

        message.Body = new TextPart("plain") { Text = outgoing.Body };
        return message;
    }

    public static async Task<MailMessageBody> ToBodyAsync(string messageId, MimeMessage message, CancellationToken ct) =>
        new(
            messageId,
            message.To.Mailboxes.Select(ToAddress).ToList(),
            message.Cc.Mailboxes.Select(ToAddress).ToList(),
            message.TextBody ?? HtmlToText(message.HtmlBody) ?? "",
            await InlineImagesAsync(message, ct).ConfigureAwait(false),
            message.MessageId);

    public static string CleanPreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return "";
        }

        var text = Whitespace().Replace(preview, " ").Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    public static string? HtmlToText(string? html)
    {
        if (html is null)
        {
            return null;
        }

        var noBlocks = StyleOrScript().Replace(html, " ");
        var text = WebUtility.HtmlDecode(Tags().Replace(noBlocks, " "));
        return Whitespace().Replace(text, " ").Trim();
    }

    private static MailAddress ToAddress(MailboxAddress m) => new(m.Name ?? "", m.Address);

    /// <summary>Replaces cid: references with data: URIs so inline images render without network access.</summary>
    private static async Task<string?> InlineImagesAsync(MimeMessage message, CancellationToken ct)
    {
        var html = message.HtmlBody;
        if (html is null)
        {
            return null;
        }

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content is null
                || !html.Contains("cid:" + part.ContentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var buffer = new MemoryStream();
            await part.Content.DecodeToAsync(buffer, ct).ConfigureAwait(false);
            var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(buffer.ToArray())}";
            html = html.Replace("cid:" + part.ContentId, dataUri, StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"<(style|script)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleOrScript();
}
