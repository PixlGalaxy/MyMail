using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyMail.Sync.Auth;

/// <summary>
/// OAuth client registrations. Read from %LOCALAPPDATA%\MyMail\oauth.json, or from the
/// MYMAIL_MS_CLIENT_ID / MYMAIL_GOOGLE_CLIENT_ID / MYMAIL_GOOGLE_CLIENT_SECRET environment variables.
/// </summary>
public sealed class OAuthOptions
{
    [JsonPropertyName("microsoft")]
    public MicrosoftClient? Microsoft { get; set; }

    [JsonPropertyName("google")]
    public GoogleClient? Google { get; set; }

    public bool HasMicrosoft => !string.IsNullOrWhiteSpace(Microsoft?.ClientId);

    public bool HasGoogle => !string.IsNullOrWhiteSpace(Google?.ClientId) && !string.IsNullOrWhiteSpace(Google?.ClientSecret);

    public static OAuthOptions Load(string directory)
    {
        var options = new OAuthOptions();
        var path = Path.Combine(directory, "oauth.json");
        if (File.Exists(path))
        {
            options = JsonSerializer.Deserialize<OAuthOptions>(File.ReadAllText(path)) ?? options;
        }

        var msClient = Environment.GetEnvironmentVariable("MYMAIL_MS_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(msClient))
        {
            options.Microsoft = new MicrosoftClient { ClientId = msClient };
        }

        var googleClient = Environment.GetEnvironmentVariable("MYMAIL_GOOGLE_CLIENT_ID");
        var googleSecret = Environment.GetEnvironmentVariable("MYMAIL_GOOGLE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(googleClient) && !string.IsNullOrWhiteSpace(googleSecret))
        {
            options.Google = new GoogleClient { ClientId = googleClient, ClientSecret = googleSecret };
        }

        return options;
    }

    public sealed class MicrosoftClient
    {
        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = "";
    }

    public sealed class GoogleClient
    {
        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = "";

        /// <summary>Desktop OAuth clients get a "secret" that is not confidential; Google still requires it.</summary>
        [JsonPropertyName("clientSecret")]
        public string ClientSecret { get; set; } = "";
    }
}
