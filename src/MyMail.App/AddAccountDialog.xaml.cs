using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyMail.Core.Models;
using MyMail.Core.Services;

namespace MyMail.App;

/// <summary>Add-account flow. Every server call is awaited off the UI thread; Cancel aborts it.</summary>
public sealed partial class AddAccountDialog : ContentDialog
{
    private readonly IAccountService _accounts;
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _discoverCts;
    private MailProvider _passwordProvider = MailProvider.Imap;
    private bool _isBusy;

    public AddAccountDialog(IAccountService accounts)
    {
        _accounts = accounts;
        InitializeComponent();

        OutlookButton.IsEnabled = accounts.IsOAuthConfigured(MailProvider.Outlook);
        GmailButton.IsEnabled = accounts.IsOAuthConfigured(MailProvider.Gmail);
        OAuthMissingInfo.IsOpen = !OutlookButton.IsEnabled || !GmailButton.IsEnabled;
    }

    public Account? AddedAccount { get; private set; }

    private async void OnOutlookClick(object sender, RoutedEventArgs e) =>
        await SignInWithProviderAsync(MailProvider.Outlook, "Completa el inicio de sesión de Microsoft en la ventana que se abrió.");

    private async void OnGmailClick(object sender, RoutedEventArgs e) =>
        await SignInWithProviderAsync(MailProvider.Gmail, "Completa el inicio de sesión de Google en tu navegador y vuelve aquí.");

    private void OnYahooClick(object sender, RoutedEventArgs e) => ShowPasswordForm(
        MailProvider.Yahoo,
        "Yahoo requiere una contraseña de aplicación: créala en Seguridad de la cuenta de Yahoo y pégala aquí.");

    private void OnICloudClick(object sender, RoutedEventArgs e) => ShowPasswordForm(
        MailProvider.ICloud,
        "iCloud requiere una contraseña específica de app: créala en appleid.apple.com y pégala aquí.");

    private void OnOtherClick(object sender, RoutedEventArgs e) => ShowPasswordForm(
        MailProvider.Imap,
        "Escribe tu dirección y contraseña. Detectaremos los servidores automáticamente; puedes ajustarlos en Configuración avanzada.");

    private async Task SignInWithProviderAsync(MailProvider provider, string waitingText)
    {
        SetBusy(waitingText);
        try
        {
            AddedAccount = await _accounts.AddOAuthAccountAsync(provider, _cts.Token);
            Hide();
        }
        catch (OperationCanceledException)
        {
            ShowStep(ProviderPanel);
        }
        catch (Exception ex)
        {
            ShowStep(ProviderPanel);
            ShowError(ex.Message);
        }
    }

    private void ShowPasswordForm(MailProvider provider, string hint)
    {
        _passwordProvider = provider;
        PasswordHint.Text = hint;
        PrimaryButtonText = "Iniciar sesión";
        ShowStep(PasswordPanel);
        EmailBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Guess servers as soon as the address is typed, without blocking typing.</summary>
    private async void OnEmailLostFocus(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@'))
        {
            return;
        }

        _discoverCts?.Cancel();
        _discoverCts = new CancellationTokenSource();
        try
        {
            var settings = await _accounts.DiscoverAsync(email, _discoverCts.Token);
            if (settings is null || EmailBox.Text.Trim() != email)
            {
                return;
            }

            ImapHostBox.Text = settings.ImapHost;
            ImapPortBox.Value = settings.ImapPort;
            SmtpHostBox.Text = settings.SmtpHost;
            SmtpPortBox.Value = settings.SmtpPort;
            UserNameBox.Text = settings.UserName;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Discovery is a convenience; the user can still fill the fields by hand.
            AdvancedExpander.IsExpanded = true;
        }
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open while we verify; close it ourselves on success.
        args.Cancel = true;
        if (_isBusy || PasswordPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') || PasswordBox.Password.Length == 0)
        {
            ShowError("Escribe la dirección de correo y la contraseña.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ImapHostBox.Text))
        {
            OnEmailLostFocus(EmailBox, new RoutedEventArgs());
            ShowError("No se encontraron los servidores. Revisa Configuración avanzada.");
            AdvancedExpander.IsExpanded = true;
            return;
        }

        var settings = new ServerSettings(
            ImapHostBox.Text.Trim(),
            (int)ImapPortBox.Value,
            SmtpHostBox.Text.Trim(),
            (int)SmtpPortBox.Value,
            string.IsNullOrWhiteSpace(UserNameBox.Text) ? email : UserNameBox.Text.Trim(),
            AuthKind.Password);

        SetBusy("Comprobando la conexión con el servidor…");
        try
        {
            AddedAccount = await _accounts.AddPasswordAccountAsync(NameBox.Text.Trim(), email, PasswordBox.Password, settings, _passwordProvider, _cts.Token);
            Hide();
        }
        catch (OperationCanceledException)
        {
            ShowStep(PasswordPanel);
        }
        catch (Exception ex)
        {
            ShowStep(PasswordPanel);
            AdvancedExpander.IsExpanded = true;
            ShowError(ex.Message);
        }
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _discoverCts?.Cancel();
    }

    private void SetBusy(string text)
    {
        ErrorInfo.IsOpen = false;
        BusyText.Text = text;
        ShowStep(BusyPanel);
        _isBusy = true;
        IsPrimaryButtonEnabled = false;
    }

    private void ShowStep(UIElement step)
    {
        _isBusy = false;
        IsPrimaryButtonEnabled = true;
        foreach (var panel in new UIElement[] { ProviderPanel, PasswordPanel, BusyPanel })
        {
            panel.Visibility = panel == step ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorInfo.Message = message;
        ErrorInfo.IsOpen = true;
    }
}
