using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickPanel.Services;

internal static class PortableUpdateJunctionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "QuickPanel.Tests",
            "update-junction-" + Guid.NewGuid().ToString("N"));
        var junctions = new List<string>();
        Directory.CreateDirectory(root);

        try
        {
            NormalDataDirectorySucceeds(root);
            FinalDataJunctionSucceeds(root, junctions);
            ParentProductJunctionSucceeds(root, junctions);
            JunctionChainSucceeds(root, junctions);
            EstablishedProfileCacheRejectsJunctionRetarget(root, junctions);
            PhysicalProfileInsideInstallIsRejected(root, junctions);
            InstallInsidePhysicalProfileIsRejected(root);
            FileWhereDirectoryIsRequiredIsRejected(root);
            BrokenJunctionIsRejected(root, junctions);
            JunctionLoopIsRejected(root, junctions);
            InvalidRollbackBackupCandidatesAreIgnored(root);
            RenamedProfileRetainsBridgeRollback(root);
            WorkerAndRollbackUseResolvedPhysicalData(root, junctions);
            Legacy245FinalDataJunctionRequestSucceeds(root, junctions);
            Legacy245ParentProductJunctionRequestSucceeds(root, junctions);
            MaintenancePathsAreValidated(root, junctions);
            UpdateBoundariesRejectUnsafeMaintenanceTopology(root);
        }
        finally
        {
            for (int index = junctions.Count - 1; index >= 0; index--)
            {
                RemoveJunction(junctions[index]);
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve a failed fixture for diagnosis rather than risk deleting through a link.
            }
        }
    }

    private static void NormalDataDirectorySucceeds(string root)
    {
        string install = CreateDirectory(root, "normal-install");
        string profile = CreateDirectory(root, "normal-profile");

        string resolved = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(profile, install);

        Need(PathsEqual(resolved, profile),
            "Updater did not accept a normal non-junction persistent data directory.");
        Need(PathsEqual(
                PortableDataPaths.GetLegacyUpdateBackupDirectory(profile, resolved),
                Path.Combine(Path.GetDirectoryName(profile)!, "update-backups")),
            "Normal-layout legacy backups were not resolved from the physical product directory.");
    }

    private static void FinalDataJunctionSucceeds(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "final-install");
        string profile = CreateDirectory(root, "final-physical-profile");
        string canonicalParent = CreateDirectory(root, "final-canonical");
        string canonicalData = Path.Combine(canonicalParent, "data");
        CreateJunction(canonicalData, profile, junctions);

        string resolved = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(canonicalData, install);

        Need(PathsEqual(resolved, profile),
            "Updater did not resolve a final data-directory junction to its physical profile.");
        Need(PathsEqual(
                PortableDataPaths.GetLegacyUpdateBackupDirectory(canonicalData, resolved),
                Path.Combine(canonicalParent, "update-backups")),
            "Final-data-junction legacy backups were not resolved from the canonical product directory.");
    }

    private static void ParentProductJunctionSucceeds(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "parent-install");
        string physicalProfile = CreateDirectory(root, "parent-physical-profile");
        string terminalDataAlias = Path.Combine(physicalProfile, "data");
        string canonicalProduct = Path.Combine(root, "parent-canonical-product");
        CreateJunction(terminalDataAlias, physicalProfile, junctions);
        CreateJunction(canonicalProduct, physicalProfile, junctions);
        string canonicalData = Path.Combine(canonicalProduct, "data");

        string resolved = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(canonicalData, install);

        Need(PathsEqual(resolved, physicalProfile),
            "Updater did not resolve the work-laptop parent/product-directory junction topology.");
        Need(PathsEqual(
                PortableDataPaths.GetLegacyUpdateBackupDirectory(canonicalData, resolved),
                Path.Combine(physicalProfile, "update-backups")),
            "Work-laptop legacy backups were not resolved from the physical product/profile directory.");
    }

    private static void JunctionChainSucceeds(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "chain-install");
        string physicalProfile = CreateDirectory(root, "chain-physical-profile");
        string second = Path.Combine(root, "chain-second");
        string first = Path.Combine(root, "chain-first");
        CreateJunction(second, physicalProfile, junctions);
        CreateJunction(first, second, junctions);

        string resolved = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(first, install);

        Need(PathsEqual(resolved, physicalProfile),
            "Updater did not follow a junction chain that Windows resolves normally.");
    }

    private static void EstablishedProfileCacheRejectsJunctionRetarget(
        string root,
        ICollection<string> junctions)
    {
        string canonicalProduct = CreateDirectory(root, "profile-cache-product");
        string canonicalData = Path.Combine(canonicalProduct, "data");
        string firstPhysicalProfile = CreateDirectory(root, "profile-cache-first");
        string secondPhysicalProfile = CreateDirectory(root, "profile-cache-second");
        string workRoot = CreateDirectory(root, "profile-cache-maintenance");
        File.WriteAllText(
            Path.Combine(firstPhysicalProfile, "settings.json"),
            "{\"CustomTabs\":[{\"Id\":\"first\"}]}");
        File.WriteAllText(
            Path.Combine(secondPhysicalProfile, "settings.json"),
            "{\"CustomTabs\":[{\"Id\":\"second\"}]}");

        CreateJunction(canonicalData, firstPhysicalProfile, junctions);
        var migrationService = new ProfileMigrationService();
        ProfileMigrationResult firstResult = migrationService.Migrate(new ProfileMigrationRequest(
            canonicalData,
            workRoot,
            [],
            _ => ProfileAccessCheck.Safe));
        Need(firstResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
            firstResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment,
            "The first physical profile was not deeply assessed before caching.");

        RemoveJunction(canonicalData);
        junctions.Remove(canonicalData);
        CreateJunction(canonicalData, secondPhysicalProfile, junctions);
        string secondProfileHash = HashDirectory(secondPhysicalProfile);
        ProfileMigrationResult retargetedResult = migrationService.Migrate(new ProfileMigrationRequest(
            canonicalData,
            workRoot,
            [],
            _ => ProfileAccessCheck.Safe));

        Need(retargetedResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
            retargetedResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment &&
            HashDirectory(secondPhysicalProfile) == secondProfileHash,
            "A profile cache survived a canonical junction retarget or changed the new target.");
    }

    private static void PhysicalProfileInsideInstallIsRejected(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "inside-install");
        string physicalProfile = CreateDirectory(install, "profile");
        string canonicalData = Path.Combine(root, "inside-canonical-data");
        CreateJunction(canonicalData, physicalProfile, junctions);

        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.ResolvePhysicalDataDirectory(canonicalData, install),
            "Updater accepted a physical profile inside the application install directory.");
    }

    private static void InstallInsidePhysicalProfileIsRejected(string root)
    {
        string physicalProfile = CreateDirectory(root, "contains-install-profile");
        string install = CreateDirectory(physicalProfile, "install");

        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.ResolvePhysicalDataDirectory(physicalProfile, install),
            "Updater accepted an application install inside the physical profile directory.");
    }

    private static void FileWhereDirectoryIsRequiredIsRejected(string root)
    {
        string install = CreateDirectory(root, "file-install");
        string file = Path.Combine(root, "profile-file");
        File.WriteAllText(file, "not a directory");

        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.ResolvePhysicalDataDirectory(file, install),
            "Updater accepted a file where the persistent data directory is required.");
    }

    private static void BrokenJunctionIsRejected(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "broken-install");
        string missingTarget = Path.Combine(root, "missing-profile-target");
        string broken = Path.Combine(root, "broken-canonical-data");
        CreateJunction(broken, missingTarget, junctions);

        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.ResolvePhysicalDataDirectory(broken, install),
            "Updater accepted a broken persistent-data junction.");
    }

    private static void JunctionLoopIsRejected(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "loop-install");
        string first = Path.Combine(root, "loop-first");
        string second = Path.Combine(root, "loop-second");
        CreateJunction(first, second, junctions);
        CreateJunction(second, first, junctions);

        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.ResolvePhysicalDataDirectory(first, install),
            "Updater accepted a persistent-data junction loop.");
    }

    private static void InvalidRollbackBackupCandidatesAreIgnored(string root)
    {
        string discoveryRoot = CreateDirectory(root, "rollback-discovery-root");
        string invalidBackup = CreateDirectory(discoveryRoot, "invalid-profile-bearing-backup");
        File.WriteAllText(Path.Combine(invalidBackup, "AIQuickPanel.exe"), "invalid-backup");
        Directory.CreateDirectory(Path.Combine(invalidBackup, "AIQuickPanel.exe.WebView2", "EBWebView", "Default"));
        File.WriteAllText(
            Path.Combine(invalidBackup, "AIQuickPanel.exe.WebView2", "EBWebView", "Default", "Cookies"),
            "profile-data");

        string validBackup = CreateDirectory(discoveryRoot, "valid-application-backup");
        WriteApplicationPayload(validBackup, "valid-backup", "valid-backup.dll");

        string hashInvalidBackup = CreateDirectory(discoveryRoot, "hash-invalid-backup");
        WriteApplicationPayload(hashInvalidBackup, "hash-invalid-backup", "hash-invalid-backup.dll");
        File.WriteAllText(
            Path.Combine(hashInvalidBackup, PortableUpdateWorker.InvalidBackupMarkerFileName),
            "Backup verification failed.");
        Directory.SetLastWriteTimeUtc(hashInvalidBackup, DateTime.UtcNow.AddMinutes(1));

        string? selected = PortableUpdateRollback.GetLatestBackupDirectory([discoveryRoot]);

        Need(selected != null && PathsEqual(selected, validBackup),
            "Rollback discovery did not ignore an invalid backup candidate.");
    }

    private static void RenamedProfileRetainsBridgeRollback(string root)
    {
        string scenario = CreateDirectory(root, "renamed-profile-rollback");
        string install = CreateDirectory(scenario, "install");
        string canonicalData = CreateDirectory(scenario, "QuickPanel-data");
        string legacyData = CreateDirectory(scenario, "AIQuickPanel-data");
        string backup = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(legacyData),
            "2.4.13-bridge");

        WriteApplicationPayload(install, "version-2.5.0", "new-binary.dll", "QuickPanel.exe");
        WriteApplicationPayload(backup, "version-2.4.13", "old-binary.dll", "AIQuickPanel.exe");
        File.WriteAllText(Path.Combine(canonicalData, "settings.json"), "{\"profile\":\"new\"}");
        File.WriteAllText(Path.Combine(legacyData, "settings.json"), "{\"profile\":\"legacy\"}");
        string canonicalHash = HashDirectory(canonicalData);
        string legacyHash = HashDirectory(legacyData);

        IReadOnlyList<string> roots = PortableUpdateRollback.GetBackupRoots(
            canonicalData,
            canonicalData,
            install,
            legacyData);
        string? discovered = PortableUpdateRollback.GetLatestBackupDirectory(roots);

        Need(discovered != null && PathsEqual(discovered, backup),
            "Rollback discovery lost the 2.4.13 bridge backup after the profile directory rename.");

        PortableUpdateRequest request = PortableUpdateRollback.CreateRequest(
            discovered!,
            install,
            canonicalData,
            Path.Combine(scenario, "rollback.log"),
            processId: 0,
            restartApplication: false);
        Need(PortableUpdateWorker.Apply(request) == 0,
            "Manual rollback could not restore the retained 2.4.13 bridge backup.");
        Need(File.Exists(Path.Combine(install, "AIQuickPanel.exe")) &&
             !File.Exists(Path.Combine(install, "QuickPanel.exe")),
            "Manual rollback did not restore the old executable identity.");
        Need(HashDirectory(canonicalData) == canonicalHash && HashDirectory(legacyData) == legacyHash,
            "Cross-name manual rollback changed canonical or retained legacy profile data.");
    }

    private static void WorkerAndRollbackUseResolvedPhysicalData(string root, ICollection<string> junctions)
    {
        string install = CreateDirectory(root, "worker-install");
        string physicalProduct = CreateDirectory(root, "worker-physical-product");
        string physicalProfile = CreateDirectory(physicalProduct, "data");
        string canonicalProduct = Path.Combine(root, "worker-canonical-product");
        CreateJunction(canonicalProduct, physicalProduct, junctions);
        string canonicalData = Path.Combine(canonicalProduct, "data");
        string payload = CreateDirectory(root, "worker-payload");
        string rollback = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(physicalProfile),
            "2.4.5-20260826-120000");

        File.WriteAllText(Path.Combine(physicalProfile, "settings.json"), "{\"preserve\":true}");
        File.WriteAllText(Path.Combine(physicalProfile, "profiles.json"), "[{\"id\":\"work\"}]");
        Directory.CreateDirectory(Path.Combine(physicalProfile, "WebView2", "EBWebView", "Default", "Network"));
        File.WriteAllBytes(Path.Combine(physicalProfile, "WebView2", "EBWebView", "Default", "Network", "Cookies"), [1, 2, 3, 4]);
        string profileHash = HashDirectory(physicalProfile);

        WriteApplicationPayload(install, "version-n", "old-binary.dll");
        WriteApplicationPayload(payload, "version-n-plus-one", "new-binary.dll");

        string physical = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(canonicalData, install);
        PortableUpdateRequest updateRequest = PortableUpdateInstaller.CreateUpdateRequest(
            processId: 0,
            payloadDirectory: payload,
            installDirectory: install,
            physicalDataDirectory: physical,
            applicationExecutable: Path.Combine(install, "AIQuickPanel.exe"),
            rollbackDirectory: rollback,
            downloadedZip: null,
            logPath: Path.Combine(root, "worker-update.log"),
            restartApplication: false);

        Need(PathsEqual(updateRequest.CanonicalDataDirectory, physicalProfile),
            "Update request did not carry the fully resolved physical profile path.");
        Need(PathsEqual(PortableDataPaths.GetMaintenanceDirectory(physicalProfile), physicalProfile + "-maintenance"),
            "Maintenance storage was not derived as a sibling of the physical profile.");
        Need(PathsEqual(
                PortableDataPaths.GetLegacyUpdateBackupDirectory(canonicalData, physicalProfile),
                Path.Combine(physicalProduct, "update-backups")),
            "Legacy update-backup discovery did not use the resolved physical product root.");

        PortableUpdateRequest aliasRequest = updateRequest with
        {
            CanonicalDataDirectory = canonicalData,
            RollbackDirectory = Path.Combine(PortableDataPaths.GetUpdateBackupDirectory(physicalProfile), "alias-rejected"),
            LogPath = Path.Combine(root, "worker-alias-rejected.log")
        };
        Need(PortableUpdateWorker.Apply(aliasRequest) == 1,
            "Worker accepted the canonical reparse alias instead of requiring the physical profile path.");
        Need(HashDirectory(physicalProfile) == profileHash,
            "Rejected worker alias request changed persistent profile bytes.");

        Need(PortableUpdateWorker.Apply(updateRequest) == 0,
            "Worker rejected the installer-provided physical profile path.");
        Need(File.ReadAllText(Path.Combine(install, "AIQuickPanel.exe")) == "version-n-plus-one",
            "Synthetic update did not replace the application executable.");
        Need(HashDirectory(physicalProfile) == profileHash,
            "Synthetic update changed persistent profile bytes.");
        Need(!Directory.EnumerateFileSystemEntries(rollback, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(rollback, path))
                .Any(path => ReleasePayloadPolicy.FindForbiddenEntries([path]).Count > 0),
            "Application rollback backup copied profile content.");

        PortableUpdateRequest rollbackRequest = PortableUpdateRollback.CreateRequest(
            backupDirectory: rollback,
            installDirectory: install,
            canonicalDataDirectory: canonicalData,
            logPath: Path.Combine(root, "worker-rollback.log"),
            processId: 0,
            restartApplication: false);
        Need(PathsEqual(rollbackRequest.CanonicalDataDirectory, physicalProfile),
            "Rollback request did not use the same resolved physical profile path as update.");
        Need(PortableUpdateWorker.Apply(rollbackRequest) == 0,
            "Rollback worker rejected the resolved physical profile path.");
        Need(File.ReadAllText(Path.Combine(install, "AIQuickPanel.exe")) == "version-n",
            "Synthetic rollback did not restore the prior application executable.");
        Need(HashDirectory(physicalProfile) == profileHash,
            "Synthetic rollback changed persistent profile bytes.");
    }

    private static void Legacy245FinalDataJunctionRequestSucceeds(
        string root,
        ICollection<string> junctions)
    {
        string physicalProfile = CreateDirectory(root, "legacy245-final-physical-profile");
        string canonicalProduct = CreateDirectory(root, "legacy245-final-canonical-product");
        string canonicalData = Path.Combine(canonicalProduct, "data");
        CreateJunction(canonicalData, physicalProfile, junctions);

        RunLegacy245JunctionRequest(
            root,
            "final-data-junction",
            canonicalData,
            physicalProfile);
    }

    private static void Legacy245ParentProductJunctionRequestSucceeds(
        string root,
        ICollection<string> junctions)
    {
        string physicalProfile = CreateDirectory(root, "legacy245-parent-physical-profile");
        string terminalDataAlias = Path.Combine(physicalProfile, "data");
        string canonicalProduct = Path.Combine(root, "legacy245-parent-canonical-product");
        CreateJunction(terminalDataAlias, physicalProfile, junctions);
        CreateJunction(canonicalProduct, physicalProfile, junctions);
        string canonicalData = Path.Combine(canonicalProduct, "data");

        RunLegacy245JunctionRequest(
            root,
            "parent-product-junction",
            canonicalData,
            physicalProfile);
    }

    private static void RunLegacy245JunctionRequest(
        string root,
        string scenario,
        string canonicalData,
        string physicalProfile)
    {
        string scenarioRoot = CreateDirectory(root, "legacy245-" + scenario);
        string stableInstall = CreateDirectory(scenarioRoot, "stable-install");
        string payload = CreateDirectory(scenarioRoot, "payload");
        string launchedBackup = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(physicalProfile),
            "before-2.4.5-" + scenario);
        string requestRoot = CreateDirectory(scenarioRoot, "request");
        string requestPath = Path.Combine(requestRoot, "update-request.json");
        string legacyRollback = Path.Combine(
            Path.GetDirectoryName(canonicalData)!,
            "update-backups",
            "2.4.5-" + scenario);

        Directory.CreateDirectory(physicalProfile);
        File.WriteAllText(Path.Combine(physicalProfile, "settings.json"), "{\"preserve\":true}");
        File.WriteAllText(Path.Combine(physicalProfile, "profiles.json"), "[{\"id\":\"work\"}]");
        WriteApplicationPayload(stableInstall, "legacy245-old", "legacy245-old.dll");
        WriteApplicationPayload(payload, "legacy245-new", "legacy245-new.dll");
        WriteApplicationPayload(launchedBackup, "legacy245-launched", "legacy245-launched.dll");

        string profileHash = HashDirectoryWithoutFollowingReparsePoints(physicalProfile);
        string launchedBackupHash = HashDirectory(launchedBackup);
        var request = new PortableUpdateRequest(
            "update",
            0,
            payload,
            launchedBackup,
            physicalProfile,
            Path.Combine(launchedBackup, "AIQuickPanel.exe"),
            legacyRollback,
            null,
            Path.Combine(requestRoot, "update.log"),
            RestartApplication: true);
        WriteLegacyUpdateRequest(requestPath, request);

        string? restartedExecutable = null;
        int exitCode = PortableUpdateWorker.ApplyRequestFile(
            requestPath,
            [Path.Combine(stableInstall, "AIQuickPanel.exe")],
            info =>
            {
                restartedExecutable = info.FileName;
                return null;
            },
            canonicalData);

        string currentBackupRoot = PortableDataPaths.GetUpdateBackupDirectory(physicalProfile);
        string[] backups = Directory.GetDirectories(currentBackupRoot);
        Need(exitCode == 0 &&
             File.ReadAllText(Path.Combine(stableInstall, "AIQuickPanel.exe")) == "legacy245-new" &&
             HashDirectoryWithoutFollowingReparsePoints(physicalProfile) == profileHash &&
             HashDirectory(launchedBackup) == launchedBackupHash &&
             !Directory.Exists(legacyRollback) &&
             backups.Length == 2 &&
             backups.Any(path =>
                 !PathsEqual(path, launchedBackup) &&
                 File.ReadAllText(Path.Combine(path, "AIQuickPanel.exe")) == "legacy245-old") &&
             PathsEqual(restartedExecutable!, Path.Combine(stableInstall, "AIQuickPanel.exe")) &&
             !File.ReadAllText(requestPath).Contains("RecoveryDirectory", StringComparison.Ordinal),
            "Exact v2.4.5 " + scenario +
            " request did not normalize its rollback and update/restart the stable install without changing profile or launched-backup bytes.");
    }

    private static void MaintenancePathsAreValidated(string root, ICollection<string> junctions)
    {
        string physicalProfile = CreateDirectory(root, "maintenance-overlap-profile");
        string maintenance = PortableDataPaths.GetMaintenanceDirectory(physicalProfile);
        Directory.CreateDirectory(maintenance);
        string independentInstall = CreateDirectory(root, "maintenance-independent-install");

        Need(PathsEqual(
                PortableDataPaths.GetValidatedMaintenanceDirectory(physicalProfile, independentInstall),
                maintenance),
            "Validated maintenance storage was not derived beside an existing physical profile.");

        string missingParent = CreateDirectory(root, "maintenance-missing-parent");
        string missingData = Path.Combine(missingParent, "data");
        Need(PathsEqual(
                PortableDataPaths.GetValidatedMaintenanceDirectory(missingData, independentInstall),
                missingData + "-maintenance"),
            "A normal first start could not derive maintenance storage through non-reparse ancestors.");

        string linkedParentTarget = CreateDirectory(root, "maintenance-linked-parent-target");
        string linkedParent = Path.Combine(root, "maintenance-linked-parent");
        CreateJunction(linkedParent, linkedParentTarget, junctions);
        NeedThrows<InvalidOperationException>(
            () => PortableDataPaths.GetValidatedMaintenanceDirectory(
                Path.Combine(linkedParent, "missing-data"),
                independentInstall),
            "A missing data directory beneath a reparse-point parent was accepted for maintenance storage.");

        string linkedMaintenanceProfile = CreateDirectory(root, "maintenance-linked-profile");
        string linkedMaintenance = PortableDataPaths.GetMaintenanceDirectory(linkedMaintenanceProfile);
        string linkedMaintenanceTarget = CreateDirectory(root, "maintenance-linked-target");
        CreateJunction(linkedMaintenance, linkedMaintenanceTarget, junctions);
        NeedThrows<InvalidOperationException>(
            () => PortableDataPaths.GetValidatedMaintenanceDirectory(
                linkedMaintenanceProfile,
                independentInstall),
            "A reparse-point maintenance root was accepted for migration staging or diagnostics.");

        NeedThrows<InvalidOperationException>(
            () => PortableDataPaths.GetValidatedMaintenanceDirectory(physicalProfile, maintenance),
            "A maintenance root overlapping the application install was accepted.");

        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureMaintenancePathSafe(maintenance, maintenance, physicalProfile),
            "Updater accepted a maintenance root equal to the application install directory.");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureMaintenancePathSafe(
                maintenance,
                Path.Combine(maintenance, "nested-install"),
                physicalProfile),
            "Updater accepted an application install inside the maintenance root.");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureMaintenancePathSafe(
                Path.Combine(maintenance, "nested-maintenance"),
                maintenance,
                physicalProfile),
            "Updater accepted a maintenance root inside the application install directory.");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureMaintenancePathSafe(
                physicalProfile,
                independentInstall,
                physicalProfile),
            "Updater accepted maintenance storage equal to the physical profile.");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureMaintenancePathSafe(
                Path.Combine(physicalProfile, "nested-maintenance"),
                independentInstall,
                physicalProfile),
            "Updater accepted maintenance storage inside the physical profile.");
        string maintenanceContainingProfile = CreateDirectory(root, "maintenance-containing-profile");
        string containedProfile = CreateDirectory(maintenanceContainingProfile, "data");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureMaintenancePathSafe(
                maintenanceContainingProfile,
                independentInstall,
                containedProfile),
            "Updater accepted a physical profile inside the maintenance root.");

        string updateLogRoot = PortableDataPaths.GetUpdateLogDirectory(physicalProfile);
        string updateLog = Path.Combine(updateLogRoot, "update-safe.log");
        PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(
            updateLog,
            independentInstall,
            physicalProfile);
        Need(PathsEqual(Path.GetDirectoryName(updateLog)!, updateLogRoot),
            "Durable update log was not derived directly under physical maintenance storage.");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(
                Path.Combine(root, "arbitrary-update.log"),
                independentInstall,
                physicalProfile),
            "Updater accepted a durable log outside the validated update-log root.");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(
                Path.Combine(updateLogRoot, "nested", "update.log"),
                independentInstall,
                physicalProfile),
            "Updater accepted a nested durable log instead of a direct child of the update-log root.");

        string linkedLogProfile = CreateDirectory(root, "maintenance-linked-log-profile");
        string linkedLogRoot = PortableDataPaths.GetUpdateLogDirectory(linkedLogProfile);
        Directory.CreateDirectory(PortableDataPaths.GetMaintenanceDirectory(linkedLogProfile));
        string linkedLogTarget = CreateDirectory(root, "maintenance-linked-log-target");
        CreateJunction(linkedLogRoot, linkedLogTarget, junctions);
        NeedThrows<InvalidOperationException>(
            () => PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(
                Path.Combine(linkedLogRoot, "update.log"),
                independentInstall,
                linkedLogProfile),
            "Updater accepted a reparse-point update-log root.");
    }

    private static void UpdateBoundariesRejectUnsafeMaintenanceTopology(string root)
    {
        string physicalProfile = CreateDirectory(root, "boundary-profile");
        string maintenance = PortableDataPaths.GetMaintenanceDirectory(physicalProfile);
        string install = CreateDirectory(maintenance, "app");
        string payload = CreateDirectory(root, "boundary-payload");
        WriteApplicationPayload(install, "boundary-old", "old-boundary.dll");
        WriteApplicationPayload(payload, "boundary-new", "new-boundary.dll");

        string requestRollback = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(physicalProfile),
            "request-backup");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdateInstaller.CreateUpdateRequest(
                processId: 0,
                payloadDirectory: payload,
                installDirectory: install,
                physicalDataDirectory: physicalProfile,
                applicationExecutable: Path.Combine(install, "AIQuickPanel.exe"),
                rollbackDirectory: requestRollback,
                downloadedZip: null,
                logPath: Path.Combine(root, "boundary-request.log"),
                restartApplication: false),
            "Installer request creation validated only the backup leaf and missed an install inside the maintenance root.");

        string workerRollback = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(physicalProfile),
            "worker-backup");
        var workerRequest = new PortableUpdateRequest(
            "update",
            0,
            payload,
            install,
            physicalProfile,
            Path.Combine(install, "AIQuickPanel.exe"),
            workerRollback,
            null,
            Path.Combine(root, "boundary-worker.log"),
            RestartApplication: false);
        Need(PortableUpdateWorker.Apply(workerRequest) == 1,
            "Post-shutdown worker validated only the backup leaf and missed an install inside the maintenance root.");

        string rollbackPayload = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(physicalProfile),
            "rollback-payload");
        WriteApplicationPayload(rollbackPayload, "boundary-rollback", "rollback-boundary.dll");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdateRollback.CreateRequest(
                backupDirectory: rollbackPayload,
                installDirectory: install,
                canonicalDataDirectory: physicalProfile,
                logPath: Path.Combine(root, "boundary-rollback.log"),
                processId: 0,
                restartApplication: false),
            "Rollback preparation missed an install inside the maintenance root.");

        string independentInstall = CreateDirectory(root, "boundary-independent-install");
        WriteApplicationPayload(independentInstall, "independent-old", "independent-old.dll");
        NeedThrows<InvalidOperationException>(
            () => PortableUpdateInstaller.CreateUpdateRequest(
                processId: 0,
                payloadDirectory: payload,
                installDirectory: independentInstall,
                physicalDataDirectory: physicalProfile,
                applicationExecutable: Path.Combine(independentInstall, "AIQuickPanel.exe"),
                rollbackDirectory: Path.Combine(root, "arbitrary-backup-destination"),
                downloadedZip: null,
                logPath: Path.Combine(root, "arbitrary-backup.log"),
                restartApplication: false),
            "Installer accepted a new backup destination outside the physical profile's update-backup root.");
    }

    private static void WriteApplicationPayload(
        string root,
        string executableContents,
        string libraryName,
        string executableName = "AIQuickPanel.exe")
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, executableName), executableContents);
        File.WriteAllText(Path.Combine(root, libraryName), libraryName);
        string[] entries = [executableName, libraryName, ReleasePayloadPolicy.ApplicationManifestFileName];
        File.WriteAllText(
            Path.Combine(root, ReleasePayloadPolicy.ApplicationManifestFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, entries }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteLegacyUpdateRequest(string path, PortableUpdateRequest request)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    request.Operation,
                    request.ProcessId,
                    request.PayloadDirectory,
                    request.InstallDirectory,
                    request.CanonicalDataDirectory,
                    request.ApplicationExecutable,
                    request.RollbackDirectory,
                    request.DownloadedZip,
                    request.LogPath,
                    request.RestartApplication
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string HashDirectoryWithoutFollowingReparsePoints(string root)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)
                         .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
            {
                string relativePath = Path.GetRelativePath(root, entry).Replace('\\', '/');
                FileAttributes attributes = File.GetAttributes(entry);
                hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
                hash.AppendData([0]);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(new DirectoryInfo(entry).LinkTarget ?? string.Empty));
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    hash.AppendData(File.ReadAllBytes(entry));
                }
                hash.AppendData([0]);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashDirectory(string root)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string CreateDirectory(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateJunction(string junction, string target, ICollection<string> junctions)
    {
        RunCmd("mklink", "/J", junction, target);
        string? linkTarget = new DirectoryInfo(junction).LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            throw new InvalidOperationException("Could not create updater junction test fixture: " + junction);
        }
        junctions.Add(junction);
    }

    private static void RemoveJunction(string junction)
    {
        string? linkTarget;
        try
        {
            linkTarget = new DirectoryInfo(junction).LinkTarget;
        }
        catch
        {
            linkTarget = null;
        }

        if (linkTarget is null && !Directory.Exists(junction))
        {
            return;
        }

        RunCmd("rmdir", junction);
    }

    private static void RunCmd(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/c");
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start cmd.exe for updater junction test fixture.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Updater junction fixture command failed ({process.ExitCode}). stdout={stdout}; stderr={stderr}");
        }
    }

    private static void NeedThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static void Need(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
