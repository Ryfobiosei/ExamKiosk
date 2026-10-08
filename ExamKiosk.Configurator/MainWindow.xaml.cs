using ExamKiosk.Configuration;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ExamKiosk.Configurator;

public partial class MainWindow : Window
{
    private string? profilePath;
    private bool isLoading;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var defaultProfile = FindDefaultProfile();

        if (defaultProfile is null)
        {
            ApplyDefaultNewProfile();
            SetStatus("Create a profile or open an existing JSON file.", false);
            return;
        }

        LoadProfile(defaultProfile);
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        profilePath = null;
        ApplyDefaultNewProfile();

        var projectProfilePath = KioskProfile.FindProjectProfilePath("kiosksettings.json");
        if (projectProfilePath is not null)
        {
            SaveProfile(projectProfilePath);
            profilePath = projectProfilePath;
        }

        PathText.Text = profilePath ?? "New profile";
        SetStatus("Enter a start URL and allowed hostnames.", false);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open exam profile",
            Filter = "ExamKiosk profiles (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadProfile(dialog.FileName);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (profilePath is null)
        {
            SaveAsButton_Click(sender, e);
            return;
        }

        SaveProfile(profilePath);
    }

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save exam profile",
            Filter = "ExamKiosk profiles (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = Path.GetFileName(profilePath ?? "kiosksettings.json")
        };

        if (dialog.ShowDialog(this) == true)
        {
            SaveProfile(dialog.FileName);
        }
    }

    private void AddStartHostButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(StartUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("Enter a valid HTTP or HTTPS start URL first.", true);
            return;
        }

        var hosts = GetHostLines();

        if (!hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(uri.Host);
            isLoading = true;
            AllowedHostsBox.Text = string.Join(Environment.NewLine, hosts);
            isLoading = false;
        }

        ValidateCurrentProfile();
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        ValidateCurrentProfile();
    }

    private void ProfileField_Changed(object sender, RoutedEventArgs e)
    {
        if (!isLoading)
        {
            SetStatus("Unsaved changes", false);
        }
    }

    private void ProfileField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!isLoading)
        {
            SetStatus("Unsaved changes", false);
        }
    }

    private void LoadProfile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<KioskProfile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("The profile file is empty.");

            isLoading = true;
            StartUrlBox.Text = profile.StartUrl;
            ExitPasswordBox.Password = profile.ExitPassword ?? "examkiosk";
            AllowedHostsBox.Text = string.Join(Environment.NewLine, profile.AllowedHosts ?? Array.Empty<string>());
            AllowAnySiteBox.IsChecked = profile.AllowAnySite;
            AllowExitBox.IsChecked = profile.AllowExit;
            RequireExitPasswordBox.IsChecked = profile.RequireExitPassword;
            ShowAddressBarBox.IsChecked = profile.ShowAddressBar;
            AllowDownloadsBox.IsChecked = profile.AllowDownloads;
            AllowDeveloperToolsBox.IsChecked = profile.AllowDeveloperTools;
            AllowContextMenusBox.IsChecked = profile.AllowContextMenus;
            AllowBrowserShortcutsBox.IsChecked = profile.AllowBrowserShortcuts;
            AllowClipboardReadBox.IsChecked = profile.AllowClipboardRead;
            isLoading = false;
            profilePath = path;
            PathText.Text = path;

            try
            {
                profile.Validate();
                SetStatus("Profile loaded and valid.", false);
            }
            catch (InvalidDataException exception)
            {
                SetStatus($"Loaded with validation issue: {exception.Message}", true);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            isLoading = false;
            SetStatus($"Could not open profile: {exception.Message}", true);
        }
    }

    private void SaveProfile(string path)
    {
        try
        {
            var profile = BuildProfile();
            profile.Save(path);
            profilePath = path;
            PathText.Text = path;
            SetStatus("Profile validated and saved. Changes are applied live in the kiosk app.", false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Not saved: {exception.Message}", true);
        }
    }

    private void ValidateCurrentProfile()
    {
        try
        {
            BuildProfile().Validate();
            SetStatus("Profile is valid and ready to save.", false);
        }
        catch (InvalidDataException exception)
        {
            SetStatus($"Validation issue: {exception.Message}", true);
        }
    }

    private KioskProfile BuildProfile()
    {
        return new KioskProfile
        {
            Version = 1,
            StartUrl = StartUrlBox.Text.Trim(),
            ExitPassword = ExitPasswordBox.Password.Trim(),
            AllowedHosts = GetHostLines().ToArray(),
            AllowAnySite = AllowAnySiteBox.IsChecked == true,
            AllowExit = AllowExitBox.IsChecked == true,
            RequireExitPassword = RequireExitPasswordBox.IsChecked == true,
            ShowAddressBar = ShowAddressBarBox.IsChecked == true,
            AllowDownloads = AllowDownloadsBox.IsChecked == true,
            AllowDeveloperTools = AllowDeveloperToolsBox.IsChecked == true,
            AllowContextMenus = AllowContextMenusBox.IsChecked == true,
            AllowBrowserShortcuts = AllowBrowserShortcutsBox.IsChecked == true,
            AllowClipboardRead = AllowClipboardReadBox.IsChecked == true
        };
    }

    private List<string> GetHostLines()
    {
        return AllowedHostsBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private string? FindDefaultProfile()
    {
        return KioskProfile.FindProjectProfilePath("kiosksettings.json");
    }

    private void ApplyDefaultNewProfile()
    {
        isLoading = true;
        StartUrlBox.Text = "https://www.safeexambrowser.org/start";
        ExitPasswordBox.Password = "examkiosk";
        AllowedHostsBox.Text = "www.safeexambrowser.org";
        AllowAnySiteBox.IsChecked = true;
        AllowExitBox.IsChecked = true;
        RequireExitPasswordBox.IsChecked = true;
        ShowAddressBarBox.IsChecked = false;
        AllowDownloadsBox.IsChecked = false;
        AllowDeveloperToolsBox.IsChecked = false;
        AllowContextMenusBox.IsChecked = false;
        AllowBrowserShortcutsBox.IsChecked = false;
        AllowClipboardReadBox.IsChecked = false;
        isLoading = false;
    }

    private void SetStatus(string message, bool isError)
    {
        if (StatusText is null)
        {
            return;
        }

        StatusText.Text = message;
        StatusText.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(181, 83, 73))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 117, 95));
    }
}
