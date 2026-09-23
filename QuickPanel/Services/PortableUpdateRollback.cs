using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace QuickPanel.Services;

public static class PortableUpdateRollback
{
    public static string? GetLatestBackupDirectory()
    {
        return GetLatestBackupDirectory(CancellationToken.None);
    }

    private static string? GetLatestBackupDirectory(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string install = GetInstallDirectory();
            string physicalData = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(
                PortableDataPaths.DataDirectory,
                install);
            return GetLatestBackupDirectory(GetBackupRoots(
                PortableDataPaths.DataDirectory,
                physicalData,
                install,
                Path.Combine(PortableDataPaths.LegacyProductDirectory, "data")),
                cancellationToken);
        }
        catch (Exception exception) when (IsExpectedPathException(exception))
        {
            return null;
        }
    }

    internal static string? GetLatestBackupDirectory(IEnumerable<string> roots)
    {
        return GetLatestBackupDirectory(roots, CancellationToken.None);
    }

    internal static string? GetLatestBackupDirectory(
        IEnumerable<string> roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var candidates = new List<string>();
        foreach (string root in roots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsValidBackup(directory, cancellationToken))
                    {
                        candidates.Add(directory);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedPathException(exception))
            {
                // An inaccessible legacy root is unavailable, not permission to relax validation.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? latest = candidates
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        cancellationToken.ThrowIfCancellationRequested();
        return latest;
    }

    public static string? GetLatestBackupVersion()
    {
        return GetLatestBackupVersion(CancellationToken.None);
    }

    public static string? GetLatestBackupVersion(CancellationToken cancellationToken)
    {
        string? directory = GetLatestBackupDirectory(cancellationToken);
        return directory == null ? null : GetBackupVersion(directory);
    }

    internal static string? GetLatestBackupVersion(IEnumerable<string> roots)
    {
        return GetLatestBackupVersion(roots, CancellationToken.None);
    }

    internal static string? GetLatestBackupVersion(
        IEnumerable<string> roots,
        CancellationToken cancellationToken)
    {
        string? directory = GetLatestBackupDirectory(roots, cancellationToken);
        return directory == null ? null : GetBackupVersion(directory);
    }

    internal static string? GetBackupVersion(string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        string executable;
        try
        {
            executable = PortableUpdateInstaller.ResolvePayloadWorkerExecutable(Path.GetFullPath(backupDirectory));
        }
        catch (InvalidDataException)
        {
            return null;
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
            foreach (string? candidate in new[] { info.ProductVersion, info.FileVersion })
            {
                if (UpdateService.TryParseSemanticVersion(candidate, out Version? version))
                {
                    return UpdateService.FormatVersion(version!);
                }
            }
            return null;
        }
        catch (Exception exception) when (IsExpectedPathException(exception))
        {
            return null;
        }
    }

    public static string PrepareAndLaunchLatest()
    {
        string applicationExecutable = PortableUpdateInstaller.ResolveActiveInstallExecutable(Environment.ProcessPath);
        string install = Path.GetDirectoryName(applicationExecutable)
            ?? throw new InvalidOperationException("The running Quick Panel executable has no installation directory.");
        string physicalData = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(
            PortableDataPaths.DataDirectory,
            install);
        string backup = GetLatestBackupDirectory(GetBackupRoots(
                PortableDataPaths.DataDirectory,
                physicalData,
                install,
                Path.Combine(PortableDataPaths.LegacyProductDirectory, "data")))
            ?? throw new InvalidOperationException("No rollback version is available yet.");
        string tempRoot = Path.Combine(Path.GetTempPath(), "QuickPanelRollback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string requestPath = Path.Combine(tempRoot, "rollback-request.json");
        string logPath = Path.Combine(tempRoot, "rollback.log");
        string workerDirectory = Path.Combine(tempRoot, "worker");
        ReleasePayloadPolicy.CopyApplicationFiles(install, workerDirectory, physicalData);
        ReleasePayloadPolicy.VerifyApplicationFilesCopy(install, workerDirectory, physicalData);
        PortableUpdateRequest request = CreateResolvedRequest(
            backup,
            install,
            physicalData,
            logPath,
            Environment.ProcessId,
            restartApplication: true,
            recoveryDirectory: workerDirectory);
        PortableUpdateWorker.WriteRequest(requestPath, request);
        PortableUpdateInstaller.StartWorker(
            PortableUpdateInstaller.ResolvePayloadWorkerExecutable(workerDirectory),
            requestPath,
            workerDirectory);
        return logPath;
    }

    internal static PortableUpdateRequest CreateRequest(
        string backupDirectory,
        string installDirectory,
        string canonicalDataDirectory,
        string logPath,
        int processId,
        bool restartApplication)
    {
        string install = Path.GetFullPath(installDirectory);
        string physicalData = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(
            canonicalDataDirectory,
            install);
        return CreateResolvedRequest(
            backupDirectory,
            install,
            physicalData,
            logPath,
            processId,
            restartApplication,
            recoveryDirectory: null);
    }

    private static PortableUpdateRequest CreateResolvedRequest(
        string backupDirectory,
        string installDirectory,
        string physicalDataDirectory,
        string logPath,
        int processId,
        bool restartApplication,
        string? recoveryDirectory)
    {
        string backup = Path.GetFullPath(backupDirectory);
        string install = Path.GetFullPath(installDirectory);
        string physicalData = Path.GetFullPath(physicalDataDirectory);
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(install, requireManifest: false);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(install, physicalData);
        PortableUpdatePathSafety.EnsureMaintenancePathSafe(
            PortableDataPaths.GetMaintenanceDirectory(physicalData),
            install,
            physicalData);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(backup);
        return new PortableUpdateRequest(
            "rollback",
            processId,
            backup,
            install,
            physicalData,
            PortableUpdateInstaller.ResolvePayloadWorkerExecutable(install),
            null,
            null,
            Path.GetFullPath(logPath),
            restartApplication,
            recoveryDirectory is null ? null : Path.GetFullPath(recoveryDirectory));
    }

    internal static IReadOnlyList<string> GetBackupRoots(
        string canonicalDataDirectory,
        string physicalDataDirectory,
        string installDirectory,
        string? legacyCanonicalDataDirectory = null)
    {
        PortableUpdatePathSafety.EnsureMaintenancePathSafe(
            PortableDataPaths.GetMaintenanceDirectory(physicalDataDirectory),
            installDirectory,
            physicalDataDirectory);
        var roots = new List<string>
        {
            PortableDataPaths.GetUpdateBackupDirectory(physicalDataDirectory),
            PortableDataPaths.GetLegacyUpdateBackupDirectory(
                canonicalDataDirectory,
                physicalDataDirectory)
        };

        if (!string.IsNullOrWhiteSpace(legacyCanonicalDataDirectory) &&
            Directory.Exists(legacyCanonicalDataDirectory) &&
            !PathsEqual(legacyCanonicalDataDirectory, canonicalDataDirectory))
        {
            try
            {
                string legacyPhysicalData = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(
                    legacyCanonicalDataDirectory,
                    installDirectory);
                PortableUpdatePathSafety.EnsureMaintenancePathSafe(
                    PortableDataPaths.GetMaintenanceDirectory(legacyPhysicalData),
                    installDirectory,
                    legacyPhysicalData);
                roots.Add(PortableDataPaths.GetUpdateBackupDirectory(legacyPhysicalData));
                roots.Add(PortableDataPaths.GetLegacyUpdateBackupDirectory(
                    legacyCanonicalDataDirectory,
                    legacyPhysicalData));
            }
            catch (Exception exception) when (IsExpectedPathException(exception))
            {
                // An unsafe or inaccessible retained legacy tree is ignored. Current-profile backups remain usable.
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool PathsEqual(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsValidBackup(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(Path.Combine(directory, PortableUpdateWorker.InvalidBackupMarkerFileName)))
        {
            return false;
        }
        try
        {
            _ = PortableUpdateInstaller.ResolvePayloadWorkerExecutable(directory);
            ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(directory, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsExpectedPathException(exception))
        {
            return false;
        }
    }

    private static string GetInstallDirectory()
    {
        string executable = PortableUpdateInstaller.ResolveActiveInstallExecutable(Environment.ProcessPath);
        return Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("The running Quick Panel executable has no installation directory.");
    }

    private static bool IsExpectedPathException(Exception exception)
    {
        return exception is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or Win32Exception;
    }
}
