# ExamKiosk

ExamKiosk is a WPF/WebView2 exam browser prototype. When you launch the app, it immediately opens the exam in a supervised, separate Windows desktop. The profile controls the start page, permitted hosts, browser features, and password-authorized exit.

## Run

Install the .NET 10 SDK and Microsoft Edge WebView2 Runtime. Configure a valid `kiosksettings.json`, then run:

```powershell
dotnet run
```

The app enters its exam desktop at startup. A valid session profile must allow exit, require an exit password, and use a non-default password of at least eight characters. Use the Configurator to edit and validate the profile.

## Configure a profile

Launch the profile editor with:

```powershell
dotnet run --project .\ExamKiosk.Configurator\ExamKiosk.Configurator.csproj
```

It opens the project-root `kiosksettings.json`, including profiles that need correction. `StartUrl` must be an absolute HTTP or HTTPS URL, and its hostname must appear in `AllowedHosts` when `AllowAnySite` is false. Hosts are exact matches; wildcards are rejected, so include every required login, content, and CDN hostname.

Secure profiles require an enabled password-protected exit and a non-default password of at least eight characters. The current profile stores that password as readable JSON; protect the deployed file with Windows permissions.

| Field | Default | Effect |
| --- | --- | --- |
| `ShowAddressBar` | `false` | Show or hide the editable address field; the host allowlist still applies. |
| `AllowDownloads` | `false` | Permit WebView2 downloads. |
| `AllowDeveloperTools` | `false` | Permit browser developer tools. |
| `AllowContextMenus` | `false` | Permit WebView2's default context menus. |
| `AllowBrowserShortcuts` | `false` | Permit WebView2 browser accelerator shortcuts. |
| `AllowClipboardRead` | `false` | Allow web content to request clipboard-read permission. |

## Included controls

- Maximized, borderless WebView2 exam window.
- HTTPS navigation and web resources restricted to exact hostnames in the profile.
- Unapproved popups and external URI launches are blocked.
- Downloads, developer tools, context menus, and browser shortcuts are disabled by default.
- App-level keyboard hook suppresses common switching shortcuts while the exam desktop is active.
- Closing or crashing the browser UI causes the supervisor to reopen it. The normal app exit flow returns to the normal desktop after the configured password check.

## Security and deployment limits

This is app-level kiosk behavior. The app hook suppresses common shortcuts, including the Windows keys, only while the exam desktop is active. It cannot intercept Windows secure attention (Ctrl+Alt+Delete) or guarantee protection from other OS-level escape routes. The app does not configure Windows Keyboard Filter, replace the Windows shell, or install an elevated service, so it is not equivalent to Safe Exam Browser's Windows lockdown. Microsoft documents Windows-level options such as [Keyboard Filter](https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/) and [Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/); those require managed Windows configuration.

The repository currently does not produce a signed installer or school-wide provisioning package. Treat this as a test build, not a production exam kiosk.

Wi-Fi status is read-only. Brightness control depends on the display exposing the Windows WMI brightness interface; external monitor support is not guaranteed. Volume changes the system-wide default output volume.
