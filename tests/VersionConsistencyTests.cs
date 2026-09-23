using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using QuickPanel.Services;

internal static class VersionConsistencyTests
{
    private const string ExpectedVersion = "2.5.0";

    [ModuleInitializer]
    internal static void Run()
    {
        string repositoryRoot = FindRepositoryRoot();
        VerifyCentralVersion(repositoryRoot);
        VerifyRuntimeVersion();
        VerifyReleaseMetadata(repositoryRoot);
        VerifyPublicDocumentationOrder(repositoryRoot);
        VerifyDiagnosticPrivacy();
    }

    private static void VerifyCentralVersion(string repositoryRoot)
    {
        XDocument props = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        XElement propertyGroup = props.Root?.Element("PropertyGroup")
            ?? throw new InvalidOperationException("Directory.Build.props has no PropertyGroup.");

        Need((string?)propertyGroup.Element("Version") == ExpectedVersion,
            "Central Version does not match the release version.");
        Need((string?)propertyGroup.Element("AssemblyVersion") == ExpectedVersion + ".0",
            "Central AssemblyVersion does not match the release version.");
        Need((string?)propertyGroup.Element("FileVersion") == ExpectedVersion + ".0",
            "Central FileVersion does not match the release version.");
        Need((string?)propertyGroup.Element("InformationalVersion") == ExpectedVersion,
            "Central InformationalVersion does not match the release version.");
        Need((string?)propertyGroup.Element("IncludeSourceRevisionInInformationalVersion") == "false",
            "The SDK can append a Git hash to the release informational version.");
    }

    private static void VerifyRuntimeVersion()
    {
        Assembly assembly = typeof(UpdateService).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Need(informational == ExpectedVersion,
            "The runtime informational version does not match Directory.Build.props.");
        Need(assembly.GetName().Version == new Version(2, 5, 0, 0),
            "The runtime assembly version does not match Directory.Build.props.");

        FileVersionInfo info = FileVersionInfo.GetVersionInfo(assembly.Location);
        Need(info.FileVersion?.StartsWith(ExpectedVersion + ".0", StringComparison.Ordinal) == true,
            "The runtime file version does not match Directory.Build.props.");
        Need(UpdateService.GetCurrentVersionText() == ExpectedVersion,
            "Updater and diagnostics version reporting does not match the release version.");
    }

    private static void VerifyReleaseMetadata(string repositoryRoot)
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "version.example.json")));
        JsonElement root = manifest.RootElement;
        Need(root.GetProperty("latest").GetString() == ExpectedVersion,
            "version.example.json latest does not match the release version.");
        Need(root.GetProperty("downloadUrl").GetString()?.Contains(
            "QuickPanel-" + ExpectedVersion + "-win-x64.zip", StringComparison.Ordinal) == true,
            "version.example.json does not use the release ZIP filename.");

        string changelog = File.ReadAllText(Path.Combine(repositoryRoot, "CHANGELOG.md"));
        Need(changelog.StartsWith("# Changelog", StringComparison.Ordinal) &&
             changelog.IndexOf("## " + ExpectedVersion, StringComparison.Ordinal) >= 0,
            "CHANGELOG.md has no current release section.");
    }

    private static void VerifyPublicDocumentationOrder(string repositoryRoot)
    {
        string readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        string[] requiredHeadings =
        {
            "## What is Quick Panel?",
            "## Screenshot or demo",
            "## Major features",
            "## Download and installation",
            "## Keyboard shortcut",
            "## Adding websites",
            "## Browser profiles",
            "## Window docking",
            "## Updates",
            "## Privacy and local data",
            "## Troubleshooting",
            "## Development information"
        };

        int previous = -1;
        foreach (string heading in requiredHeadings)
        {
            int current = readme.IndexOf(heading, StringComparison.Ordinal);
            Need(current > previous, $"README.md is missing or misorders '{heading}'.");
            previous = current;
        }
    }

    private static void VerifyDiagnosticPrivacy()
    {
        Need(DiagnosticPrivacy.FormatPageOrigin(
                "https://user:password@example.com:8443/private/path?access_token=secret#fragment") ==
             "https://example.com:8443",
            "Copied diagnostics can expose URL user information, path, query, or fragment data.");
        Need(DiagnosticPrivacy.FormatPageOrigin("file:///C:/private/path") == "None" &&
             DiagnosticPrivacy.FormatPageOrigin("not a URL") == "None",
            "Copied diagnostics accept a non-web or invalid page address.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                File.Exists(Path.Combine(directory.FullName, "QuickPanel.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Quick Panel repository root.");
    }

    private static void Need(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
