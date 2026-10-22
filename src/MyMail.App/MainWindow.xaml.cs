using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using MyMail.Core.Models;
using MyMail.Core.ViewModels;

namespace MyMail.App;

public sealed partial class MainWindow : Window
{
    private const double CompactNavWidth = 48;
    private const double MinNavWidth = 200;
    private const double MaxNavWidth = 420;
    private const double MinListWidth = 280;
    private const double MaxListWidth = 640;

    /// <summary>Message HTML is served from this folder through a virtual host (NavigateToString caps at 2 MB).</summary>
    private const string RenderHost = "message.mymail.local";

    private static readonly string RenderFolder = Path.Combine(Storage.MailStore.DefaultDirectory, "render");

    private double _expandedNavWidth = 280;
    private bool _isNavExpanded = true;
    private Task? _webViewReady;
    private string? _renderedMessageId;
    private bool _allowRemoteContent;
    private int _renderVersion;

    public MainWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // AppWindow sizes are in physical pixels; scale the 1280x800 design size by the monitor DPI.
        var scale = GetDpiForWindow(Handle) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(800 * scale)));

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RootGrid.ActualThemeChanged += async (_, _) => await RenderBodyAsync();
    }

    public ShellViewModel ViewModel { get; }

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeCommand.ExecuteAsync(null);
    }

    // ================================================================ panes

    private void OnToggleNavPane(object sender, RoutedEventArgs e)
    {
        _isNavExpanded = !_isNavExpanded;
        NavColumn.Width = new GridLength(_isNavExpanded ? _expandedNavWidth : CompactNavWidth);
        ExpandedNavContent.Visibility = Ui.Visible(_isNavExpanded);
        CompactNavContent.Visibility = Ui.Collapsed(_isNavExpanded);
        ComposeLabel.Visibility = Ui.Visible(_isNavExpanded);
        NavThumb.Visibility = Ui.Visible(_isNavExpanded);
        AppSwitcher.Orientation = _isNavExpanded ? Orientation.Horizontal : Orientation.Vertical;
    }

    private void OnNavThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        _expandedNavWidth = Math.Clamp(NavColumn.ActualWidth + e.HorizontalChange, MinNavWidth, MaxNavWidth);
        NavColumn.Width = new GridLength(_expandedNavWidth);
    }

    private void OnListThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        var width = Math.Clamp(ListColumn.ActualWidth + e.HorizontalChange, MinListWidth, MaxListWidth);
        ListColumn.Width = new GridLength(width);
    }

    // ================================================================ message actions

    private async void OnSwipeFlag(SwipeItem sender, SwipeItemInvokedEventArgs args) =>
        await ViewModel.ToggleFlagCommand.ExecuteAsync(args.SwipeControl.DataContext as MessageItemViewModel);

    private async void OnSwipeDelete(SwipeItem sender, SwipeItemInvokedEventArgs args) =>
        await ViewModel.DeleteCommand.ExecuteAsync(args.SwipeControl.DataContext as MessageItemViewModel);

    private async void OnToggleReadClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ToggleReadCommand.ExecuteAsync(ItemOf(sender));

    private async void OnToggleFlagClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ToggleFlagCommand.ExecuteAsync(ItemOf(sender));

    private async void OnDeleteClick(object sender, RoutedEventArgs e) =>
        await ViewModel.DeleteCommand.ExecuteAsync(ItemOf(sender));

    private static MessageItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.Tag as MessageItemViewModel;

    // ================================================================ accounts

    private async void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AccountService is null)
        {
            await ShowMessageAsync("Modo de demostración", "Para agregar cuentas, ejecuta MyMail sin la variable MYMAIL_SAMPLE.");
            return;
        }

        var dialog = new AddAccountDialog(ViewModel.AccountService) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private async void OnRemoveAccountClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } account)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "¿Quitar esta cuenta?",
            Content = $"Se quitará {account.Email} de MyMail y se borrará su correo guardado en este equipo. El correo del servidor no se toca.",
            PrimaryButtonText = "Quitar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveSelectedAccountCommand.ExecuteAsync(null);
        }
    }

    private Task ShowMessageAsync(string title, string message) =>
        new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = message, CloseButtonText = "Aceptar" }
            .ShowAsync()
            .AsTask();

    // ================================================================ message body (WebView2)

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedBody))
        {
            await RenderBodyAsync();
        }
    }

    private async void OnShowRemoteContentClick(object sender, RoutedEventArgs e)
    {
        _allowRemoteContent = true;
        await RenderBodyAsync();
    }

    private async Task RenderBodyAsync()
    {
        var body = ViewModel.SelectedBody;
        if (body?.MessageId != _renderedMessageId)
        {
            _allowRemoteContent = false;
            _renderedMessageId = body?.MessageId;
        }

        RemoteContentInfo.IsOpen = body?.Html is { } html && !_allowRemoteContent && RemoteReference().IsMatch(html);

        await (_webViewReady ??= InitializeWebViewAsync());
        var page = body is null ? "" : BuildPage(body, _allowRemoteContent, RootGrid.ActualTheme == ElementTheme.Dark);
        var version = ++_renderVersion;
        await File.WriteAllTextAsync(Path.Combine(RenderFolder, "message.html"), page);
        if (version == _renderVersion)
        {
            BodyView.CoreWebView2.Navigate($"https://{RenderHost}/message.html?v={version}");
        }
    }

    private async Task InitializeWebViewAsync()
    {
        Directory.CreateDirectory(RenderFolder);
        await BodyView.EnsureCoreWebView2Async();
        var core = BodyView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(RenderHost, RenderFolder, CoreWebView2HostResourceAccessKind.Deny);

        // Mail is untrusted content: no scripts, no dialogs, no dev tools.
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        // Links open in the default browser, never inside the reading pane.
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith($"https://{RenderHost}/", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(parsed);
        }
    }

    private static string BuildPage(MailMessageBody body, bool allowRemote, bool dark)
    {
        var csp = allowRemote
            ? "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline' https: http:; font-src data: https:"
            : "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:";
        var pane = dark ? "#1F1F1F" : "#FFFFFF";
        var text = dark ? "#E6E6E6" : "#1A1A1A";
        const string font = "'Segoe UI Variable Text','Segoe UI',sans-serif";

        string content;
        string bodyStyle;
        if (body.Html is { } html)
        {
            // HTML mail is designed for a light page; in dark mode show it on a light card, like the original.
            content = html;
            bodyStyle = dark
                ? $"margin:0 24px 24px;padding:16px;border-radius:8px;background:#FFFFFF;color:#000000;font:15px/1.45 {font}"
                : $"margin:0 24px 24px;color:#000000;font:15px/1.45 {font}";
        }
        else
        {
            content = $"<div style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(body.Text)}</div>";
            bodyStyle = $"margin:0 24px 24px;color:{text};font:16px/1.45 {font}";
        }

        return $"""
            <!doctype html>
            <html><head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{csp}">
            <style>
              html {"{"} background:{pane}; color-scheme:{(dark ? "dark" : "light")}; {"}"}
              body {"{"} {bodyStyle}; overflow-wrap:anywhere; {"}"}
              img {"{"} max-width:100%; height:auto; {"}"}
            </style>
            </head><body>{content}</body></html>
            """;
    }

    [GeneratedRegex(@"(src|background)\s*=\s*[""']?\s*https?:|url\(\s*[""']?\s*https?:", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteReference();
}
