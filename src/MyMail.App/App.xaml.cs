using Microsoft.UI.Xaml;
using MyMail.Core.Services;
using MyMail.Core.ViewModels;
using MyMail.Storage;
using MyMail.Sync;
using MyMail.Sync.Auth;

namespace MyMail.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Unpackaged apps would otherwise create the WebView2 profile next to the .exe (read-only once installed).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(MailStore.DefaultDirectory, "WebView2"));

        // MYMAIL_SAMPLE=1 runs the UI with in-memory demo data (no accounts, no network).
        if (Environment.GetEnvironmentVariable("MYMAIL_SAMPLE") == "1")
        {
            _window = new MainWindow(new ShellViewModel(new SampleMailService()));
            _window.Activate();
            return;
        }

        var dataDirectory = MailStore.DefaultDirectory;
        var store = new MailStore(Path.Combine(dataDirectory, "mymail.db"));
        await store.InitializeAsync();

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // WAM needs the window that owns its sign-in prompt; the window exists by the time anyone signs in.
        var credentials = new Credentials(OAuthOptions.Load(dataDirectory), dataDirectory, () => _window?.Handle ?? IntPtr.Zero);
        var mail = new MailService(store, credentials, http);

        _window = new MainWindow(new ShellViewModel(mail, mail));
        _window.Closed += async (_, _) => await mail.DisposeAsync();
        _window.Activate();
    }
}
