using Microsoft.Web.WebView2.Core;
using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ExamKiosk.Configuration;
using ExamKiosk.Services;

namespace ExamKiosk;

public partial class MainWindow : Window
{
    private readonly WindowsDeviceControls deviceControls = new();
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer wifiStatusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private KioskProfile? profile;
    private FileSystemWatcher? settingsWatcher;
    private string? profilePath;
        private bool allowClose;
        private bool wifiStatusUpdatePending;
        private bool reloadPending;
        private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
            InitializeComponent();
            clockTimer.Tick += (_, _) => UpdateTime();
            wifiStatusTimer.Tick += async (_, _) => await UpdateWifiStatusAsync();
            hideTimer.Tick += (_, _) => HideTopBar();
            UpdateTime();
        clockTimer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            profilePath = KioskProfile.FindProjectProfilePath("kiosksettings.json")
                ?? Path.Combine(AppContext.BaseDirectory, "kiosksettings.json");
            profile = KioskProfile.Load(profilePath);
            InitializeProfileWatcher();
            await Browser.EnsureCoreWebView2Async();
            ApplyProfileToBrowser();
            Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
            Browser.CoreWebView2.FrameNavigationStarting += Browser_FrameNavigationStarting;
            Browser.CoreWebView2.LaunchingExternalUriScheme += Browser_LaunchingExternalUriScheme;
            Browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.WebResourceRequested += Browser_WebResourceRequested;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.DownloadStarting += Browser_DownloadStarting;
            Browser.CoreWebView2.PermissionRequested += Browser_PermissionRequested;
            InitializeDeviceControls();
            await UpdateWifiStatusAsync();
            wifiStatusTimer.Start();
            Navigate(profile.StartUrl);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Browser initialization failed: {exception.Message}";
        }
    }

    private void InitializeProfileWatcher()
    {
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(profilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        settingsWatcher?.Dispose();
        settingsWatcher = new FileSystemWatcher(directory, Path.GetFileName(profilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };

        settingsWatcher.Changed += (_, _) => ScheduleProfileReload();
        settingsWatcher.Created += (_, _) => ScheduleProfileReload();
        settingsWatcher.Deleted += (_, _) => ScheduleProfileReload();
        settingsWatcher.Renamed += (_, _) => ScheduleProfileReload();
    }

    private void ScheduleProfileReload()
    {
        if (reloadPending)
        {
            return;
        }

        reloadPending = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await ReloadProfileFromDiskAsync();
            }
            finally
            {
                reloadPending = false;
            }
        }));
    }

    private async Task ReloadProfileFromDiskAsync()
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
        {
            return;
        }

        try
        {
            var updated = KioskProfile.Load(profilePath);
            profile = updated;
            ApplyProfileToBrowser();
            StatusText.Text = "Profile updated live from the configurator.";

            if (Browser.CoreWebView2 is not null && !string.IsNullOrWhiteSpace(updated.StartUrl))
            {
                var currentSource = Browser.CoreWebView2.Source;
                if (string.IsNullOrWhiteSpace(currentSource) || !IsAllowed(currentSource) || !Uri.TryCreate(currentSource, UriKind.Absolute, out var currentUri) || currentUri.Host != new Uri(updated.StartUrl).Host)
                {
                    Browser.CoreWebView2.Stop();
                    Navigate(updated.StartUrl);
                }
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Live profile reload failed: {exception.Message}";
        }
    }

    private void ApplyProfileToBrowser()
    {
        if (profile is null)
        {
            return;
        }

        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = profile.AllowDeveloperTools;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = profile.AllowContextMenus;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = profile.AllowBrowserShortcuts;
        }

        AddressBox.Visibility = profile.ShowAddressBar ? Visibility.Visible : Visibility.Collapsed;
        AddressBox.Text = profile.StartUrl;
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAllowed(e.Uri))
        {
            e.Cancel = true;
            StatusText.Text = "Navigation blocked by this exam session's site policy.";
            return;
        }

        AddressBox.Text = e.Uri;
        StatusText.Text = $"Allowed site: {new Uri(e.Uri).Host}";
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (IsAllowed(e.Uri))
        {
            Navigate(e.Uri);
        }
        else
        {
            StatusText.Text = "A new window was blocked by this exam session's site policy.";
        }
    }

    private void Browser_FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsHttpUrl(e.Uri) && !IsAllowed(e.Uri))
        {
            e.Cancel = true;
            StatusText.Text = "A frame navigation was blocked by this exam session's site policy.";
        }
    }

    private void Browser_LaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        StatusText.Text = "External application links are blocked during this exam session.";
    }

    private void Browser_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (IsHttpUrl(e.Request.Uri) && !IsAllowed(e.Request.Uri))
        {
            e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
                null,
                403,
                "Blocked by exam session policy",
                "Content-Type: text/plain");
        }
    }

    private void Browser_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (profile?.AllowDownloads != true)
        {
            e.Cancel = true;
            StatusText.Text = "Downloads are disabled for this session.";
        }
    }

    private void Browser_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
        {
            e.State = profile?.AllowClipboardRead == true
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(AddressBox.Text);
            e.Handled = true;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Reload();
    }

    private void ControlsButton_Click(object sender, RoutedEventArgs e)
    {
        ControlsPopup.IsOpen = !ControlsPopup.IsOpen;
    }

    private void CloseControlsButton_Click(object sender, RoutedEventArgs e)
    {
        ControlsPopup.IsOpen = false;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ControlsPopup.IsOpen && !ControlsPopup.IsMouseOver && !ControlsButton.IsMouseOver)
        {
            ControlsPopup.IsOpen = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && profile?.AllowBrowserShortcuts != true)
        {
            e.Handled = true;
            StatusText.Text = "Refresh is disabled in kiosk mode.";
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!BrightnessSlider.IsEnabled)
        {
            return;
        }

        try
        {
            deviceControls.SetBrightness((byte)Math.Round(BrightnessSlider.Value));
            BrightnessStatusText.Text = $"{BrightnessSlider.Value:0}%";
        }
        catch (Exception exception)
        {
            BrightnessSlider.IsEnabled = false;
            BrightnessStatusText.Text = $"Could not change brightness: {exception.Message}";
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!VolumeSlider.IsEnabled)
        {
            return;
        }

        try
        {
            deviceControls.SetMasterVolume((float)(VolumeSlider.Value / 100));
            VolumeStatusText.Text = $"{VolumeSlider.Value:0}%";
        }
        catch (Exception exception)
        {
            VolumeSlider.IsEnabled = false;
            VolumeStatusText.Text = $"Could not change volume: {exception.Message}";
        }
    }

    private void UpdateTime()
    {
        TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    private async Task UpdateWifiStatusAsync()
    {
        if (wifiStatusUpdatePending)
        {
            return;
        }

        wifiStatusUpdatePending = true;

        try
        {
            WifiStatusText.Text = await Task.Run(deviceControls.GetWifiStatus);
        }
        catch (UnauthorizedAccessException)
        {
            WifiStatusText.Text = "Permission required to read Wi-Fi status";
        }
        catch
        {
            WifiStatusText.Text = "Wi-Fi status unavailable";
        }
        finally
        {
            wifiStatusUpdatePending = false;
        }
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        if (profile?.AllowExit != true)
        {
            StatusText.Text = "Exit is disabled for this session.";
            return;
        }

        ConfirmClose();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!allowClose)
        {
            if (profile?.AllowExit != true)
            {
                e.Cancel = true;
                StatusText.Text = "Exit is disabled for this session.";
                return;
            }

            e.Cancel = true;
            ConfirmClose();
        }
    }

    private void ConfirmClose()
    {
        var passwordWindow = new Window
        {
            Title = "Exit kiosk",
            Width = 360,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        var titleText = profile?.RequireExitPassword == true
            ? "Enter the exit password to end this session."
            : "Confirm that you want to exit this session.";

        var title = new TextBlock
        {
            Text = titleText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            Width = 220,
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = profile?.RequireExitPassword == true ? Visibility.Visible : Visibility.Collapsed
        };

        var buttonRow = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var exitButton = new System.Windows.Controls.Button
        {
            Content = "Exit",
            Width = 90,
            IsDefault = true
        };

        cancelButton.Click += (_, _) => passwordWindow.Close();
        exitButton.Click += (_, _) =>
        {
            if (profile?.RequireExitPassword != true)
            {
                passwordWindow.DialogResult = true;
                FinalizeSessionExit();
                return;
            }

            if (IsCorrectExitPassword(passwordBox.Password))
            {
                passwordWindow.DialogResult = true;
                FinalizeSessionExit();
                return;
            }

            StatusText.Text = "Incorrect exit password.";
            passwordBox.Clear();
            passwordBox.Focus();
        };

        passwordBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            if (profile?.RequireExitPassword != true)
            {
                passwordWindow.DialogResult = true;
                FinalizeSessionExit();
                return;
            }

            if (IsCorrectExitPassword(passwordBox.Password))
            {
                passwordWindow.DialogResult = true;
                FinalizeSessionExit();
                return;
            }

            StatusText.Text = "Incorrect exit password.";
            passwordBox.Clear();
            passwordBox.Focus();
        };

        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(exitButton);
        panel.Children.Add(title);
        panel.Children.Add(passwordBox);
        panel.Children.Add(buttonRow);
        passwordWindow.Content = panel;

        passwordWindow.ShowDialog();
    }

    private bool IsCorrectExitPassword(string enteredPassword)
    {
        var expectedPassword = profile?.ExitPassword ?? "examkiosk";
        return string.Equals(enteredPassword, expectedPassword, StringComparison.Ordinal);
    }

    private void FinalizeSessionExit()
    {
        if (App.IsExamDesktopChild)
        {
            if (!App.RequestAuthorizedExit())
            {
                StatusText.Text = "The exit request could not reach the session supervisor. The exam remains active.";
            }

            return;
        }

        clockTimer.Stop();
        wifiStatusTimer.Stop();
        allowClose = true;
        Close();
    }

    internal void PrepareAuthorizedApplicationShutdown()
    {
        clockTimer.Stop();
        wifiStatusTimer.Stop();
        allowClose = true;
    }

    private void Navigate(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            var candidates = new[]
            {
                $"http://{address}",
                $"https://{address}"
            };

            uri = null;
            foreach (var candidate in candidates)
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
                {
                    uri = parsed;
                    break;
                }
            }

            if (uri is null)
            {
                StatusText.Text = "Enter a valid HTTP or HTTPS address.";
                return;
            }
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            StatusText.Text = "Only HTTP and HTTPS addresses are allowed.";
            return;
        }

        if (!IsAllowed(uri.AbsoluteUri))
        {
            StatusText.Text = "That address is not allowed in this exam session.";
            return;
        }

        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.Stop();
        Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void InitializeDeviceControls()
    {
        try
        {
            var brightness = deviceControls.GetBrightness();

            if (brightness.HasValue)
            {
                BrightnessSlider.Value = brightness.Value;
                BrightnessSlider.IsEnabled = true;
                BrightnessStatusText.Text = $"{brightness.Value:0}%";
            }
            else
            {
                BrightnessStatusText.Text = "Brightness control is not available on this display.";
            }
        }
        catch (Exception exception)
        {
            BrightnessStatusText.Text = $"Brightness control is unavailable: {exception.Message}";
        }

        try
        {
            var volume = deviceControls.GetMasterVolume();

            if (volume.HasValue)
            {
                VolumeSlider.Value = volume.Value * 100;
                VolumeSlider.IsEnabled = true;
                VolumeStatusText.Text = $"{VolumeSlider.Value:0}%";
            }
            else
            {
                VolumeStatusText.Text = "No Windows audio output is available.";
            }
        }
        catch (Exception exception)
        {
            VolumeStatusText.Text = $"Audio control is unavailable: {exception.Message}";
        }
    }

    private bool IsAllowed(string address)
    {
        if (profile is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (profile.AllowAnySite)
        {
            return true;
        }

        var allowedHosts = profile.AllowedHosts
            .Select(KioskProfile.NormalizeHostEntry)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToArray();

        return allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || allowedHosts.Contains(uri.Authority, StringComparer.OrdinalIgnoreCase)
            || (!uri.IsDefaultPort && allowedHosts.Contains($"{uri.Host}:{uri.Port}", StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsHttpUrl(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void ShowTopBar()
    {
        hideTimer.Stop();
        TopBarTransform.Y = 0;
        RevealChromeButton.Visibility = Visibility.Collapsed;
    }

    private void HideTopBar()
    {
        TopBarTransform.Y = -TopBar.ActualHeight;
        RevealChromeButton.Visibility = Visibility.Visible;
    }

    private void TopBar_MouseEnter(object sender, MouseEventArgs e)
    {
        ShowTopBar();
    }

    private void TopBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!ControlsPopup.IsOpen)
        {
            hideTimer.Start();
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.GetPosition(this).Y < 5)
        {
            ShowTopBar();
        }
    }

    private void RevealChromeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowTopBar();
    }
}
