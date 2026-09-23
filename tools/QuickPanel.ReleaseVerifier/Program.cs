using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickPanel.Services;

Dictionary<string, string> options = ParseOptions(args);
string packagePath = RequiredPath(options, "package", requireFile: true);
string manifestPath = RequiredPath(options, "manifest", requireFile: true);
string checksumsPath = RequiredPath(options, "checksums", requireFile: true);
string previousPublish = RequiredPath(options, "previous-publish", requireFile: false);
string workRoot = RequiredPath(options, "work-root", requireFile: false);
string reportPath = RequiredPath(options, "report", requireFile: false);
Version expectedPreviousVersion = Version.Parse(RequiredValue(options, "previous-version"));
Version expectedTargetVersion = Version.Parse(RequiredValue(options, "target-version"));
string targetExecutableName = RequiredValue(options, "target-executable");
Need(targetExecutableName is "AIQuickPanel.exe" or "QuickPanel.exe",
    "Unsupported target executable name.");
string targetUpdaterName = Path.GetFileNameWithoutExtension(targetExecutableName) + ".Updater.exe";

Directory.CreateDirectory(workRoot);
string extractRoot = Path.Combine(workRoot, "extracted-" + expectedTargetVersion);
ExtractValidated(packagePath, extractRoot);
ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(extractRoot);

ReleaseManifest manifest = JsonSerializer.Deserialize<ReleaseManifest>(
    File.ReadAllText(manifestPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
    throw new InvalidDataException("version.json is empty or invalid.");
Need(manifest.Latest == expectedTargetVersion.ToString(3),
    "version.json latest does not match the target version.");
Need(Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? downloadUri) && downloadUri.Scheme == Uri.UriSchemeHttps,
    "version.json downloadUrl is not HTTPS.");
Need(string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl) ||
     (Uri.TryCreate(manifest.ReleaseNotesUrl, UriKind.Absolute, out Uri? notesUri) && notesUri.Scheme == Uri.UriSchemeHttps),
    "version.json releaseNotesUrl is not HTTPS.");

string packageHash = HashFile(packagePath);
Need(packageHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase),
    "version.json SHA-256 does not match the release ZIP.");
VerifyChecksums(checksumsPath, packagePath, manifestPath);

string executable = Path.Combine(extractRoot, targetExecutableName);
Need(File.Exists(executable), "Extracted release does not contain the target executable.");
Need(ReadProductVersion(executable) == expectedTargetVersion,
    "Extracted application executable has the wrong version.");
string updaterExecutable = Path.Combine(extractRoot, "UpdaterRuntime", targetUpdaterName);
Need(File.Exists(updaterExecutable), "Extracted release does not contain the dedicated updater executable.");
Need(ReadProductVersion(updaterExecutable) == expectedTargetVersion,
    "Extracted updater executable has the wrong version.");
Need(File.Exists(Path.Combine(extractRoot, "UpdaterRuntime", "coreclr.dll")) &&
     File.Exists(Path.Combine(extractRoot, "UpdaterRuntime", "hostfxr.dll")) &&
     File.Exists(Path.Combine(extractRoot, "UpdaterRuntime", "hostpolicy.dll")),
    "Dedicated updater runtime is not self-contained.");
Need(File.Exists(Path.Combine(extractRoot, "coreclr.dll")) &&
     File.Exists(Path.Combine(extractRoot, "hostfxr.dll")) &&
     File.Exists(Path.Combine(extractRoot, "hostpolicy.dll")),
    "Release payload is not a normal self-contained .NET Windows deployment.");
Need(File.Exists(Path.Combine(extractRoot, "Assets", "TabIcons", "globe.svg")) &&
     File.Exists(Path.Combine(extractRoot, "appsettings.json")) &&
     File.Exists(Path.Combine(extractRoot, ReleasePayloadPolicy.ApplicationManifestFileName)),
    "Release payload is missing required application resources.");
Need(!Directory.EnumerateFiles(extractRoot, "*.pdb", SearchOption.AllDirectories).Any(),
    "Release payload contains debug symbols.");

Version previousVersion = ReadProductVersion(Path.Combine(previousPublish, "AIQuickPanel.exe"));
Need(previousVersion == expectedPreviousVersion,
    $"Previous publish fixture is not the requested {expectedPreviousVersion} executable.");
string oldOnlyMarkerName = $"old-{expectedPreviousVersion}-only.marker";
File.WriteAllText(Path.Combine(previousPublish, oldOnlyMarkerName), "must be removed by update");
WriteApplicationManifest(previousPublish);
string rollbackPayload = Path.Combine(workRoot, $"rollback-{expectedPreviousVersion}");
CopyDirectory(previousPublish, rollbackPayload);

string canonicalData = Path.Combine(workRoot, "LocalAppData", "AIQuickPanel", "data");
SeedRealistic246Profile(canonicalData);
Dictionary<string, string> beforeInventory = Inventory(canonicalData);
ReleasePayloadPolicy.ReplaceApplicationFiles(extractRoot, previousPublish, canonicalData);
Dictionary<string, string> afterInventory = Inventory(canonicalData);
Need(InventoriesEqual(beforeInventory, afterInventory),
    $"The {expectedPreviousVersion} to {expectedTargetVersion} update changed canonical settings, profiles, WebView2 data, icons, sessions, startup, or updater state.");
