using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace QuickPanel.Services;

public static class ReleasePayloadPolicy
{
    public const string ApplicationManifestFileName = "application-files.json";

    private static readonly HashSet<string> ProtectedInstallEntryNamesSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "data",
        "webview2",
        "iconcache",
        "logs",
        "sessions",
        "user_data",
        "userdata",
        "user data",
        "appdata",
        "settings.json",
        "profiles.json",
        "external-apps.user.json",
        "performance.json",
        "cookies",
        "local storage",
        "indexeddb"
    };

    private static readonly HashSet<string> ForbiddenReleaseNames = new(ProtectedInstallEntryNamesSet, StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "testresults", "test-output", "migration-backups", "migration-logs",
        "update-backups", "tmp", "temp", ".env", "secrets.json", "launchsettings.json",
        "appsettings.development.json"
    };

    private static readonly HashSet<string> ForbiddenFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".pdb", ".pfx", ".p12", ".pem", ".key", ".snk"
    };

    private static readonly HashSet<string> LegacyApplicationEntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AIQuickPanel.exe", "AIQuickPanel.dll", "AIQuickPanel.deps.json", "AIQuickPanel.runtimeconfig.json",
        "QuickPanel.exe", "QuickPanel.dll", "QuickPanel.deps.json", "QuickPanel.runtimeconfig.json",
        "appsettings.json", "external-apps.json", "application-files.json", "runtimes", "win-x64", "Assets",
        "BlackSharp.Core.dll", "DiskInfoToolkit.dll", "HidSharp.dll", "libMonoPosixHelper.dll",
        "LibreHardwareMonitorLib.dll", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Core.xml",
        "Microsoft.Web.WebView2.WinForms.dll", "Microsoft.Web.WebView2.WinForms.xml",
        "Microsoft.Web.WebView2.Wpf.dll", "Microsoft.Web.WebView2.Wpf.xml", "Mono.Posix.NETStandard.dll",
        "MonoPosixHelper.dll", "RAMSPDToolkit-NDD.dll", "SharpVectors.Converters.Wpf.dll",
        "SharpVectors.Core.dll", "SharpVectors.Css.dll", "SharpVectors.Dom.dll", "SharpVectors.Model.dll",
        "SharpVectors.Rendering.Wpf.dll", "SharpVectors.Runtime.Wpf.dll", "System.CodeDom.dll",
        "System.IO.Ports.dll", "System.Management.dll", "System.Threading.AccessControl.dll", "WebView2Loader.dll",
        "CHANGELOG.md", "EXTERNAL-APPS.md", "QA.md", "README.md", "STARTUP.md",
        "THIRD-PARTY-NOTICES.md", "version.example.json"
    };

    private static readonly string[] RequiredLegacyIdentityEntries =
    [
        "AIQuickPanel.exe", "AIQuickPanel.dll", "AIQuickPanel.deps.json", "AIQuickPanel.runtimeconfig.json"
    ];

    public static IReadOnlyCollection<string> ProtectedInstallEntryNames => ProtectedInstallEntryNamesSet;

    public static bool IsSafeInstallTarget(string installDirectory, string canonicalDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || string.IsNullOrWhiteSpace(canonicalDataDirectory))
        {
            return false;
        }

        string install = NormalizeDirectoryIdentity(installDirectory);
        string data = NormalizeDirectoryIdentity(canonicalDataDirectory);
        return !IsSameOrDescendant(install, data) && !IsSameOrDescendant(data, install);
    }

    public static void EnsureSafeInstallTarget(string installDirectory, string canonicalDataDirectory)
    {
        EnsurePathContainsNoReparsePoints(installDirectory);
        EnsurePathContainsNoReparsePoints(canonicalDataDirectory);
        if (!IsSafeInstallTarget(installDirectory, canonicalDataDirectory))
        {
            throw new InvalidOperationException(
                "The application install directory must remain independent from Quick Panel's persistent user-data directory.");
        }
    }

    public static void EnsureApplicationInstallIdentity(string installDirectory, bool requireManifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        string install = Path.GetFullPath(installDirectory);
        EnsurePathContainsNoReparsePoints(install);
        if (!Directory.Exists(install))
        {
            throw new DirectoryNotFoundException($"Application install directory was not found: {install}");
        }
        EnsureTreeContainsNoReparsePoints(install);
        if (requireManifest && !File.Exists(Path.Combine(install, ApplicationManifestFileName)))
        {
            throw new InvalidDataException(
                "The active Quick Panel folder is missing application-files.json and cannot be updated safely.");
        }
        _ = GetOwnedInstallEntries(install, requireIdentity: true);
    }

    public static IReadOnlyList<string> FindForbiddenEntries(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        return relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && IsForbiddenRelativePath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void EnsurePayloadContainsNoProfile(string payloadDirectory)
    {
        EnsurePayloadContainsNoProfile(payloadDirectory, CancellationToken.None);
    }

    internal static void EnsurePayloadContainsNoProfile(
        string payloadDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.GetFullPath(payloadDirectory);
        EnsurePathContainsNoReparsePoints(root);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Release payload directory was not found: {root}");
        }
        EnsureTreeContainsNoReparsePoints(root, cancellationToken);

        var relativePaths = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            relativePaths.Add(Path.GetRelativePath(root, path));
        }
        IReadOnlyList<string> forbidden = FindForbiddenEntries(relativePaths);
        if (forbidden.Count > 0)
        {
            throw new InvalidDataException(
                $"Release payload contains persistent profile data: {string.Join(", ", forbidden)}");
        }

        IReadOnlyList<string> ownedEntries = ReadApplicationManifest(root);
        var actualEntries = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            actualEntries.Add(Path.GetFileName(path));
        }
        actualEntries.Sort(StringComparer.OrdinalIgnoreCase);
        if (!ownedEntries.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(actualEntries, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release payload entries do not match application-files.json.");
        }
    }

    public static void ReplaceApplicationFiles(
        string payloadDirectory,
        string installDirectory,
        string canonicalDataDirectory,
        bool requireExistingInstallIdentity = true)
    {
        EnsureSafeInstallTarget(installDirectory, canonicalDataDirectory);
        EnsurePayloadContainsNoProfile(payloadDirectory);

        string payload = Path.GetFullPath(payloadDirectory);
        string install = Path.GetFullPath(installDirectory);
        EnsurePathContainsNoReparsePoints(payload);
        EnsurePathContainsNoReparsePoints(install);
        if (Directory.Exists(install))
        {
            EnsureTreeContainsNoReparsePoints(install);
        }
        IReadOnlyList<string> payloadEntries = ReadApplicationManifest(payload);
        IReadOnlyList<string> installEntries = Directory.Exists(install) && Directory.EnumerateFileSystemEntries(install).Any()
            ? GetOwnedInstallEntries(install, requireExistingInstallIdentity)
            : [];

        Directory.CreateDirectory(install);
        foreach (string name in installEntries)
        {
            string entry = Path.Combine(install, name);
            EnsureDirectChild(entry, install);
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        foreach (string name in payloadEntries)
        {
            string entry = Path.Combine(payload, name);
            string destination = Path.Combine(install, name);
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, destination);
            }
            else
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Release payload cannot contain file reparse points.");
                }
                File.Copy(entry, destination, overwrite: true);
            }
        }
    }

    public static void CopyApplicationFiles(
        string sourceDirectory,
        string destinationDirectory,
        string canonicalDataDirectory)
    {
        EnsureSafeInstallTarget(sourceDirectory, canonicalDataDirectory);
        EnsureSafeInstallTarget(destinationDirectory, canonicalDataDirectory);
        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.GetFullPath(destinationDirectory);
        IReadOnlyList<string> ownedEntries = GetOwnedInstallEntries(source, requireIdentity: true);
        Directory.CreateDirectory(destination);
        EnsurePathContainsNoReparsePoints(destination);
        foreach (string name in ownedEntries)
        {
            string entry = Path.Combine(source, name);
            string target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target);
            }
            else
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Application backup cannot follow file reparse points.");
                }
                File.Copy(entry, target, overwrite: false);
            }
        }
        WriteApplicationManifest(destination);
    }

    public static void RestoreApplicationFiles(
        string recoveryDirectory,
        string attemptedPayloadDirectory,
        string installDirectory,
        string canonicalDataDirectory)
    {
        EnsureSafeInstallTarget(installDirectory, canonicalDataDirectory);
        EnsurePayloadContainsNoProfile(recoveryDirectory);
        EnsurePayloadContainsNoProfile(attemptedPayloadDirectory);

        string recovery = Path.GetFullPath(recoveryDirectory);
        string attemptedPayload = Path.GetFullPath(attemptedPayloadDirectory);
        string install = Path.GetFullPath(installDirectory);
        EnsurePathContainsNoReparsePoints(install);
        if (Directory.Exists(install))
        {
            EnsureTreeContainsNoReparsePoints(install);
        }

        IReadOnlyList<string> recoveryEntries = ReadApplicationManifest(recovery);
        IReadOnlyList<string> attemptedEntries = ReadApplicationManifest(attemptedPayload);
        Directory.CreateDirectory(install);
        foreach (string name in recoveryEntries.Concat(attemptedEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string entry = Path.Combine(install, name);
            EnsureDirectChild(entry, install);
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else if (File.Exists(entry))
            {
                File.Delete(entry);
            }
        }

        foreach (string name in recoveryEntries)
        {
            string entry = Path.Combine(recovery, name);
            string destination = Path.Combine(install, name);
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, destination);
            }
            else
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Application recovery cannot follow file reparse points.");
                }
                File.Copy(entry, destination, overwrite: true);
            }
        }
    }

    public static void VerifyApplicationFilesCopy(
        string sourceDirectory,
        string destinationDirectory,
        string canonicalDataDirectory)
    {
        EnsureSafeInstallTarget(sourceDirectory, canonicalDataDirectory);
        EnsureSafeInstallTarget(destinationDirectory, canonicalDataDirectory);
        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.GetFullPath(destinationDirectory);
        EnsureTreeContainsNoReparsePoints(source);
        EnsureTreeContainsNoReparsePoints(destination);

        IReadOnlyList<string> sourceEntries = GetOwnedInstallEntries(source, requireIdentity: true);
        IReadOnlyList<string> destinationEntries = GetOwnedInstallEntries(destination, requireIdentity: true);
        if (!sourceEntries.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(
                    destinationEntries.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Application copy manifest does not match its source.");
        }
        foreach (string name in sourceEntries)
        {
            if (name.Equals(ApplicationManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                // CopyApplicationFiles regenerates this manifest from the copied inventory.
                continue;
            }
            string sourceEntry = Path.Combine(source, name);
            string destinationEntry = Path.Combine(destination, name);
            if (Directory.Exists(sourceEntry))
            {
                if (!Directory.Exists(destinationEntry))
                {
                    throw new InvalidDataException("Application backup is missing a required directory.");
                }
                VerifyDirectoryCopy(sourceEntry, destinationEntry);
            }
            else
            {
                if (!File.Exists(destinationEntry) || !FilesEqual(sourceEntry, destinationEntry))
                {
                    throw new InvalidDataException("Application backup does not match a required file.");
                }
            }
        }
    }

    private static void WriteApplicationManifest(string root)
    {
        string[] entries = Directory.EnumerateFileSystemEntries(root)
            .Select(path => Path.GetFileName(path)!)
            .Where(name => !name.Equals(ApplicationManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Append(ApplicationManifestFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
        string json = JsonSerializer.Serialize(
            new { schemaVersion = 1, entries },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, ApplicationManifestFileName), json);
    }

    private static IReadOnlyList<string> GetOwnedInstallEntries(string installDirectory, bool requireIdentity)
    {
        string manifestPath = Path.Combine(installDirectory, ApplicationManifestFileName);
        if (File.Exists(manifestPath))
        {
            return ReadApplicationManifest(installDirectory);
        }

        string[] actualNames = Directory.EnumerateFileSystemEntries(installDirectory)
            .Select(Path.GetFileName)
            .ToArray()!;
        bool hasLegacyIdentity = RequiredLegacyIdentityEntries.All(name => File.Exists(Path.Combine(installDirectory, name)));
        string[] unknown = actualNames
            .Where(name => !IsProtectedInstallEntry(name) && !LegacyApplicationEntryNames.Contains(name))
            .ToArray();
        if ((requireIdentity && !hasLegacyIdentity) || unknown.Length > 0)
        {
            throw new InvalidDataException(
                "Refusing to update a shared or unrecognized folder. Move Quick Panel to its own install directory first.");
        }
        return actualNames.Where(name => LegacyApplicationEntryNames.Contains(name)).ToArray();
    }

    private static IReadOnlyList<string> ReadApplicationManifest(string root)
    {
        string path = Path.Combine(root, ApplicationManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("Application payload is missing application-files.json.");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Application file manifest is not valid JSON.", exception);
        }
        using (document)
        {
            JsonElement documentRoot = document.RootElement;
            if (documentRoot.ValueKind != JsonValueKind.Object ||
                !documentRoot.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
                !schemaVersion.TryGetInt32(out int version) || version != 1 ||
                !documentRoot.TryGetProperty("entries", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Application file manifest is invalid.");
            }

            string[] names = entries.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length != entries.GetArrayLength() ||
                names.Count(name => name.Equals("AIQuickPanel.exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("QuickPanel.exe", StringComparison.OrdinalIgnoreCase)) != 1 ||
                !names.Contains(ApplicationManifestFileName, StringComparer.OrdinalIgnoreCase) ||
                names.Any(name => name is "." or ".." || Path.GetFileName(name) != name || IsProtectedInstallEntry(name)))
            {
                throw new InvalidDataException("Application file manifest contains unsafe or incomplete entries.");
            }
            foreach (string name in names)
            {
                string entryPath = Path.Combine(root, name);
                if (!File.Exists(entryPath) && !Directory.Exists(entryPath))
                {
                    throw new InvalidDataException("Application file manifest references a missing entry.");
                }
            }
            return names;
        }
    }

    public static void EnsurePathContainsNoReparsePoints(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("A rooted path is required.");
        }

        string current = root;
        string remainder = full[root.Length..];
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Updater paths cannot traverse filesystem reparse points.");
            }
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(
        string root,
        CancellationToken cancellationToken = default)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Updater directories cannot contain filesystem reparse points.");
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Updater trees cannot contain filesystem reparse points.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static bool IsForbiddenRelativePath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(ForbiddenReleaseNames.Contains))
        {
            return true;
        }
        string fileName = segments.LastOrDefault() ?? string.Empty;
        string extension = Path.GetExtension(fileName);
        return ForbiddenFileExtensions.Contains(extension);
    }

    private static bool IsProtectedInstallEntry(string name)
    {
        return ProtectedInstallEntryNamesSet.Contains(name);
    }

    private static void CopyDirectory(string source, string destination)
    {
        DirectoryInfo sourceInfo = new(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Release payload cannot contain directory reparse points.");
        }

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            FileInfo fileInfo = new(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Release payload cannot contain file reparse points.");
            }

            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void VerifyDirectoryCopy(string source, string destination)
    {
        string[] sourceEntries = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] destinationEntries = Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!sourceEntries.SequenceEqual(destinationEntries, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Application backup directory inventory does not match its source.");
        }

        foreach (string relativePath in sourceEntries)
        {
            string sourceEntry = Path.Combine(source, relativePath);
            string destinationEntry = Path.Combine(destination, relativePath);
            bool sourceIsDirectory = Directory.Exists(sourceEntry);
            if (sourceIsDirectory != Directory.Exists(destinationEntry))
            {
                throw new InvalidDataException("Application backup entry type does not match its source.");
            }
            if (!sourceIsDirectory && !FilesEqual(sourceEntry, destinationEntry))
            {
                throw new InvalidDataException("Application backup file hash does not match its source.");
            }
        }
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        var left = new FileInfo(leftPath);
        var right = new FileInfo(rightPath);
        if (left.Length != right.Length)
        {
            return false;
        }
        using FileStream leftStream = File.OpenRead(leftPath);
        using FileStream rightStream = File.OpenRead(rightPath);
        byte[] leftHash = SHA256.HashData(leftStream);
        byte[] rightHash = SHA256.HashData(rightStream);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static void EnsureDirectChild(string path, string parent)
    {
        string normalizedParent = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(normalizedPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(NormalizeDirectory(parent), StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException("Application replacement resolved an entry outside the validated install directory.");
        }
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeDirectoryIdentity(string path)
    {
        string normalized = NormalizeDirectory(path);
        return Directory.Exists(normalized)
            ? PhysicalDirectoryResolver.ResolveExistingDirectory(normalized)
            : normalized;
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
