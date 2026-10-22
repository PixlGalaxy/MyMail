# MyMail

A C# rebuild of the Windows 10 **Mail** app, with the same look and feel and modern support for Outlook, Microsoft 365, Gmail and IMAP.

- **UI:** WinUI 3 (Windows App SDK 1.7) on .NET 9, with Mica and the original app's typography and colors.
- **Icons:** [Lucide](https://lucide.dev) (`lucide-static` font, ISC license).
- **Architecture:** MVVM (CommunityToolkit.Mvvm). Everything is async and cancellable, so the UI never freezes.

## Project layout
```
src/MyMail.Core     Models, service interfaces and ViewModels
src/MyMail.Storage  Local SQLite cache and secret encryption (DPAPI)
src/MyMail.Sync     Sync engine: IMAP/SMTP (MailKit) and Microsoft Graph; OAuth (MSAL + WAM, Google)
src/MyMail.App      WinUI 3 UI: main window, add-account dialog, themes and icons
tests/MyMail.Tests  xUnit tests
```

## Build and run
Requirements: .NET 9 SDK, Windows 10 1809 or later, and the WebView2 runtime.

```powershell
dotnet build src\MyMail.App\MyMail.App.csproj -p:Platform=x64
.\src\MyMail.App\bin\x64\Debug\net9.0-windows10.0.19041.0\MyMail.exe
```

To try the UI without any account, set `$env:MYMAIL_SAMPLE = "1"` before starting the app. It then uses in-memory demo data and no network.

Run the tests:
```powershell
dotnet test tests\MyMail.Tests\MyMail.Tests.csproj
```

## Accounts
| Provider | Sign-in | Protocol |
|---|---|---|
| Outlook.com, Microsoft 365 | MSAL with the Windows account broker (WAM), browser fallback | Microsoft Graph |
| Gmail, Google Workspace | OAuth in the system browser | IMAP/SMTP with XOAUTH2 |
| Yahoo, iCloud | App password | IMAP/SMTP |
| Any other server | Password; servers auto-detected (Thunderbird ISPDB) | IMAP/SMTP |

Microsoft accounts go through Graph rather than IMAP because many Microsoft 365 tenants disable IMAP and SMTP AUTH.

### OAuth setup (Outlook/Microsoft 365 and Gmail)
MyMail reads its OAuth client ids from `%LOCALAPPDATA%\MyMail\oauth.json`. You can also set them with the `MYMAIL_MS_CLIENT_ID`, `MYMAIL_GOOGLE_CLIENT_ID` and `MYMAIL_GOOGLE_CLIENT_SECRET` environment variables.

```json
{
  "microsoft": { "clientId": "00000000-0000-0000-0000-000000000000" },
  "google": { "clientId": "xxxx.apps.googleusercontent.com", "clientSecret": "GOCSPX-..." }
}
```

**Microsoft (Entra ID → App registrations → New registration)**
1. Supported account types: *Accounts in any organizational directory and personal Microsoft accounts*.
2. Under **Authentication**, add the *Mobile and desktop applications* platform with these redirect URIs:
   - `http://localhost`
   - `ms-appx-web://microsoft.aad.brokerplugin/<clientId>`

   On the same page, enable *Allow public client flows*.
3. Under **API permissions**, add these delegated Microsoft Graph permissions: `Mail.ReadWrite`, `Mail.Send`, `User.Read` and `offline_access`. In a business tenant where users can't consent to apps, an admin must click *Grant admin consent*.

**Google (Cloud Console)**
1. On the OAuth consent screen, add the `https://mail.google.com/` scope and add yourself as a test user.
2. Create an OAuth client ID of type *Desktop app*.

Secrets are never stored in plain text. IMAP passwords and Google tokens are encrypted with DPAPI. MSAL tokens are kept by WAM or stored in an encrypted cache.

## Status
What works:
- offline cache and background sync;
- read, flag, delete, compose, reply, forward and send;
- account setup for IMAP, Gmail and Microsoft.

The Gmail and Microsoft paths have not yet been tested against real accounts.

Planned next:
- unified inbox and Focused/Other;
- conversations and search;
- attachments and signatures;
- IMAP IDLE and toast notifications;
- the narrow single-pane layout.