Need(!File.Exists(Path.Combine(previousPublish, oldOnlyMarkerName)),
    $"The update retained an application file owned only by {expectedPreviousVersion}.");
Need(ReadProductVersion(Path.Combine(previousPublish, targetExecutableName)) == expectedTargetVersion,
    "The realistic update did not install the target executable.");

ReleasePayloadPolicy.ReplaceApplicationFiles(rollbackPayload, previousPublish, canonicalData);
Need(InventoriesEqual(beforeInventory, Inventory(canonicalData)),
    $"Rolling application files back to {expectedPreviousVersion} changed canonical user data.");
Need(ReadProductVersion(Path.Combine(previousPublish, "AIQuickPanel.exe")) == expectedPreviousVersion &&
     File.Exists(Path.Combine(previousPublish, oldOnlyMarkerName)),
    $"The rollback payload did not restore the complete {expectedPreviousVersion} application state.");

ReleasePayloadPolicy.ReplaceApplicationFiles(extractRoot, previousPublish, canonicalData);
Need(InventoriesEqual(beforeInventory, Inventory(canonicalData)),
    "Reapplying the target version after rollback changed canonical user data.");
Need(ReadProductVersion(Path.Combine(previousPublish, targetExecutableName)) == expectedTargetVersion &&
     !File.Exists(Path.Combine(previousPublish, oldOnlyMarkerName)),
    "The target payload could not be reapplied cleanly after rollback.");

string renamedCanonicalData = Path.Combine(workRoot, "LocalAppData", "QuickPanel", "data");
string renamedMaintenance = renamedCanonicalData + "-maintenance";
ProfileMigrationResult migration = new ProfileMigrationService().Migrate(new ProfileMigrationRequest(
    renamedCanonicalData,
    renamedMaintenance,
    [new ProfileMigrationCandidate("legacy-canonical-data", canonicalData, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(migration.Status == ProfileMigrationStatus.Migrated,
    "The old canonical profile did not migrate to the Quick Panel data directory.");
Dictionary<string, string> migratedInventory = Inventory(renamedCanonicalData);
Need(beforeInventory.All(pair => migratedInventory.TryGetValue(pair.Key, out string? hash) && hash == pair.Value),
    "The renamed canonical profile does not contain a byte-identical copy of every legacy profile file.");
Need(InventoriesEqual(beforeInventory, Inventory(canonicalData)),
    "Profile migration changed or removed the legacy recovery copy.");

var report = new
{
    schemaVersion = 1,
    verifiedAtUtc = DateTimeOffset.UtcNow,
    version = manifest.Latest,
    previousVersion = expectedPreviousVersion.ToString(3),
    packagePath,
    packageSha256 = packageHash,
    extractedPath = extractRoot,
    selfContained = true,
    payloadSafety = "pass",
    checksums = "pass",
    extractedResources = "pass",
    upgradePreservation = "pass",
    rollbackCycle = "pass",
    renamedProfileMigration = "pass",
    preservedFileCount = beforeInventory.Count,
    profileInventorySha256 = HashInventory(beforeInventory)
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? throw new InvalidOperationException("Could not resolve report directory."));
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine($"Release package, dedicated updater runtime, realistic {expectedPreviousVersion} to {expectedTargetVersion} preservation, and rollback/reapply verification pass.");

static Dictionary<string, string> ParseOptions(string[] args)
{
    Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Release verifier arguments must be --name value pairs.");
        }
        parsed[args[index][2..]] = args[index + 1];
    }
    return parsed;
}

static string RequiredPath(Dictionary<string, string> options, string name, bool requireFile)
{
    if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"Missing --{name}.");
    }
    string full = Path.GetFullPath(value);
    if (requireFile && !File.Exists(full))
    {
        throw new FileNotFoundException($"Required file was not found: {full}");
    }
    if (!requireFile && name == "previous-publish" && !Directory.Exists(full))
    {
        throw new DirectoryNotFoundException($"Previous publish was not found: {full}");
    }
    return full;
}

static string RequiredValue(Dictionary<string, string> options, string name)
{
    if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"Missing --{name}.");
    }
    return value;
}

static void ExtractValidated(string packagePath, string extractRoot)
{
    Directory.CreateDirectory(extractRoot);
    string prefix = Path.GetFullPath(extractRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
        string destination = Path.GetFullPath(Path.Combine(extractRoot, relative));
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release ZIP contains a path outside its extraction root.");
        }
        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destination);
            continue;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using Stream source = entry.Open();
        using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }
}

static void VerifyChecksums(string checksumsPath, string packagePath, string manifestPath)
{
    Dictionary<string, string> expected = File.ReadAllLines(checksumsPath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.OrdinalIgnoreCase);
    Need(expected.TryGetValue(Path.GetFileName(packagePath), out string? packageHash) &&
         packageHash.Equals(HashFile(packagePath), StringComparison.OrdinalIgnoreCase),
        "SHA256SUMS.txt does not verify the ZIP.");
    Need(expected.TryGetValue(Path.GetFileName(manifestPath), out string? manifestHash) &&
         manifestHash.Equals(HashFile(manifestPath), StringComparison.OrdinalIgnoreCase),
        "SHA256SUMS.txt does not verify version.json.");
}

