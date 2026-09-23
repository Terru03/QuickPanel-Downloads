using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickPanel.Services;

internal static class LegacyUpdateInstallResolver
{
    internal static PortableUpdateRequest Resolve(
        PortableUpdateRequest request,
        IEnumerable<string> candidateExecutables)
    {
        return Resolve(request, candidateExecutables, request.CanonicalDataDirectory);
    }

    internal static PortableUpdateRequest Resolve(
        PortableUpdateRequest request,
        IEnumerable<string> candidateExecutables,
        string legacyCanonicalDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidateExecutables);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyCanonicalDataDirectory);

        if (!RequiresResolution(request))
        {
            return request;
        }

        string reportedInstall = Path.GetFullPath(request.InstallDirectory);
        string reportedExecutable = Path.GetFullPath(request.ApplicationExecutable);
        if (!reportedExecutable.Equals(
                Path.Combine(reportedInstall, "AIQuickPanel.exe"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Legacy updater request executable does not belong to its reported installation directory.");
        }
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(reportedInstall, requireManifest: false);

        string physicalData = Path.GetFullPath(request.CanonicalDataDirectory);
        var validated = new Dictionary<string, (string Install, string Executable)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidateExecutables)
        {
            if (!TryValidateCandidate(candidate, physicalData, out string? install, out string? executable))
            {
                continue;
            }

            string physicalInstall = PhysicalDirectoryResolver.ResolveExistingDirectory(install!);
            validated.TryAdd(physicalInstall, (install!, executable!));
        }

        if (validated.Count == 0)
        {
            throw new InvalidOperationException(
                "The legacy updater request points to a maintenance copy, but no validated stable Quick Panel installation was found. No files were changed.");
        }
        if (validated.Count > 1)
        {
            throw new InvalidOperationException(
                "The legacy updater request points to a maintenance copy, and more than one stable Quick Panel installation was found. No files were changed.");
        }

        (string resolvedInstall, string resolvedExecutable) = validated.Values.Single();
        string rollbackDirectory = ResolveRollbackDirectory(
            request,
            resolvedInstall,
            physicalData,
            legacyCanonicalDataDirectory);
        return request with
        {
            InstallDirectory = resolvedInstall,
            ApplicationExecutable = resolvedExecutable,
            RollbackDirectory = rollbackDirectory
        };
    }

    internal static PortableUpdateRequest ResolveFromRegisteredInstallations(PortableUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RequiresResolution(request))
        {
            return request;
        }
        return Resolve(
            request,
            StartupService.GetUpdateInstallCandidateExecutables(),
            PortableDataPaths.DataDirectory);
    }

    private static bool RequiresResolution(PortableUpdateRequest request)
    {
        return request.Operation.Equals("update", StringComparison.OrdinalIgnoreCase) &&
            PortableUpdatePathSafety.IsUpdaterArtifactOrTemporaryDirectory(request.InstallDirectory);
    }

    private static bool TryValidateCandidate(
        string? candidateExecutable,
        string physicalDataDirectory,
        out string? installDirectory,
        out string? executable)
    {
        installDirectory = null;
        executable = null;
        try
        {
            if (string.IsNullOrWhiteSpace(candidateExecutable))
            {
                return false;
            }

            string candidate = Path.GetFullPath(candidateExecutable);
            if (!Path.GetFileName(candidate).Equals("AIQuickPanel.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
            {
                return false;
            }

            string install = Path.GetDirectoryName(candidate)
                ?? throw new InvalidOperationException("Quick Panel installation candidate has no parent directory.");
            PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
            ReleasePayloadPolicy.EnsureApplicationInstallIdentity(install, requireManifest: true);
            ReleasePayloadPolicy.EnsureSafeInstallTarget(install, physicalDataDirectory);
            installDirectory = Path.GetFullPath(install);
            executable = Path.Combine(installDirectory, "AIQuickPanel.exe");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
            IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string ResolveRollbackDirectory(
        PortableUpdateRequest request,
        string resolvedInstallDirectory,
        string physicalDataDirectory,
        string legacyCanonicalDataDirectory)
    {
        string rollback = Path.GetFullPath(request.RollbackDirectory
            ?? throw new InvalidDataException("Legacy update request is missing its rollback directory."));
        try
        {
            PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(
                rollback,
                resolvedInstallDirectory,
                physicalDataDirectory);
            return rollback;
        }
        catch (InvalidOperationException)
        {
            // v2.4.5 wrote rollback backups under the product root. Accept only
            // that exact historical layout, then derive a new destination under
            // the current profile-independent maintenance root.
            string legacyCanonicalData = Path.GetFullPath(legacyCanonicalDataDirectory);
            string? legacyCanonicalProduct = Path.GetDirectoryName(legacyCanonicalData);
            if (string.IsNullOrWhiteSpace(legacyCanonicalProduct))
            {
                throw;
            }
            string legacyRequestRoot = Path.Combine(
                legacyCanonicalProduct,
                "update-backups");
            string legacyPhysicalRoot = PortableDataPaths.GetLegacyUpdateBackupDirectory(
                legacyCanonicalData,
                physicalDataDirectory);
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(legacyPhysicalRoot);
            string? rollbackParent = Path.GetDirectoryName(rollback);
            if (string.IsNullOrWhiteSpace(rollbackParent) ||
                !Path.TrimEndingDirectorySeparator(Path.GetFullPath(rollbackParent)).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRequestRoot)),
                    StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(rollback) ||
                File.Exists(rollback))
            {
                throw;
            }

            string currentRoot = PortableDataPaths.GetUpdateBackupDirectory(physicalDataDirectory);
            string name = Path.GetFileName(rollback);
            string derived = Path.Combine(currentRoot, name);
            while (Directory.Exists(derived) || File.Exists(derived))
            {
                derived = Path.Combine(
                    currentRoot,
                    name + "-legacy-" + Guid.NewGuid().ToString("N"));
            }
            PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(
                derived,
                resolvedInstallDirectory,
                physicalDataDirectory);
            return derived;
        }
    }
}
