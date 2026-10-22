using System.Xml.Linq;
using MyMail.Core.Models;

namespace MyMail.Sync;

/// <summary>
/// Finds IMAP/SMTP settings for an address: built-in table for the big providers,
/// then Mozilla's ISPDB (autoconfig.thunderbird.net), then the imap./smtp. convention.
/// </summary>
public sealed class ServerDiscovery(HttpClient http)
{
    private static readonly Dictionary<string, (MailProvider Provider, ServerSettings Settings)> Known = BuildKnown();

    public static MailProvider ProviderFor(string email)
    {
        var domain = DomainOf(email);
        return Known.TryGetValue(domain, out var known) ? known.Provider : MailProvider.Imap;
    }

    /// <summary>Settings for a known provider, with the address as user name.</summary>
    public static ServerSettings? KnownSettings(string email)
    {
        return Known.TryGetValue(DomainOf(email), out var known)
            ? known.Settings with { UserName = email }
            : null;
    }

    public async Task<ServerSettings?> DiscoverAsync(string email, CancellationToken ct)
    {
        if (KnownSettings(email) is { } known)
        {
            return known;
        }

        var domain = DomainOf(email);
        if (domain.Length == 0)
        {
            return null;
        }

        try
        {
            using var response = await http
                .GetAsync($"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}", ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (ParseAutoconfig(xml, email) is { } fromIspdb)
                {
                    return fromIspdb;
                }
            }
        }
        catch (HttpRequestException)
        {
            // Offline or ISPDB unavailable: fall through to the convention.
        }

        return new ServerSettings($"imap.{domain}", 993, $"smtp.{domain}", 587, email, AuthKind.Password);
    }

    /// <summary>Parses a Thunderbird autoconfig document (clientConfig v1.1).</summary>
    public static ServerSettings? ParseAutoconfig(string xml, string email)
    {
        var doc = XDocument.Parse(xml);
        var imap = doc.Descendants("incomingServer").FirstOrDefault(e => (string?)e.Attribute("type") == "imap");
        var smtp = doc.Descendants("outgoingServer").FirstOrDefault(e => (string?)e.Attribute("type") == "smtp");
        if (imap is null || smtp is null)
        {
            return null;
        }

        var userName = ExpandPlaceholders((string?)imap.Element("username") ?? "%EMAILADDRESS%", email);
        return new ServerSettings(
            (string?)imap.Element("hostname") ?? "",
            (int?)imap.Element("port") ?? 993,
            (string?)smtp.Element("hostname") ?? "",
            (int?)smtp.Element("port") ?? 587,
            userName,
            AuthKind.Password);
    }

    private static string ExpandPlaceholders(string value, string email)
    {
        var at = email.IndexOf('@');
        var local = at > 0 ? email[..at] : email;
        return value
            .Replace("%EMAILADDRESS%", email, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILLOCALPART%", local, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILDOMAIN%", DomainOf(email), StringComparison.OrdinalIgnoreCase);
    }

    private static string DomainOf(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 0 ? "" : email[(at + 1)..].Trim().ToLowerInvariant();
    }

    private static Dictionary<string, (MailProvider, ServerSettings)> BuildKnown()
    {
        var map = new Dictionary<string, (MailProvider, ServerSettings)>();
        // Microsoft accounts use Graph (HTTPS), not IMAP/SMTP.
        var outlook = new ServerSettings("graph.microsoft.com", 443, "graph.microsoft.com", 443, "", AuthKind.OAuthMicrosoft);
        foreach (var d in new[] { "outlook.com", "hotmail.com", "live.com", "msn.com", "outlook.es", "hotmail.es", "live.com.mx", "outlook.com.ar" })
        {
            map[d] = (MailProvider.Outlook, outlook);
        }

        var gmail = new ServerSettings("imap.gmail.com", 993, "smtp.gmail.com", 465, "", AuthKind.OAuthGoogle);
        map["gmail.com"] = (MailProvider.Gmail, gmail);
        map["googlemail.com"] = (MailProvider.Gmail, gmail);

        var yahoo = new ServerSettings("imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465, "", AuthKind.Password);
        foreach (var d in new[] { "yahoo.com", "yahoo.es", "yahoo.com.mx", "ymail.com", "rocketmail.com" })
        {
            map[d] = (MailProvider.Yahoo, yahoo);
        }

        var icloud = new ServerSettings("imap.mail.me.com", 993, "smtp.mail.me.com", 587, "", AuthKind.Password);
        foreach (var d in new[] { "icloud.com", "me.com", "mac.com" })
        {
            map[d] = (MailProvider.ICloud, icloud);
        }

        return map;
    }
}