static Version ReadProductVersion(string executable)
{
    if (!File.Exists(executable))
    {
        throw new FileNotFoundException("Versioned executable was not found.", executable);
    }
    FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
    foreach (string? candidate in new[] { info.ProductVersion, info.FileVersion })
    {
        string value = (candidate ?? string.Empty).Split('+', '-')[0];
        if (Version.TryParse(value, out Version? parsed))
        {
            return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        }
    }
    throw new InvalidDataException($"Executable has no parseable version metadata: {executable}");
}

static void WriteApplicationManifest(string root)
{
    string[] entries = Directory.EnumerateFileSystemEntries(root)
        .Select(Path.GetFileName)
        .Where(name => !string.IsNullOrWhiteSpace(name) &&
                       !name.Equals(ReleasePayloadPolicy.ApplicationManifestFileName, StringComparison.OrdinalIgnoreCase))
        .Append(ReleasePayloadPolicy.ApplicationManifestFileName)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray()!;
    File.WriteAllText(
        Path.Combine(root, ReleasePayloadPolicy.ApplicationManifestFileName),
        JsonSerializer.Serialize(new { schemaVersion = 1, entries }, new JsonSerializerOptions { WriteIndented = true }),
        new UTF8Encoding(false));
}

static void SeedRealistic246Profile(string dataRoot)
{
    Directory.CreateDirectory(dataRoot);
    File.WriteAllText(Path.Combine(dataRoot, "settings.json"), """
        {
          "CustomTabs": [
            { "Id": "custom:chatgpt-work", "Name": "ChatGPT Work", "Url": "https://chatgpt.com/", "BrowserProfileId": "qp-work", "BrowserProfileLabel": "Work", "IsPinned": true, "IsCustom": true },
            { "Id": "custom:manual", "Name": "Manual icon", "Url": "https://example.com/", "Icon": "reddit", "IsPinned": false, "IsCustom": true }
          ],
          "LastSelectedTabId": "custom:chatgpt-work",
          "TabOrder": [ "custom:manual", "custom:chatgpt-work" ],
          "TabZoomFactors": { "custom:chatgpt-work": 1.25, "custom:manual": 0.9 },
          "TabPinStates": { "custom:chatgpt-work": true },
          "StartWithWindows": true,
          "CheckForUpdatesOnStartup": true,
          "UpdateManifestUrl": "https://updates.example.invalid/version.json",
          "PanelWidth": 620.0,
          "PanelHeight": 760.0
        }
        """, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(dataRoot, "profiles.json"), """
        {
          "Profiles": [
            { "Id": "default", "Name": "Default", "IsDefault": true },
            { "Id": "qp-work", "Name": "Work", "IsDefault": false },
            { "Id": "qp-personal", "Name": "Personal", "IsDefault": false }
          ],
          "PendingDeletionProfileIds": []
        }
        """, new UTF8Encoding(false));

    WriteSentinel(dataRoot, "WebView2/EBWebView/Default/Network/Cookies", "default-cookie-session-sentinel");
    WriteSentinel(dataRoot, "WebView2/EBWebView/qp-work/Network/Cookies", "work-cookie-session-sentinel");
    WriteSentinel(dataRoot, "WebView2/EBWebView/qp-personal/Local Storage/leveldb/000003.log", "personal-local-storage-sentinel");
    WriteSentinel(dataRoot, "IconCache/Websites/index.json", "{\"schemaVersion\":1,\"entries\":{\"sentinel\":\"favicon.png\"}}");
    WriteSentinel(dataRoot, "IconCache/Websites/favicon.png", "cached-favicon-sentinel");
    WriteSentinel(dataRoot, "updater-state.json", "{\"lastCheckedVersion\":\"2.4.6\",\"rollbackAvailable\":true}");
    WriteSentinel(dataRoot, "startup-preference.sentinel", "enabled");
}

static void WriteSentinel(string root, string relativePath, string content)
{
    string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}

static void CopyDirectory(string source, string destination)
{
    foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    }
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: false);
    }
}

static Dictionary<string, string> Inventory(string root)
{
    return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(
            file => Path.GetRelativePath(root, file).Replace('\\', '/'),
            HashFile,
            StringComparer.OrdinalIgnoreCase);
}

static bool InventoriesEqual(Dictionary<string, string> left, Dictionary<string, string> right)
{
    return left.Count == right.Count && left.All(entry =>
        right.TryGetValue(entry.Key, out string? hash) && hash.Equals(entry.Value, StringComparison.OrdinalIgnoreCase));
}

static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string HashInventory(Dictionary<string, string> inventory)
{
    string serialized = string.Join('\n', inventory.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
        .Select(entry => entry.Key + "=" + entry.Value));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
}

static void Need(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class ReleaseManifest
{
    public string Latest { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string ReleaseNotesUrl { get; init; } = string.Empty;
}
