# MyMail

Reconstrucción en C# de la app **Correo** de Windows 10, con la misma estética y con soporte moderno para Outlook, Gmail e IMAP.

- **UI:** WinUI 3 (Windows App SDK 1.7) sobre .NET 9, con fondo Mica y la tipografía y los colores de la app original.
- **Iconos:** [Lucide](https://lucide.dev) (fuente `lucide-static`, licencia ISC).
- **Arquitectura:** MVVM (CommunityToolkit.Mvvm). Todo es async y cancelable, para que la interfaz nunca se bloquee.

## Estructura
```
src/MyMail.Core     Modelos, interfaces de servicio y ViewModels
src/MyMail.Storage  Caché local SQLite y cifrado de secretos (DPAPI)
src/MyMail.Sync     Sincronización: IMAP/SMTP (MailKit) y Microsoft Graph, OAuth (MSAL + WAM, Google)
src/MyMail.App      Interfaz WinUI 3: ventana principal, alta de cuentas, temas e iconos
tests/MyMail.Tests  Pruebas xUnit
docs/referencia     Cómo funcionan la app original y New Outlook, y el plan de reconstrucción
```

## Cuentas
- **Yahoo, iCloud y otros servidores IMAP:** funcionan sin configuración extra (Yahoo e iCloud piden una contraseña de aplicación).
- **Outlook.com, Microsoft 365 y Gmail:** necesitan registrar la app y guardar los identificadores en `%LOCALAPPDATA%\MyMail\oauth.json`. Los pasos están en [docs/referencia/05-cuentas-y-configuracion.md](docs/referencia/05-cuentas-y-configuracion.md).
- **Probar la interfaz sin cuentas:** `$env:MYMAIL_SAMPLE = "1"` antes de abrir la app.

## Compilar y ejecutar
Requisitos: .NET 9 SDK y Windows 10 1809 o posterior.

```powershell
dotnet build src\MyMail.App\MyMail.App.csproj -p:Platform=x64
.\src\MyMail.App\bin\x64\Debug\net9.0-windows10.0.19041.0\MyMail.exe
```

## Estado
Ya funciona:
- la caché sin conexión;
- la sincronización en segundo plano;
- leer, marcar, eliminar, redactar, responder, reenviar y enviar;
- el alta de cuentas IMAP, Gmail y Microsoft.

Gmail y Microsoft todavía no se han probado con cuentas reales. El detalle por fase está en [docs/referencia/06-plan-mymail.md](docs/referencia/06-plan-mymail.md).

```powershell
dotnet test tests\MyMail.Tests\MyMail.Tests.csproj
```
