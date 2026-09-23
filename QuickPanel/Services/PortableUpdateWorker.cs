using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace QuickPanel.Services;

public sealed record PortableUpdateRequest(
    string Operation,
    int ProcessId,
    string PayloadDirectory,
    string InstallDirectory,
    string CanonicalDataDirectory,
    string ApplicationExecutable,
    string? RollbackDirectory,
    string? DownloadedZip,
    string LogPath,
    bool RestartApplication = true,
    string? RecoveryDirectory = null);

public static class PortableUpdateWorker
{
    internal const string InvalidBackupMarkerFileName = ".invalid-update-backup";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void WriteRequest(string path, PortableUpdateRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(request, JsonOptions));
    }

    internal static PortableUpdateRequest ReadRequest(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return JsonSerializer.Deserialize<PortableUpdateRequest>(
                   File.ReadAllText(Path.GetFullPath(path)),
                   JsonOptions)
               ?? throw new InvalidDataException("Portable update request was empty.");
    }

    public static int ApplyRequestFile(string path)
    {
        return ApplyRequestFileCore(
            path,
            legacyInstallCandidates: null,
            processStarter: null,
            legacyCanonicalDataDirectory: null);
    }

    internal static int ApplyRequestFile(
        string path,
        IEnumerable<string> legacyInstallCandidates)
    {
        ArgumentNullException.ThrowIfNull(legacyInstallCandidates);
        return ApplyRequestFileCore(
            path,
            legacyInstallCandidates,
            processStarter: null,
            legacyCanonicalDataDirectory: null);
    }

    internal static int ApplyRequestFile(
        string path,
        IEnumerable<string> legacyInstallCandidates,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        ArgumentNullException.ThrowIfNull(legacyInstallCandidates);
        ArgumentNullException.ThrowIfNull(processStarter);
        return ApplyRequestFileCore(
            path,
            legacyInstallCandidates,
            processStarter,
            legacyCanonicalDataDirectory: null);
    }

    internal static int ApplyRequestFile(
        string path,
        IEnumerable<string> legacyInstallCandidates,
        Func<ProcessStartInfo, Process?> processStarter,
        string legacyCanonicalDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(legacyInstallCandidates);
        ArgumentNullException.ThrowIfNull(processStarter);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyCanonicalDataDirectory);
        return ApplyRequestFileCore(
            path,
            legacyInstallCandidates,
            processStarter,
            legacyCanonicalDataDirectory);
    }

    private static int ApplyRequestFileCore(
        string path,
        IEnumerable<string>? legacyInstallCandidates,
        Func<ProcessStartInfo, Process?>? processStarter,
        string? legacyCanonicalDataDirectory)
    {
        PortableUpdateDiagnostics.WriteStartup(
            path,
            "worker entry; worker-pid=" + Environment.ProcessId + "; request=" + path);

        PortableUpdateRequest request;
        try
        {
            request = ReadRequest(path);
        }
        catch (Exception exception)
        {
            PortableUpdateDiagnostics.WriteStartup(
                path,
                "request deserialization failed: " + Describe(exception));
            return 1;
        }

        bool parentRequiresHandoff;
        try
        {
            parentRequiresHandoff = PortableUpdateDiagnostics.RequestRequiresHandoff(path);
        }
        catch (Exception exception)
        {
            PortableUpdateDiagnostics.WriteStartup(
                path,
                "handoff-mode detection failed: " + Describe(exception));
            return 1;
        }

        bool legacyTargetResolved = false;
        if (!parentRequiresHandoff)
        {
            PortableUpdateRequest reportedRequest = request;
            try
            {
                request = legacyInstallCandidates is null
                    ? LegacyUpdateInstallResolver.ResolveFromRegisteredInstallations(request)
                    : legacyCanonicalDataDirectory is null
                        ? LegacyUpdateInstallResolver.Resolve(request, legacyInstallCandidates)
                        : LegacyUpdateInstallResolver.Resolve(
                            request,
                            legacyInstallCandidates,
                            legacyCanonicalDataDirectory);
                legacyTargetResolved = !request.InstallDirectory.Equals(
                    reportedRequest.InstallDirectory,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                try
                {
                    PortableUpdateDiagnostics failureDiagnostics =
                        PortableUpdateDiagnostics.CreateLegacyResolutionFailure(path, reportedRequest);
                    failureDiagnostics.TryLog(
                        "request-read",
                        "operation=" + reportedRequest.Operation +
                        "; parent-pid=" + reportedRequest.ProcessId +
                        "; worker-pid=" + Environment.ProcessId);
                    failureDiagnostics.TryLog(
                        "legacy-target-resolution-failed",
                        Describe(exception));
                }
                catch (Exception diagnosticsException)
                {
                    PortableUpdateDiagnostics.WriteStartup(
                        path,
                        "legacy target resolution failed and durable diagnostics initialization failed: " +
                        Describe(diagnosticsException));
                }
                return 1;
            }
        }

        PortableUpdateDiagnostics diagnostics;
        try
        {
            diagnostics = PortableUpdateDiagnostics.Create(path, request);
            diagnostics.TryLog(
                "request-read",
                "operation=" + request.Operation +
                "; parent-pid=" + request.ProcessId +
                "; worker-pid=" + Environment.ProcessId);
            if (legacyTargetResolved)
            {
                diagnostics.TryLog(
                    "legacy-target-resolved",
                    "reported-install=updater-artifact; resolved-install=validated-stable-install");
            }
        }
        catch (Exception exception)
        {
            PortableUpdateDiagnostics.WriteStartup(
                path,
                "diagnostics initialization failed: " + Describe(exception));
            TryRestartLegacyParent(path, request);
            return 1;
        }

        if (processStarter is null)
        {
            if (!UpdaterBootstrap.IsAvailable(request))
            {
                diagnostics.TryLog(
                    "guarded-updater-missing",
                    "The update request has no usable dedicated updater runtime; parent handoff was not acknowledged.");
                return 1;
            }
            try
            {
                _ = UpdaterBootstrap.PrepareAndLaunch(request, path, diagnostics);
                return 0;
            }
            catch (Exception exception)
            {
                diagnostics.TryLog("guarded-bootstrap-failed", Describe(exception));
                return 1;
            }
        }

        return ApplyCore(
            request,
            diagnostics,
            faultInjection: null,
            processStarter);
    }

    public static int Apply(PortableUpdateRequest request)
    {
        return ApplyCore(
            request,
            diagnostics: null,
            faultInjection: null,
            processStarter: null);
    }

    public static int Apply(PortableUpdateRequest request, Action<string>? faultInjection)
    {
        return ApplyCore(
            request,
            diagnostics: null,
            faultInjection,
            processStarter: null);
    }

    internal static int Apply(
        PortableUpdateRequest request,
        Action<string>? faultInjection,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        ArgumentNullException.ThrowIfNull(processStarter);
        return ApplyCore(
            request,
            diagnostics: null,
            faultInjection,
            processStarter);
    }

    private static int ApplyCore(
        PortableUpdateRequest request,
        PortableUpdateDiagnostics? diagnostics,
        Action<string>? faultInjection,
        Func<ProcessStartInfo, Process?>? processStarter)
    {
        ArgumentNullException.ThrowIfNull(request);

        string install = string.Empty;
        string payload = string.Empty;
        string physicalData = string.Empty;
        string appExe = string.Empty;
        string log = TryGetFullPath(request.LogPath);
        string? recoverySource = null;
        bool installMayBeMutated = false;
        bool restoreSucceeded = false;
        bool handoffSignaled = false;
        bool restartTargetValidated = false;
        bool parentRequiresHandoff = diagnostics?.ParentRequiresHandoff == true;

        try
        {
            install = Path.GetFullPath(request.InstallDirectory);
            physicalData = Path.GetFullPath(request.CanonicalDataDirectory);
            appExe = Path.GetFullPath(request.ApplicationExecutable);
            ValidateRestartTarget(install, physicalData, appExe);
            restartTargetValidated = true;
            (install, payload, physicalData, appExe, string? rollback, string? rollbackRecovery) =
                ValidateRequest(request);
            recoverySource = rollbackRecovery;
            Log(
                diagnostics,
                log,
                "validated",
                "operation=" + request.Operation +
                "; install=" + install +
                "; payload=" + payload +
                "; current-version=" + PortableUpdateDiagnostics.ReadVersion(appExe) +
                "; payload-version=" + PortableUpdateDiagnostics.ReadVersion(
                    PortableUpdateInstaller.ResolvePayloadWorkerExecutable(payload)));

            if (parentRequiresHandoff)
            {
                diagnostics!.SignalHandoff();
                handoffSignaled = true;
            }
            else
            {
                Log(
                    diagnostics,
                    log,
                    "legacy-parent",
                    "request has no acknowledgement marker; failure recovery will wait for parent exit");
            }

            Log(diagnostics, log, "wait-parent", "parent-pid=" + request.ProcessId);
            WaitForProcessExit(request.ProcessId);
            Log(diagnostics, log, "parent-exited", "parent-pid=" + request.ProcessId);

            if (request.Operation.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Log(diagnostics, log, "backup-start", "destination=" + rollback);
                    ReleasePayloadPolicy.CopyApplicationFiles(install, rollback!, physicalData);
                    faultInjection?.Invoke("after-backup-copy");
                    ReleasePayloadPolicy.VerifyApplicationFilesCopy(install, rollback!, physicalData);
                }
                catch (Exception backupException)
                {
                    MarkBackupInvalid(rollback!, backupException, diagnostics, log);
                    throw;
                }
                recoverySource = rollback;
                Log(
                    diagnostics,
                    log,
                    "backup-verified",
                    "destination=" + rollback +
                    "; version=" + PortableUpdateDiagnostics.ReadVersion(
                        PortableUpdateInstaller.ResolvePayloadWorkerExecutable(rollback!)));
            }
            else if (recoverySource is not null)
            {
                Log(diagnostics, log, "rollback-recovery-verify", "source=" + recoverySource);
                ReleasePayloadPolicy.VerifyApplicationFilesCopy(install, recoverySource, physicalData);
                Log(diagnostics, log, "rollback-recovery-verified", "source=" + recoverySource);
            }

            Log(diagnostics, log, "replace-start", "source=" + payload + "; destination=" + install);
            installMayBeMutated = true;
            ReleasePayloadPolicy.ReplaceApplicationFiles(payload, install, physicalData);
            faultInjection?.Invoke("after-replace");
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(payload, install, physicalData);
            Log(
                diagnostics,
                log,
                "replacement-verified",
                "installed-version=" + PortableUpdateDiagnostics.ReadVersion(appExe));

            if (!string.IsNullOrWhiteSpace(request.DownloadedZip) && File.Exists(request.DownloadedZip))
            {
                ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(request.DownloadedZip);
                File.Delete(request.DownloadedZip);
                Log(diagnostics, log, "download-removed", "archive=" + Path.GetFullPath(request.DownloadedZip));
            }

            if (request.RestartApplication)
            {
                StartApplication(appExe, install, diagnostics, log, processStarter, "completed");
            }
            Log(diagnostics, log, "success", request.Operation + " applied successfully");
            return 0;
        }
        catch (Exception exception)
        {
            Log(diagnostics, log, "failure", request.Operation + " failed: " + Describe(exception));

            if (installMayBeMutated && recoverySource is not null && Directory.Exists(recoverySource))
            {
                try
                {
                    Log(diagnostics, log, "restore-start", "source=" + recoverySource);
                    ReleasePayloadPolicy.RestoreApplicationFiles(
                        recoverySource,
                        payload,
                        install,
                        physicalData);
                    ReleasePayloadPolicy.VerifyApplicationFilesCopy(recoverySource, install, physicalData);
                    restoreSucceeded = true;
                    Log(
                        diagnostics,
                        log,
                        "restore-verified",
                        "restored-version=" + PortableUpdateDiagnostics.ReadVersion(appExe));
                }
                catch (Exception restoreException)
                {
                    Log(
                        diagnostics,
                        log,
                        "restore-failed",
                        "automatic restore failed: " + Describe(restoreException));
                }
            }

            bool parentWillExit = !parentRequiresHandoff || handoffSignaled;
            bool safeToRestart = !installMayBeMutated || restoreSucceeded;
            if (request.RestartApplication && parentWillExit && safeToRestart &&
                restartTargetValidated && File.Exists(appExe))
            {
                try
                {
                    WaitForProcessExit(request.ProcessId);
                    StartApplication(appExe, install, diagnostics, log, processStarter, "recovery");
                }
                catch (Exception restartException)
                {
                    Log(
                        diagnostics,
                        log,
                        "recovery-restart-failed",
                        Describe(restartException));
                }
            }
            return 1;
        }
    }

    private static (
        string Install,
        string Payload,
        string PhysicalData,
        string ApplicationExecutable,
        string? Rollback,
        string? Recovery) ValidateRequest(PortableUpdateRequest request)
    {
        string install = Path.GetFullPath(request.InstallDirectory);
        string payload = Path.GetFullPath(request.PayloadDirectory);
        string physicalData = Path.GetFullPath(request.CanonicalDataDirectory);
        string appExe = Path.GetFullPath(request.ApplicationExecutable);
        string log = Path.GetFullPath(request.LogPath);

        ValidateRestartTarget(install, physicalData, appExe);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(payload);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(payload);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(log);

        bool isUpdate = request.Operation.Equals("update", StringComparison.OrdinalIgnoreCase);
        bool isRollback = request.Operation.Equals("rollback", StringComparison.OrdinalIgnoreCase);
        if (!isUpdate && !isRollback)
        {
            throw new InvalidDataException("Unknown portable update operation.");
        }

        string? rollback = null;
        string? recovery = null;
        if (isUpdate)
        {
            rollback = Path.GetFullPath(request.RollbackDirectory
                ?? throw new InvalidDataException("Update request is missing its rollback directory."));
            PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(rollback, install, physicalData);
            if (Directory.Exists(rollback) || File.Exists(rollback))
            {
                throw new IOException("Rollback directory already exists.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.RecoveryDirectory))
        {
            recovery = Path.GetFullPath(request.RecoveryDirectory);
            ReleasePayloadPolicy.EnsureSafeInstallTarget(recovery, physicalData);
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(recovery);
            ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(recovery);
        }

        if (!string.IsNullOrWhiteSpace(request.DownloadedZip))
        {
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(request.DownloadedZip);
        }
        return (install, payload, physicalData, appExe, rollback, recovery);
    }

    private static void ValidateRestartTarget(
        string installDirectory,
        string physicalDataDirectory,
        string applicationExecutable)
    {
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(installDirectory);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(installDirectory, requireManifest: false);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(installDirectory, physicalDataDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(applicationExecutable);
        if (!PortableUpdateInstaller.IsSupportedApplicationExecutableName(Path.GetFileName(applicationExecutable)) ||
            !Path.GetDirectoryName(applicationExecutable)!.Equals(
                Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Restart executable is outside the validated installation directory.");
        }
    }

    private static void StartApplication(
        string appExe,
        string install,
        PortableUpdateDiagnostics? diagnostics,
        string log,
        Func<ProcessStartInfo, Process?>? processStarter,
        string reason)
    {
        if (!File.Exists(appExe))
        {
            throw new InvalidDataException("Application executable was not installed.");
        }
        var info = new ProcessStartInfo
        {
            FileName = appExe,
            UseShellExecute = true,
            WorkingDirectory = install
        };
        Process? process = processStarter is null
            ? Process.Start(info)
                ?? throw new InvalidOperationException("Could not restart the installed Quick Panel executable.")
            : processStarter(info);
        Log(
            diagnostics,
            log,
            "restart",
            "reason=" + reason +
            "; executable=" + appExe +
            "; process-id=" + (process?.Id.ToString() ?? "injected"));
        process?.Dispose();
    }

    private static void TryRestartLegacyParent(string requestPath, PortableUpdateRequest request)
    {
        try
        {
            string requestDirectory = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
            if (File.Exists(Path.Combine(
                    requestDirectory,
                    PortableUpdateDiagnostics.HandoffRequiredFileName)))
            {
                return;
            }

            string install = Path.GetFullPath(request.InstallDirectory);
            string physicalData = Path.GetFullPath(request.CanonicalDataDirectory);
            string appExe = Path.GetFullPath(request.ApplicationExecutable);
            ValidateRestartTarget(install, physicalData, appExe);
            if (!File.Exists(appExe))
            {
                return;
            }
            WaitForProcessExit(request.ProcessId);
            if (request.RestartApplication)
            {
                using Process? _ = Process.Start(new ProcessStartInfo
                {
                    FileName = appExe,
                    UseShellExecute = true,
                    WorkingDirectory = install
                });
            }
        }
        catch (Exception exception)
        {
            PortableUpdateDiagnostics.WriteStartup(
                requestPath,
                "legacy recovery restart failed: " + Describe(exception));
        }
    }

    private static void Log(
        PortableUpdateDiagnostics? diagnostics,
        string logPath,
        string stage,
        string message)
    {
        if (diagnostics is not null)
        {
            diagnostics.TryLog(stage, message);
            return;
        }

        try
        {
            string path = Path.GetFullPath(logPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.UtcNow:O}] stage={Sanitize(stage)}; {Sanitize(message)}{Environment.NewLine}");
        }
        catch
        {
            // Direct worker callers still receive the exit code when diagnostics are unavailable.
        }
    }

    private static void MarkBackupInvalid(
        string backupDirectory,
        Exception backupException,
        PortableUpdateDiagnostics? diagnostics,
        string logPath)
    {
        try
        {
            if (!Directory.Exists(backupDirectory))
            {
                return;
            }

            string marker = Path.Combine(backupDirectory, InvalidBackupMarkerFileName);
            File.WriteAllText(
                marker,
                $"Backup verification failed at {DateTimeOffset.UtcNow:O}.{Environment.NewLine}" +
                Describe(backupException) + Environment.NewLine);
            Log(diagnostics, logPath, "backup-invalid", "marker=" + marker);
        }
        catch (Exception markerException)
        {
            Log(
                diagnostics,
                logPath,
                "backup-invalid-marker-failed",
                Describe(markerException));
        }
    }

    private static string TryGetFullPath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Path.GetTempPath(), "QuickPanelUpdate", "update-unavailable.log")
                : Path.GetFullPath(path);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "QuickPanelUpdate", "update-unavailable.log");
        }
    }

    private static string Describe(Exception exception)
    {
        return exception.GetType().Name + ": " + Sanitize(exception.Message);
    }

    private static string Sanitize(string? value)
    {
        return (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static void WaitForProcessExit(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            return;
        }
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds))
            {
                throw new TimeoutException("Quick Panel did not exit before the update timeout.");
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }
}
