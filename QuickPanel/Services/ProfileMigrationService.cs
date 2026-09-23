using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickPanel.Services;

public sealed class ProfileMigrationService
{
    public const string MarkerFileName = ".profile-migration-v1.json";
    public const string EstablishedMarkerFileName = ".profile-established-v1.json";

    private const long MinimumDurableCookieBytes = 24 * 1024;

    private static readonly string[] ProductRootProfileEntries =
    [
        "settings.json",
        "WebView2",
        "IconCache",
        "logs",
        "external-apps.user.json",
        "performance.json"
    ];

    private static readonly HashSet<string> DedicatedRootExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        MarkerFileName,
        EstablishedMarkerFileName,
        "migration-backups",
        "update-backups",
        "migration-logs",
        "data-maintenance",
        "AIQuickPanel.exe",
        "AIQuickPanel.dll",
        "AIQuickPanel.deps.json",
        "AIQuickPanel.runtimeconfig.json",
        "QuickPanel.exe",
        "QuickPanel.dll",
        "QuickPanel.deps.json",
        "QuickPanel.runtimeconfig.json"
    };

    private static readonly string[] ExcludedDedicatedExtensions =
    [
        ".exe", ".dll", ".pdb", ".zip", ".msi", ".msix", ".appx", ".cmd", ".bat", ".ps1"
    ];

    public ProfileAssessment Assess(string directory, bool isProductRoot = false)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            return new ProfileAssessment(root, false, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        string settingsPath = Path.Combine(root, "settings.json");
        long settingsBytes = File.Exists(settingsPath) ? new FileInfo(settingsPath).Length : 0;
        int settingsItemCount = File.Exists(settingsPath) ? CountDurableSettings(settingsPath) : 0;
        int auxiliaryStateCount = CountAuxiliaryState(root);

        string webViewRoot = Path.Combine(root, "WebView2");
        FileInfo[] webViewFiles = Directory.Exists(webViewRoot)
            ? Directory.EnumerateFiles(webViewRoot, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToArray()
            : [];
        FileInfo[] cookieFiles = webViewFiles
            .Where(file => file.Name.Equals("Cookies", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        int localStorageFiles = CountFilesUnderNamedDirectory(webViewRoot, "Local Storage");
        int indexedDbFiles = CountFilesUnderNamedDirectory(webViewRoot, "IndexedDB");
        bool meaningfulSettings = settingsItemCount > 0 || auxiliaryStateCount > 0;
        bool durableBrowserState = cookieFiles.Any(IsDurableCookieDatabase) ||
            HasDurableLocalStorageState(webViewFiles) ||
            indexedDbFiles > 0;
        int durableTier = meaningfulSettings && durableBrowserState
            ? 3
            : meaningfulSettings || durableBrowserState
                ? 2
                : settingsBytes > 0 || webViewFiles.Length > 0
                    ? 1
                    : 0;

        return new ProfileAssessment(
            root,
            true,
            durableTier,
            settingsItemCount,
            settingsBytes,
            cookieFiles.Length,
            cookieFiles.Sum(file => file.Length),
            localStorageFiles,
            indexedDbFiles,
            webViewFiles.Length,
            webViewFiles.Sum(file => file.Length),
            auxiliaryStateCount);
    }

    public ProfileMigrationResult Migrate(ProfileMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.LegacyCandidates);
        ArgumentNullException.ThrowIfNull(request.AccessCheck);

        string canonical = Path.GetFullPath(request.CanonicalDirectory);
        string workRoot = Path.GetFullPath(request.WorkRoot);
        string migrationMarkerPath = Path.Combine(canonical, MarkerFileName);
        string establishedMarkerPath = Path.Combine(workRoot, EstablishedMarkerFileName);
        if (File.Exists(migrationMarkerPath))
        {
            try
            {
                bool markerIsValid = IsValidMarker(migrationMarkerPath);
                if (markerIsValid && HasCanonicalProfileAnchor(canonical))
                {
                    return new ProfileMigrationResult(
                        ProfileMigrationStatus.AlreadyComplete,
                        null,
                        null,
                        "A valid profile migration marker and canonical profile are present.",
                        InspectionMode: ProfileMigrationInspectionMode.MarkerFastPath);
                }

                if (markerIsValid)
                {
                    return new ProfileMigrationResult(
                        ProfileMigrationStatus.Failed,
                        null,
                        canonical,
                        "The migration marker is valid but the canonical profile is incomplete. Recovery is required.",
                        InspectionMode: ProfileMigrationInspectionMode.MarkerFastPath);
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemOrDataException(exception))
            {
                // A marker read or parse failure is a cache miss. Deep assessment remains authoritative.
            }
        }
        else if (File.Exists(establishedMarkerPath))
        {
            try
            {
                if (IsValidEstablishedMarker(establishedMarkerPath, canonical) &&
                    HasCanonicalProfileAnchor(canonical))
                {
                    return new ProfileMigrationResult(
                        ProfileMigrationStatus.AlreadyComplete,
                        null,
                        null,
                        "A valid established-profile marker and canonical profile are present.",
                        InspectionMode: ProfileMigrationInspectionMode.MarkerFastPath);
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemOrDataException(exception))
            {
                // This marker is only a performance cache. Invalid cache data falls back to deep assessment.
            }
        }

        ProfileAssessment canonicalAssessment;
        List<(ProfileMigrationCandidate Candidate, ProfileAssessment Assessment)> candidates;
        try
        {
            canonicalAssessment = Assess(canonical);
            if (canonicalAssessment.DurableTier >= 2)
            {
                TryWriteEstablishedCanonicalMarker(workRoot, canonical, canonicalAssessment);
                return new ProfileMigrationResult(
                    ProfileMigrationStatus.CanonicalPreserved,
                    null,
                    null,
                    "The canonical profile is established and was preserved.");
            }

            candidates = request.LegacyCandidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Directory))
                .Select(candidate => candidate with { Directory = Path.GetFullPath(candidate.Directory) })
                .Where(candidate => !PathsEqual(candidate.Directory, canonical))
                .GroupBy(candidate => candidate.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(candidate => (candidate, Assess(candidate.Directory, candidate.IsProductRoot)))
                .Where(item => item.Item2.Exists && item.Item2.DurableTier >= 2)
                .ToList();
        }
        catch (Exception exception) when (IsExpectedFileSystemOrDataException(exception))
        {
            return Failed("Profile assessment could not be completed safely.", exception);
        }

        if (candidates.Count == 0)
        {
            return new ProfileMigrationResult(
                ProfileMigrationStatus.NoProfile,
                null,
                null,
                "No established legacy profile was found.");
        }

        int highestTier = candidates.Max(item => item.Assessment.DurableTier);
        List<(ProfileMigrationCandidate Candidate, ProfileAssessment Assessment)> strongest = candidates
            .Where(item => item.Assessment.DurableTier == highestTier)
            .ToList();
        if (strongest.Count != 1)
        {
            return new ProfileMigrationResult(
                ProfileMigrationStatus.Ambiguous,
                null,
                null,
                "Multiple legacy profiles contain comparably meaningful state. No profile was changed.",
                strongest.Select(item => item.Assessment).ToArray());
        }

        (ProfileMigrationCandidate candidate, ProfileAssessment sourceAssessment) = strongest[0];
        if (sourceAssessment.DurableTier <= canonicalAssessment.DurableTier)
        {
            return new ProfileMigrationResult(
                ProfileMigrationStatus.CanonicalPreserved,
                null,
                null,
                "The legacy profile was not clearly richer than canonical data.");
        }

        ProfileAccessCheck access;
        try
        {
            access = request.AccessCheck([canonical, candidate.Directory]);
        }
        catch (Exception exception)
        {
            return Failed("Exclusive profile access could not be established.", exception);
        }

        if (!access.IsSafe)
        {
            return new ProfileMigrationResult(
                ProfileMigrationStatus.Unsafe,
                candidate.Directory,
                null,
                string.IsNullOrWhiteSpace(access.Message)
                    ? "A profile is currently in use. No profile was changed."
                    : access.Message);
        }

        string? stagingDirectory = null;
        string? backupDirectory = null;
        bool canonicalMoved = false;
        bool canonicalActivated = false;
        try
        {
            ProfileAssessment guardedCanonical = Assess(canonical);
            ProfileAssessment guardedSource = Assess(candidate.Directory, candidate.IsProductRoot);
            if (guardedCanonical.DurableTier >= 2 ||
                guardedCanonical.DurableTier != canonicalAssessment.DurableTier ||
                guardedSource.DurableTier != sourceAssessment.DurableTier)
            {
                return new ProfileMigrationResult(
                    ProfileMigrationStatus.Unsafe,
                    candidate.Directory,
                    null,
                    "Profile state changed during migration assessment. No profile was changed.");
            }

            Directory.CreateDirectory(workRoot);
            stagingDirectory = Path.Combine(workRoot, $".migration-staging-{UniqueSuffix()}");
            ProfileManifest sourceManifest = CreateManifest(candidate.Directory, candidate.IsProductRoot);
            CopyProfile(candidate.Directory, stagingDirectory, candidate.IsProductRoot);
            ProfileManifest stagingManifest = CreateManifest(stagingDirectory, isProductRoot: false);
            VerifyManifest(sourceManifest, stagingManifest);

            ProfileAccessCheck finalAccess = request.AccessCheck([canonical, candidate.Directory]);
            if (!finalAccess.IsSafe)
            {
                return new ProfileMigrationResult(
                    ProfileMigrationStatus.Unsafe,
                    candidate.Directory,
                    stagingDirectory,
                    string.IsNullOrWhiteSpace(finalAccess.Message)
                        ? "Profile ownership changed during migration. No profile was activated."
                        : finalAccess.Message);
            }
            VerifyManifest(sourceManifest, CreateManifest(candidate.Directory, candidate.IsProductRoot));

            if (Directory.Exists(canonical))
            {
                string backupRoot = Path.Combine(workRoot, "migration-backups");
                Directory.CreateDirectory(backupRoot);
                backupDirectory = Path.Combine(backupRoot, $"canonical-{UniqueSuffix()}");
                Directory.Move(canonical, backupDirectory);
                canonicalMoved = true;
            }

            Directory.Move(stagingDirectory, canonical);
            stagingDirectory = null;
            canonicalActivated = true;
            VerifyManifest(sourceManifest, CreateManifest(canonical, isProductRoot: false));
            request.FaultInjection?.Invoke("before-marker-write");
            WriteMarker(canonical, candidate.Kind, sourceManifest.Files.Count);

            return new ProfileMigrationResult(
                ProfileMigrationStatus.Migrated,
                candidate.Directory,
                backupDirectory,
                "A richer legacy profile was copied and verified in the canonical data directory.");
        }
        catch (Exception exception) when (IsExpectedFileSystemOrDataException(exception))
        {
            string? failedActiveDirectory = null;
            if (canonicalActivated && Directory.Exists(canonical))
            {
                failedActiveDirectory = Path.Combine(workRoot, $"failed-canonical-{UniqueSuffix()}");
                try
                {
                    Directory.Move(canonical, failedActiveDirectory);
                }
                catch
                {
                    failedActiveDirectory = canonical;
                }
            }

            if (canonicalMoved && !Directory.Exists(canonical) && backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                try
                {
                    Directory.Move(backupDirectory, canonical);
                    backupDirectory = null;
                }
                catch
                {
                    // Keep the recovery directory untouched when automatic restoration cannot be proven safe.
                }
            }

            return new ProfileMigrationResult(
                ProfileMigrationStatus.Failed,
                candidate.Directory,
                failedActiveDirectory ?? backupDirectory ?? stagingDirectory,
                $"Profile migration stopped without deleting legacy data: {exception.GetType().Name}");
        }
    }

    private static int CountDurableSettings(string settingsPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("settings.json must contain a JSON object.");
        }

        int count = 0;
        foreach (string collectionName in new[] { "CustomTabs", "ClosedTabs", "TabOrder", "TabZoomFactors", "TabPinStates" })
        {
            if (document.RootElement.TryGetProperty(collectionName, out JsonElement value))
            {
                count += CountMeaningfulValue(value);
            }
        }

        foreach (string preferenceName in new[] { "LastSelectedTabId", "UpdateManifestUrl", "CodexReset", "TripMode" })
        {
            if (document.RootElement.TryGetProperty(preferenceName, out JsonElement value))
            {
                count += CountMeaningfulValue(value);
            }
        }

        count += CountCustomizedScalarPreference(document.RootElement, "StartWithWindows", JsonValueKind.False);
        count += CountCustomizedScalarPreference(document.RootElement, "AlwaysOnTop", JsonValueKind.False);
        count += CountCustomizedScalarPreference(document.RootElement, "PanelWidth", 500.0);
        count += CountCustomizedScalarPreference(document.RootElement, "PanelHeight", 650.0);
        count += CountCustomizedScalarPreference(document.RootElement, "LaunchCodexAutomatically", JsonValueKind.False);
        count += CountCustomizedScalarPreference(document.RootElement, "CheckForUpdatesOnStartup", JsonValueKind.True);

        return count;
    }

    private static int CountAuxiliaryState(string root)
    {
        int count = CountNonEmptyJsonCollection(Path.Combine(root, "external-apps.user.json"));
        string performancePath = Path.Combine(root, "performance.json");
        if (!File.Exists(performancePath))
        {
            return count;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(performancePath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("performance.json must contain a JSON object.");
        }
        if (document.RootElement.TryGetProperty("InactiveTabSleepMinutes", out JsonElement minutes) &&
            minutes.TryGetInt32(out int sleepMinutes) && sleepMinutes != 15)
        {
            count++;
        }
        if (document.RootElement.TryGetProperty("TabSleepOverridesMinutes", out JsonElement overrides))
        {
            count += CountMeaningfulValue(overrides);
        }
        return count;
    }

    private static int CountNonEmptyJsonCollection(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.GetArrayLength(),
            JsonValueKind.Object => document.RootElement.EnumerateObject().Count(),
            _ => throw new InvalidDataException("Auxiliary profile JSON must contain an object or array.")
        };
    }

    private static int CountMeaningfulValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => 0,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? 0 : 1,
            JsonValueKind.Array => value.EnumerateArray().Sum(CountMeaningfulValue),
            JsonValueKind.Object => value.EnumerateObject().Sum(property => CountMeaningfulValue(property.Value)),
            _ => 1
        };
    }

    private static int CountCustomizedScalarPreference(JsonElement root, string name, JsonValueKind freshDefault)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }
        return value.ValueKind == freshDefault ? 0 : 1;
    }

    private static int CountCustomizedScalarPreference(JsonElement root, string name, double freshDefault)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }
        return value.TryGetDouble(out double number) && Math.Abs(number - freshDefault) < 0.001 ? 0 : 1;
    }

    private static int CountFilesUnderNamedDirectory(string webViewRoot, string directoryName)
    {
        if (!Directory.Exists(webViewRoot))
        {
            return 0;
        }

        return Directory.EnumerateDirectories(webViewRoot, "*", SearchOption.AllDirectories)
            .Where(directory => Path.GetFileName(directory).Equals(directoryName, StringComparison.OrdinalIgnoreCase))
            .Sum(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count());
    }

    private static bool IsDurableCookieDatabase(FileInfo file)
    {
        if (file.Length >= MinimumDurableCookieBytes)
        {
            return true;
        }

        if (file.Length < 16)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == header.Length &&
            header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static bool HasDurableLocalStorageState(IEnumerable<FileInfo> webViewFiles)
    {
        return webViewFiles.Any(file =>
            IsUnderNamedDirectory(file.Directory, "Local Storage") &&
            (file.Extension.Equals(".ldb", StringComparison.OrdinalIgnoreCase) ||
             file.Extension.Equals(".sst", StringComparison.OrdinalIgnoreCase) ||
             (file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
              file.Length > 0)));
    }

    private static bool IsUnderNamedDirectory(DirectoryInfo? directory, string directoryName)
    {
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            if (current.Name.Equals(directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void CopyProfile(string sourceRoot, string destinationRoot, bool isProductRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string entry in GetCopyableTopLevelEntries(sourceRoot, isProductRoot))
        {
            string destination = Path.Combine(destinationRoot, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, destination);
            }
            else
            {
                CopyFile(entry, destination);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        DirectoryInfo sourceInfo = new(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Profile migration does not follow directory reparse points.");
        }

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        TryPreserveDirectoryTimestamps(sourceInfo, new DirectoryInfo(destination));
    }

    private static void CopyFile(string source, string destination)
    {
        FileInfo sourceInfo = new(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Profile migration does not follow file reparse points.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
        File.SetCreationTimeUtc(destination, sourceInfo.CreationTimeUtc);
    }

    private static IEnumerable<string> GetCopyableTopLevelEntries(string root, bool isProductRoot)
    {
        if (isProductRoot)
        {
            foreach (string name in ProductRootProfileEntries)
            {
                string path = Path.Combine(root, name);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    yield return path;
                }
            }

            yield break;
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(path);
            if (DedicatedRootExclusions.Contains(name) || name.StartsWith(".migration-staging-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(path) && ExcludedDedicatedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return path;
        }
    }

    private static bool IsValidMarker(string markerPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(markerPath));
        JsonElement root = document.RootElement;
        return IsValidMarkerDocument(root) &&
            root.TryGetProperty("sourceKind", out JsonElement sourceKind) &&
            sourceKind.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sourceKind.GetString()) &&
            root.TryGetProperty("verifiedFileCount", out JsonElement verifiedFileCount) &&
            verifiedFileCount.TryGetInt32(out int fileCount) &&
            fileCount > 0;
    }

    private static bool IsValidEstablishedMarker(string markerPath, string canonicalDirectory)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(markerPath));
        JsonElement root = document.RootElement;
        if (!IsValidMarkerDocument(root) ||
            !root.TryGetProperty("sourceKind", out JsonElement sourceKind) ||
            sourceKind.ValueKind != JsonValueKind.String ||
            !sourceKind.ValueEquals("canonical-established") ||
            !root.TryGetProperty("inspectedFileCount", out JsonElement inspectedFileCount) ||
            !inspectedFileCount.TryGetInt32(out int inspectedCount) ||
            inspectedCount <= 0 ||
            !root.TryGetProperty("canonicalPhysicalDirectory", out JsonElement markedPhysical) ||
            markedPhysical.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(markedPhysical.GetString()) ||
            !root.TryGetProperty("volumeSerialNumber", out JsonElement markedVolume) ||
            !markedVolume.TryGetUInt32(out uint volumeSerialNumber) ||
            !root.TryGetProperty("fileId", out JsonElement markedFileId) ||
            !markedFileId.TryGetUInt64(out ulong fileId) ||
            !root.TryGetProperty("canonicalDirectory", out JsonElement markedCanonical) ||
            markedCanonical.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(markedCanonical.GetString()) ||
            !PathsEqual(markedCanonical.GetString()!, canonicalDirectory))
        {
            return false;
        }

        PhysicalDirectoryIdentity currentIdentity =
            PhysicalDirectoryResolver.GetExistingDirectoryIdentity(canonicalDirectory);
        return PathsEqual(markedPhysical.GetString()!, currentIdentity.ResolvedPath) &&
            volumeSerialNumber == currentIdentity.VolumeSerialNumber &&
            fileId == currentIdentity.FileId &&
            HasCanonicalProfileAnchor(canonicalDirectory);
    }

    private static bool IsValidMarkerDocument(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("migrationVersion", out JsonElement version) &&
            version.TryGetInt32(out int migrationVersion) && migrationVersion == 1 &&
            root.TryGetProperty("completedAtUtc", out JsonElement completed) &&
            completed.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(completed.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }

    private static bool HasCanonicalProfileAnchor(string canonicalDirectory)
    {
        return File.Exists(Path.Combine(canonicalDirectory, "settings.json")) ||
            Directory.Exists(Path.Combine(canonicalDirectory, "WebView2")) ||
            File.Exists(Path.Combine(canonicalDirectory, "external-apps.user.json")) ||
            File.Exists(Path.Combine(canonicalDirectory, "performance.json"));
    }

    private static ProfileManifest CreateManifest(string root, bool isProductRoot)
    {
        List<ProfileManifestFile> files = [];
        foreach (string entry in GetCopyableTopLevelEntries(root, isProductRoot))
        {
            if (File.Exists(entry))
            {
                files.Add(CreateManifestFile(root, entry));
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)
                .Select(path => CreateManifestFile(root, path)));
        }

        return new ProfileManifest(files
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static ProfileManifestFile CreateManifestFile(string root, string path)
    {
        FileInfo file = new(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        return new ProfileManifestFile(
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            file.Length,
            hash);
    }

    private static void VerifyManifest(ProfileManifest expected, ProfileManifest actual)
    {
        if (expected.Files.Count != actual.Files.Count)
        {
            throw new InvalidDataException("Profile copy file count did not match its source.");
        }

        for (int index = 0; index < expected.Files.Count; index++)
        {
            ProfileManifestFile source = expected.Files[index];
            ProfileManifestFile copy = actual.Files[index];
            if (!source.RelativePath.Equals(copy.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                source.Length != copy.Length ||
                !source.Sha256.Equals(copy.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Profile copy failed byte-for-byte verification.");
            }
        }
    }

    private static void WriteMarker(string canonicalDirectory, string sourceKind, int verifiedFileCount)
    {
        string markerPath = Path.Combine(canonicalDirectory, MarkerFileName);
        string temporaryPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(new
        {
            migrationVersion = 1,
            completedAtUtc = DateTimeOffset.UtcNow,
            sourceKind,
            verifiedFileCount
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, markerPath);
    }

    private static void TryWriteEstablishedCanonicalMarker(
        string workRoot,
        string canonicalDirectory,
        ProfileAssessment assessment)
    {
        try
        {
            int inspectedFileCount = assessment.WebViewFileCount +
                (assessment.SettingsBytes > 0 ? 1 : 0) +
                assessment.AuxiliaryStateCount;
            PhysicalDirectoryIdentity identity =
                PhysicalDirectoryResolver.GetExistingDirectoryIdentity(canonicalDirectory);
            Directory.CreateDirectory(workRoot);
            string markerPath = Path.Combine(workRoot, EstablishedMarkerFileName);
            string temporaryPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    migrationVersion = 1,
                    completedAtUtc = DateTimeOffset.UtcNow,
                    sourceKind = "canonical-established",
                    canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalDirectory)),
                    canonicalPhysicalDirectory = identity.ResolvedPath,
                    volumeSerialNumber = identity.VolumeSerialNumber,
                    fileId = identity.FileId,
                    inspectedFileCount
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, markerPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemOrDataException(exception))
        {
            // This marker only caches a completed deep assessment. Failure to cache must not block startup.
        }
    }

    private static void TryPreserveDirectoryTimestamps(DirectoryInfo source, DirectoryInfo destination)
    {
        try
        {
            destination.CreationTimeUtc = source.CreationTimeUtc;
            destination.LastWriteTimeUtc = source.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            // Directory timestamps are best-effort; file bytes remain hash-verified.
        }
        catch (UnauthorizedAccessException)
        {
            // Directory timestamps are best-effort; file bytes remain hash-verified.
        }
    }

    private static string UniqueSuffix()
    {
        return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("N");
    }

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedFileSystemOrDataException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            ArgumentException or NotSupportedException or SecurityException;
    }

    private static ProfileMigrationResult Failed(string message, Exception exception)
    {
        return new ProfileMigrationResult(
            ProfileMigrationStatus.Failed,
            null,
            null,
            $"{message} ({exception.GetType().Name})");
    }

    private sealed record ProfileManifest(IReadOnlyList<ProfileManifestFile> Files);

    private sealed record ProfileManifestFile(string RelativePath, long Length, string Sha256);
}
