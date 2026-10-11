using System.IO;
using System.Text.Json;

namespace ExamKiosk.Configuration;

public sealed class KioskProfile
{
    public int Version { get; init; } = 1;
    public string StartUrl { get; init; } = string.Empty;
    public string ExitPassword { get; init; } = "examkiosk";
    public string[] AllowedHosts { get; set; } = Array.Empty<string>();
    public bool AllowAnySite { get; init; } = true;
    public bool AllowExit { get; init; } = true;
    public bool RequireExitPassword { get; init; } = true;
    public bool ShowAddressBar { get; init; }
    public bool AllowDownloads { get; init; }
    public bool AllowDeveloperTools { get; init; }
    public bool AllowContextMenus { get; init; }
    public bool AllowBrowserShortcuts { get; init; }
    public bool AllowClipboardRead { get; init; }

    public static KioskProfile Load(string path)
    {
        var resolvedPath = path;

        if (!Path.IsPathRooted(path))
        {
            resolvedPath = FindProjectProfile(path) ?? Path.Combine(AppContext.BaseDirectory, path);
        }

        return Parse(File.ReadAllText(resolvedPath));
    }

    public static KioskProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<KioskProfile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("The kiosk profile is empty.");

        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidDataException($"Unsupported kiosk profile version: {Version}.");
        }

        if (string.IsNullOrWhiteSpace(StartUrl)
            || !Uri.TryCreate(StartUrl, UriKind.Absolute, out var startUri)
            || (startUri.Scheme != Uri.UriSchemeHttp && startUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(startUri.UserInfo))
        {
            throw new InvalidDataException("StartUrl must be an absolute HTTP or HTTPS URL without embedded credentials.");
        }

        if (string.IsNullOrWhiteSpace(ExitPassword))
        {
            throw new InvalidDataException("ExitPassword must not be empty.");
        }

        if (AllowedHosts is null)
        {
            throw new InvalidDataException("AllowedHosts must be an array of exact hostnames.");
        }

        var allowedHosts = AllowedHosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(NormalizeHostEntry)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!AllowAnySite && (allowedHosts.Length == 0 || !allowedHosts.Contains(startUri.Host, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("AllowedHosts must include the StartUrl hostname when site restrictions are enabled.");
        }

        if (allowedHosts.Any(host => host.Contains('*') || Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new InvalidDataException("AllowedHosts must contain valid exact hostnames; wildcards are not supported.");
        }

        AllowedHosts = allowedHosts;
    }

    public void ValidateForSecureSession()
    {
        Validate();

        if (!AllowExit || !RequireExitPassword)
        {
            throw new InvalidDataException("Secure sessions require an authorized, password-protected exit flow.");
        }

        if (ExitPassword.Length < 8
            || string.Equals(ExitPassword, "examkiosk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a non-default exit password with at least 8 characters before using this profile for an exam.");
        }
    }

    public static string NormalizeHostEntry(string hostValue)
    {
        if (string.IsNullOrWhiteSpace(hostValue))
        {
            return string.Empty;
        }

        var trimmed = hostValue.Trim();

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsedUrl) && !string.IsNullOrWhiteSpace(parsedUrl.Host))
            {
                return parsedUrl.Host.TrimEnd('.');
            }
        }

        var hostOnly = trimmed
            .TrimEnd('/')
            .Split(['/', '?', '#'], 2)[0]
            .Trim();

        if (hostOnly.Contains("://"))
        {
            if (Uri.TryCreate(hostOnly, UriKind.Absolute, out var parsedHost) && !string.IsNullOrWhiteSpace(parsedHost.Host))
            {
                return parsedHost.Host.TrimEnd('.');
            }
        }

        if (hostOnly.Contains(':'))
        {
            var parts = hostOnly.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out _))
            {
                return parts[0].TrimEnd('.');
            }
        }

        return hostOnly.TrimEnd('.');
    }

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Save(string path)
    {
        File.WriteAllText(path, ToJson() + Environment.NewLine);
    }

    public static string? FindProjectProfilePath(string fileName)
    {
        return FindProjectProfilePath(fileName, AppContext.BaseDirectory);
    }

    public static string? FindProjectProfilePath(string fileName, string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExamKiosk.csproj")))
            {
                var projectProfile = Path.Combine(directory.FullName, fileName);
                if (File.Exists(projectProfile))
                {
                    return projectProfile;
                }

                break;
            }
        }

        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindProjectProfile(string fileName)
    {
        return FindProjectProfilePath(fileName);
    }
}
