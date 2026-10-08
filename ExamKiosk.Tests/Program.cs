using ExamKiosk.Configuration;
using System.IO;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("valid restrictive profile loads", ValidProfileLoads),
    ("valid any-site profile loads", ValidAnySiteProfileLoads),
    ("project profile takes precedence over output copy", ProjectProfileTakesPrecedence),
    ("exit toggle can disable quitting", ExitToggleCanDisableQuitting),
    ("password requirement toggle can be disabled", ExitPasswordRequirementCanBeDisabled),
    ("HTTP start URL is accepted", HttpStartUrlIsAccepted),
    ("localhost dev URL with port and hash is accepted", LocalhostUrlWithPortAndHashIsAccepted),
    ("host list accepts full URL values with port", FullUrlHostWithPortIsAccepted),
    ("allow-any-site bypasses host restriction", MissingStartHostIsAllowed),
    ("null host list is rejected", NullHostListIsRejected),
    ("wildcard hosts are rejected", WildcardHostIsRejected),
    ("embedded URL credentials are rejected", EmbeddedCredentialsAreRejected),
    ("unknown profile versions are rejected", UnknownVersionIsRejected)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

Console.WriteLine($"Passed {tests.Length} profile validation tests.");

static void ValidProfileLoads()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "https://exam.example.edu/start",
        AllowedHosts = new[] { "EXAM.EXAMPLE.EDU." },
        ShowAddressBar = false,
        AllowDownloads = false,
        AllowDeveloperTools = false,
        AllowContextMenus = false,
        AllowBrowserShortcuts = false,
        AllowClipboardRead = false
    });

    if (profile.AllowedHosts.Length != 1 || profile.AllowedHosts[0] != "EXAM.EXAMPLE.EDU")
    {
        throw new InvalidOperationException("Allowed hosts were not normalized.");
    }

    if (profile.ShowAddressBar || profile.AllowDownloads || profile.AllowClipboardRead)
    {
        throw new InvalidOperationException("Restrictive profile options were not preserved.");
    }
}

static void ValidAnySiteProfileLoads()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "https://amazon.com",
        AllowedHosts = Array.Empty<string>(),
        AllowAnySite = true,
        ShowAddressBar = false,
        AllowDownloads = false,
        AllowDeveloperTools = false,
        AllowContextMenus = false,
        AllowBrowserShortcuts = false,
        AllowClipboardRead = false
    });

    if (!profile.AllowAnySite || profile.StartUrl != "https://amazon.com")
    {
        throw new InvalidOperationException("Any-site mode did not accept the public HTTPS landing page.");
    }
}

static void ProjectProfileTakesPrecedence()
{
    var root = Path.Combine(Path.GetTempPath(), $"ExamKiosk-{Guid.NewGuid():N}");
    var output = Path.Combine(root, "bin", "Debug", "net10.0-windows");
    Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(root, "ExamKiosk.csproj"), string.Empty);
    File.WriteAllText(Path.Combine(root, "kiosksettings.json"), "project profile");
    File.WriteAllText(Path.Combine(output, "kiosksettings.json"), "output copy");

    try
    {
        var resolved = KioskProfile.FindProjectProfilePath("kiosksettings.json", output);
        var expected = Path.Combine(root, "kiosksettings.json");

        if (!string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected project profile '{expected}', got '{resolved}'.");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void ExitToggleCanDisableQuitting()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "https://exam.example.edu",
        AllowedHosts = new[] { "exam.example.edu" },
        AllowExit = false
    });

    if (profile.AllowExit)
    {
        throw new InvalidOperationException("The exit toggle did not disable quitting as expected.");
    }
}

static void ExitPasswordRequirementCanBeDisabled()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "https://exam.example.edu",
        AllowedHosts = new[] { "exam.example.edu" },
        AllowExit = true,
        RequireExitPassword = false
    });

    if (profile.RequireExitPassword)
    {
        throw new InvalidOperationException("The password requirement toggle did not disable the exit password prompt as expected.");
    }
}

static void HttpStartUrlIsAccepted()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "http://exam.example.edu",
        AllowedHosts = new[] { "exam.example.edu" }
    });

    if (profile.StartUrl != "http://exam.example.edu")
    {
        throw new InvalidOperationException("HTTP start URLs should be accepted.");
    }
}

static void LocalhostUrlWithPortAndHashIsAccepted()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "http://127.0.0.1:4173/#features",
        AllowedHosts = new[] { "127.0.0.1" }
    });

    if (profile.StartUrl != "http://127.0.0.1:4173/#features")
    {
        throw new InvalidOperationException("Local development URLs with ports and hash fragments should be accepted.");
    }
}

static void FullUrlHostWithPortIsAccepted()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "http://127.0.0.1:4173/#features",
        AllowedHosts = new[] { "http://127.0.0.1:4173" },
        AllowAnySite = false
    });

    if (profile.AllowedHosts.Length != 1 || profile.AllowedHosts[0] != "127.0.0.1")
    {
        throw new InvalidOperationException("Full URL host values with a port should be normalized to a hostname.");
    }
}

static void MissingStartHostIsAllowed()
{
    var profile = LoadProfile(new
    {
        Version = 1,
        StartUrl = "https://exam.example.edu",
        AllowedHosts = Array.Empty<string>(),
        AllowAnySite = true,
        ShowAddressBar = true,
        AllowDownloads = false,
        AllowDeveloperTools = false,
        AllowContextMenus = false,
        AllowBrowserShortcuts = false,
        AllowClipboardRead = false
    });

    if (!profile.AllowAnySite || profile.StartUrl != "https://exam.example.edu")
    {
        throw new InvalidOperationException("Any-site mode did not load successfully.");
    }
}

static void WildcardHostIsRejected()
{
    AssertInvalid(new { Version = 1, StartUrl = "https://exam.example.edu", AllowedHosts = new[] { "exam.example.edu", "*.example.edu" } });
}

static void NullHostListIsRejected()
{
    AssertInvalid(new { Version = 1, StartUrl = "https://exam.example.edu", AllowedHosts = (string[]?)null });
}

static void EmbeddedCredentialsAreRejected()
{
    AssertInvalid(new { Version = 1, StartUrl = "https://student:secret@exam.example.edu", AllowedHosts = new[] { "exam.example.edu" } });
}

static void UnknownVersionIsRejected()
{
    AssertInvalid(new { Version = 2, StartUrl = "https://exam.example.edu", AllowedHosts = new[] { "exam.example.edu" } });
}

static KioskProfile LoadProfile(object value)
{
    var path = WriteProfile(value);

    try
    {
        return KioskProfile.Load(path);
    }
    finally
    {
        File.Delete(path);
    }
}

static void AssertInvalid(object value)
{
    var path = WriteProfile(value);

    try
    {
        try
        {
            KioskProfile.Load(path);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("Expected the profile to be rejected.");
    }
    finally
    {
        File.Delete(path);
    }
}

static string WriteProfile(object value)
{
    var path = Path.Combine(Path.GetTempPath(), $"ExamKiosk-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(value));
    return path;
}