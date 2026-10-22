using MyMail.Core.Models;
using MyMail.Core.ViewModels;
using MyMail.Sync;

namespace MyMail.Tests;

public class SyncTests
{
    [Theory]
    [InlineData("ana@gmail.com", MailProvider.Gmail)]
    [InlineData("ana@outlook.com", MailProvider.Outlook)]
    [InlineData("ana@hotmail.es", MailProvider.Outlook)]
    [InlineData("ana@icloud.com", MailProvider.ICloud)]
    [InlineData("ana@yahoo.com.mx", MailProvider.Yahoo)]
    [InlineData("ana@empresa.com", MailProvider.Imap)]
    public void ProviderFor_recognizes_known_domains(string email, MailProvider expected) =>
        Assert.Equal(expected, ServerDiscovery.ProviderFor(email));

    [Fact]
    public void Microsoft_accounts_use_graph_with_oauth()
    {
        var settings = ServerDiscovery.KnownSettings("ana@outlook.com")!;
        Assert.Equal(AuthKind.OAuthMicrosoft, settings.Auth);
        Assert.Equal("graph.microsoft.com", settings.ImapHost);
        Assert.Equal("ana@outlook.com", settings.UserName);
    }

    [Fact]
    public void ParseAutoconfig_reads_imap_and_smtp_with_placeholders()
    {
        const string xml = """
            <clientConfig version="1.1">
              <emailProvider id="example.com">
                <incomingServer type="pop3"><hostname>pop.example.com</hostname><port>995</port></incomingServer>
                <incomingServer type="imap">
                  <hostname>imap.example.com</hostname><port>993</port><username>%EMAILLOCALPART%</username>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>smtp.example.com</hostname><port>465</port><username>%EMAILADDRESS%</username>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;

        var settings = ServerDiscovery.ParseAutoconfig(xml, "ana@example.com")!;

        Assert.Equal("imap.example.com", settings.ImapHost);
        Assert.Equal(993, settings.ImapPort);
        Assert.Equal("smtp.example.com", settings.SmtpHost);
        Assert.Equal(465, settings.SmtpPort);
        Assert.Equal("ana", settings.UserName);
    }

    [Fact]
    public void MailIds_round_trip_imap_and_graph_ids()
    {
        var imapFolder = MailIds.Folder("acc1", "[Gmail]/Sent Mail");
        var imapMessage = MailIds.Message(imapFolder, "4242");
        Assert.Equal(("acc1", "[Gmail]/Sent Mail"), MailIds.ParseFolder(imapFolder));
        Assert.Equal((imapFolder, "4242"), MailIds.ParseMessage(imapMessage));
        Assert.Equal("acc1", MailIds.AccountOf(imapMessage));

        var graphFolder = MailIds.Folder("acc2", "AAMkAGI2T/G+ab==");
        var graphMessage = MailIds.Message(graphFolder, "AAMkADBh/+x9==");
        Assert.Equal((graphFolder, "AAMkADBh/+x9=="), MailIds.ParseMessage(graphMessage));
    }

    [Fact]
    public void ParseAddresses_accepts_names_and_separators()
    {
        var list = ComposeViewModel.ParseAddresses("Ana Torres <ana@contoso.com>; luis@fabrikam.com, \"Marta\" <marta@x.com>, no-es-correo");

        Assert.Equal(3, list.Count);
        Assert.Equal(new MailAddress("Ana Torres", "ana@contoso.com"), list[0]);
        Assert.Equal(new MailAddress("", "luis@fabrikam.com"), list[1]);
        Assert.Equal(new MailAddress("Marta", "marta@x.com"), list[2]);
    }

    [Fact]
    public void HtmlToText_drops_tags_styles_and_entities()
    {
        var text = MimeConversion.HtmlToText("<style>p{color:red}</style><p>Hola&nbsp;<b>Ana</b></p><script>x()</script>");
        Assert.Equal("Hola Ana", text);
    }
}
