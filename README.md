# ExamKiosk

A Windows 10/11 WPF kiosk-browser prototype built with .NET 10 and WebView2.

## Run

Install the .NET 10 SDK and the Microsoft Edge WebView2 Runtime, then run from this directory:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run
```

If `dotnet` is on your `PATH`, `dotnet run` also works.

## Configure a profile

Launch the separate profile editor with:

```powershell
dotnet run --project .\ExamKiosk.Configurator\ExamKiosk.Configurator.csproj
```

It opens the project-root `kiosksettings.json`, including profiles that need correction. Use **Validate profile** before saving; invalid profiles are not written. Saved settings are applied live while ExamKiosk is running.

## Validate profiles

Run the profile validation checks with:

```powershell
dotnet run --project .\ExamKiosk.Tests\ExamKiosk.Tests.csproj
```

Edit `kiosksettings.json` with the configurator or directly, then save to apply the profile live. `StartUrl` must be an absolute HTTP or HTTPS URL, and its hostname must appear in `AllowedHosts` when `AllowAnySite` is false. Hosts are exact matches; wildcards are rejected, so include every required login, content, and CDN hostname.

Profile options:

| Field | Default | Effect |
| --- | --- | --- |
| `ShowAddressBar` | `false` | Show or hide the editable address field; the HTTPS host allowlist still applies. |
| `AllowDownloads` | `false` | Permit WebView2 downloads. |
| `AllowDeveloperTools` | `false` | Permit browser developer tools. |
| `AllowContextMenus` | `false` | Permit WebView2's default context menus. |
| `AllowBrowserShortcuts` | `false` | Permit WebView2 browser accelerator shortcuts. |
| `AllowClipboardRead` | `false` | Allow web content to request clipboard-read permission. |

Clipboard permission is browser-level only; it does not block OS clipboard shortcuts or clipboard writes globally. These options do not prevent Windows shortcuts or escape from the app.

## Included

- Maximized, borderless browser window with back, forward, reload, and address controls.
- HTTPS navigation, child frames, and HTTP(S) web resources restricted to exact hostnames in the session profile.
- New browser windows are redirected into the kiosk window only when the host is allowed; other popups are blocked.
- Downloads, browser developer tools, default context menus, and browser accelerator shortcuts are disabled.
- Session exit asks for confirmation, including normal window-close requests.
- Device panel can adjust supported built-in display brightness and Windows master output volume.
- Device panel displays current Wi-Fi connection status and local time without exposing network controls.

## Limitations

This prototype is an app-level kiosk, not a secure Windows lockdown. It does not block Windows-key shortcuts, Task Manager, user switching, external application launches, or administrative access. Those protections require managed Windows devices and OS-level configuration such as Assigned Access or Shell Launcher; this app does not claim to replace those controls.

Wi-Fi status is read-only and reports whether a wireless adapter is connected; it does not reveal the network name or allow network changes. Brightness control depends on the display exposing the Windows WMI brightness interface; external monitor support is not guaranteed. Volume changes the system-wide default output volume.

The exam profile is currently a local JSON file and is not signed or encrypted. Do not treat it as tamper-proof. This is an early prototype and is not suitable for high-stakes exams without additional security review, test coverage, deployment hardening, and recovery behavior.