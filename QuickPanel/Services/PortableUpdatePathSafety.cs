using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickPanel.Services;

public static class PortableUpdatePathSafety
{
    private static readonly HashSet<string> UpdaterArtifactDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "data-maintenance",
        "update-backups",
        "update-staging",
        "migration-backups",
        "migration-logs",
        "QuickPanelUpdate"
    };

    public static void EnsureActiveInstallDirectorySafe(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        string install = NormalizeDirectory(installDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(install);
        string physicalInstall = PhysicalDirectoryResolver.ResolveExistingDirectory(install);
        string physicalTemporaryDirectory = PhysicalDirectoryResolver.ResolveExistingDirectory(Path.GetTempPath());
        if (IsSameOrDescendant(physicalInstall, physicalTemporaryDirectory) ||
            GetPathSegments(install).Any(IsUpdaterArtifactDirectoryName) ||
            GetPathSegments(physicalInstall).Any(IsUpdaterArtifactDirectoryName))
        {
            throw new InvalidOperationException(
                "Quick Panel was launched from an updater backup, staging, maintenance, or temporary folder. " +
                "Start the app from its real installation folder and try the update again. No files were changed.");
        }
    }

    internal static bool IsUpdaterArtifactOrTemporaryDirectory(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        string install = NormalizeDirectory(installDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(install);
        string physicalInstall = PhysicalDirectoryResolver.ResolveExistingDirectory(install);
        string physicalTemporaryDirectory = PhysicalDirectoryResolver.ResolveExistingDirectory(Path.GetTempPath());
        return IsSameOrDescendant(physicalInstall, physicalTemporaryDirectory) ||
            GetPathSegments(install).Any(IsUpdaterArtifactDirectoryName) ||
            GetPathSegments(physicalInstall).Any(IsUpdaterArtifactDirectoryName);
    }

    public static string ResolvePhysicalDataDirectory(string canonicalDataDirectory, string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        // The install alias remains under the ordinary strict policy. Only the
        // known canonical user-data alias is allowed to traverse reparse points.
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(installDirectory);
        string physicalDataDirectory = PhysicalDirectoryResolver.ResolveExistingDirectory(canonicalDataDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(physicalDataDirectory);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(installDirectory, physicalDataDirectory);
        return physicalDataDirectory;
    }

    public static void EnsureMaintenancePathSafe(
        string maintenanceDirectory,
        string installDirectory,
        string physicalDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDataDirectory);

        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(maintenanceDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(installDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(physicalDataDirectory);
        if (!ReleasePayloadPolicy.IsSafeInstallTarget(maintenanceDirectory, installDirectory) ||
            !ReleasePayloadPolicy.IsSafeInstallTarget(maintenanceDirectory, physicalDataDirectory))
        {
            throw new InvalidOperationException(
                "Updater maintenance storage must remain independent from the application install and persistent user-data directories.");
        }
    }

    public static void EnsureUpdateBackupDestinationSafe(
        string backupDirectory,
        string installDirectory,
        string physicalDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDataDirectory);

        string backup = NormalizeDirectory(backupDirectory);
        string physicalData = NormalizeDirectory(physicalDataDirectory);
        string maintenance = PortableDataPaths.GetMaintenanceDirectory(physicalData);
        string updateBackupRoot = PortableDataPaths.GetUpdateBackupDirectory(physicalData);

        EnsureMaintenancePathSafe(maintenance, installDirectory, physicalData);
        EnsureMaintenancePathSafe(updateBackupRoot, installDirectory, physicalData);
        EnsureMaintenancePathSafe(backup, installDirectory, physicalData);
        string? backupParent = Path.GetDirectoryName(backup);
        if (string.IsNullOrWhiteSpace(backupParent) ||
            !NormalizeDirectory(backupParent)
                .Equals(NormalizeDirectory(updateBackupRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "New updater backups must be direct children of the validated update-backup root.");
        }
    }

    public static void EnsureUpdateLogDestinationSafe(
        string logPath,
        string installDirectory,
        string physicalDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDataDirectory);

        string log = Path.GetFullPath(logPath);
        string physicalData = NormalizeDirectory(physicalDataDirectory);
        string maintenance = PortableDataPaths.GetMaintenanceDirectory(physicalData);
        string updateLogRoot = PortableDataPaths.GetUpdateLogDirectory(physicalData);

        EnsureMaintenancePathSafe(maintenance, installDirectory, physicalData);
        EnsureMaintenancePathSafe(updateLogRoot, installDirectory, physicalData);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(log);
        string? logParent = Path.GetDirectoryName(log);
        if (string.IsNullOrWhiteSpace(logParent) ||
            !NormalizeDirectory(logParent)
                .Equals(NormalizeDirectory(updateLogRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Durable updater logs must be direct children of the validated update-log root.");
        }
    }

    internal static void EnsureLegacyFailureLogDestinationSafe(
        string logPath,
        string reportedInstallDirectory,
        string physicalDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedInstallDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDataDirectory);

        if (!IsUpdaterArtifactOrTemporaryDirectory(reportedInstallDirectory))
        {
            throw new InvalidOperationException(
                "Legacy updater failure logging is allowed only for an updater artifact or temporary reported installation.");
        }
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(
            reportedInstallDirectory,
            requireManifest: false);

        string log = Path.GetFullPath(logPath);
        string physicalData = NormalizeDirectory(physicalDataDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(physicalData);
        string updateLogRoot = PortableDataPaths.GetUpdateLogDirectory(physicalData);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(updateLogRoot, physicalData);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(log);
        string? logParent = Path.GetDirectoryName(log);
        if (string.IsNullOrWhiteSpace(logParent) ||
            !NormalizeDirectory(logParent)
                .Equals(NormalizeDirectory(updateLogRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Durable legacy updater logs must be direct children of the validated update-log root.");
        }
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static IEnumerable<string> GetPathSegments(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsUpdaterArtifactDirectoryName(string segment)
    {
        return UpdaterArtifactDirectoryNames.Contains(segment) ||
            segment.StartsWith(".migration-staging-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        string normalizedCandidate = NormalizeDirectory(candidate);
        string normalizedParent = NormalizeDirectory(parent);
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
