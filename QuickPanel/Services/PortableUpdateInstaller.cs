using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace QuickPanel.Services;

public static class PortableUpdateInstaller
{
    private const string UpdateStagingDirectoryName = "update-staging";

    public static string PrepareAndLaunch(string zipPath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new FileNotFoundException("Downloaded update ZIP was not found.", zipPath);
        }
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Trim().Length != 64)
        {
            throw new InvalidDataException("An expected SHA256 is required before an update can be installed.");
        }
        using (FileStream stream = File.OpenRead(zipPath))
        {
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded update SHA256 no longer matches the verified manifest.");
            }
        }

        string applicationExecutable = ResolveActiveInstallExecutable(Environment.ProcessPath);
        string appDirectory = Path.GetDirectoryName(applicationExecutable)
            ?? throw new InvalidOperationException("The running Quick Panel executable has no installation directory.");
        string physicalDataDirectory = PortableUpdatePathSafety.ResolvePhysicalDataDirectory(
            PortableDataPaths.DataDirectory,
            appDirectory);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(zipPath);
        string rollbackDirectory = Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(physicalDataDirectory),
            SanitizePathPart(UpdateService.GetCurrentVersionText()) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string stagingRoot = GetUpdateStagingRoot(physicalDataDirectory, appDirectory);
        string attemptDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        string extractDirectory = Path.Combine(attemptDirectory, "payload");
        Directory.CreateDirectory(extractDirectory);
        ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: false);
        string payloadRoot = ResolvePayloadRoot(extractDirectory);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(payloadRoot);
        string workerExe = ResolvePayloadWorkerExecutable(payloadRoot);

        string requestPath = Path.Combine(attemptDirectory, "update-request.json");
        string logPath = Path.Combine(attemptDirectory, "update.log");
        PortableUpdateRequest request = CreateUpdateRequest(
            Environment.ProcessId,
            payloadRoot,
            appDirectory,
            physicalDataDirectory,
            applicationExecutable,
            rollbackDirectory,
            zipPath,
            logPath);
        PortableUpdateWorker.WriteRequest(requestPath, request);
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(requestPath, request);
        diagnostics.Log(
            "prepared",
            "request=" + requestPath +
            "; source=" + payloadRoot +
            "; install=" + appDirectory +
            "; old-version=" + PortableUpdateDiagnostics.ReadVersion(request.ApplicationExecutable) +
            "; new-version=" + PortableUpdateDiagnostics.ReadVersion(workerExe));
        StartWorker(workerExe, requestPath, payloadRoot);
        return logPath;
    }

    internal static string ResolveActiveInstallExecutable(string? processExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(processExecutablePath))
        {
            throw new InvalidOperationException("The running Quick Panel executable path is unavailable.");
        }

        string executable = Path.GetFullPath(processExecutablePath);
        if (!IsSupportedApplicationExecutableName(Path.GetFileName(executable)) ||
            !File.Exists(executable))
        {
            throw new InvalidOperationException(
                "The updater can only run from a launched Quick Panel installation.");
        }
        string install = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("The running Quick Panel executable has no installation directory.");
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(install, requireManifest: true);
        return executable;
    }

    internal static string GetUpdateStagingRoot(
        string physicalDataDirectory,
        string installDirectory)
    {
        string physicalData = Path.GetFullPath(physicalDataDirectory);
        string install = Path.GetFullPath(installDirectory);
        string maintenance = PortableDataPaths.GetMaintenanceDirectory(physicalData);
        string stagingRoot = Path.Combine(maintenance, UpdateStagingDirectoryName);
        PortableUpdatePathSafety.EnsureMaintenancePathSafe(maintenance, install, physicalData);
        PortableUpdatePathSafety.EnsureMaintenancePathSafe(stagingRoot, install, physicalData);
        return stagingRoot;
    }

    internal static PortableUpdateRequest CreateUpdateRequest(
        int processId,
        string payloadDirectory,
        string installDirectory,
        string physicalDataDirectory,
        string applicationExecutable,
        string rollbackDirectory,
        string? downloadedZip,
        string logPath,
        bool restartApplication = true)
    {
        string install = Path.GetFullPath(installDirectory);
        string physicalData = Path.GetFullPath(physicalDataDirectory);
        string rollback = Path.GetFullPath(rollbackDirectory);
        string appExe = Path.GetFullPath(applicationExecutable);
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(install, requireManifest: false);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(install, physicalData);
        if (!IsSupportedApplicationExecutableName(Path.GetFileName(appExe)) ||
            !Path.GetDirectoryName(appExe)!.Equals(install, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The update executable must be in the validated Quick Panel installation directory.");
        }
        PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(rollback, install, physicalData);

        return new PortableUpdateRequest(
            "update",
            processId,
            Path.GetFullPath(payloadDirectory),
            install,
            physicalData,
            appExe,
            rollback,
            string.IsNullOrWhiteSpace(downloadedZip) ? null : Path.GetFullPath(downloadedZip),
            Path.GetFullPath(logPath),
            restartApplication);
    }

    internal static int StartWorker(string workerExe, string requestPath, string workingDirectory)
    {
        string worker = Path.GetFullPath(workerExe);
        string request = Path.GetFullPath(requestPath);
        string work = Path.GetFullPath(workingDirectory);
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException("Portable updater worker was not found.", worker);
        }
        if (!Directory.Exists(work) ||
            !Path.GetDirectoryName(worker)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    work.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Portable updater worker must be launched from its validated payload directory.");
        }
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(worker);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(request);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(work);
        PortableUpdateRequest updateRequest = PortableUpdateWorker.ReadRequest(request);
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(request, updateRequest);
        diagnostics.PrepareParentHandoff();
        diagnostics.Log(
            "worker-start-attempt",
            "worker=" + worker + "; request=" + request + "; working-directory=" + work);

        var info = new ProcessStartInfo
        {
            FileName = worker,
            UseShellExecute = false,
            WorkingDirectory = work,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--portable-update-worker");
        info.ArgumentList.Add(request);

        using Process process = StartProcess(info, diagnostics);
        int workerProcessId = process.Id;
        diagnostics.Log("worker-process-started", "worker-pid=" + workerProcessId);
        var timeout = TimeSpan.FromSeconds(15);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            process.Refresh();
            if (diagnostics.TryReadHandoff(workerProcessId, out _))
            {
                diagnostics.Log(
                    "worker-handoff-complete",
                    "worker-pid=" + workerProcessId + "; request=" + request);
                return workerProcessId;
            }
            if (process.HasExited)
            {
                diagnostics.TryLog(
                    "worker-handoff-failed",
                    "worker exited before acknowledgement; worker-pid=" + workerProcessId +
                    "; exit-code=" + process.ExitCode);
                throw new InvalidOperationException(
                    "The updater worker exited before it acknowledged the update request. " +
                    "Quick Panel will remain open; see the durable update log for details.");
            }
            Thread.Sleep(50);
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            diagnostics.TryLog(
                "worker-timeout-cleanup-failed",
                exception.GetType().Name + ": " + exception.Message);
        }
        diagnostics.TryLog(
            "worker-handoff-timeout",
            "worker did not acknowledge within 15 seconds; worker-pid=" + workerProcessId);
        throw new InvalidOperationException(
            "The updater worker did not acknowledge the update request. " +
            "Quick Panel will remain open; see the durable update log for details.");
    }

    private static Process StartProcess(
        ProcessStartInfo info,
        PortableUpdateDiagnostics diagnostics)
    {
        try
        {
            return Process.Start(info)
                ?? throw new InvalidOperationException("Could not start the portable updater worker.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            diagnostics.TryLog(
                "worker-process-start-failed",
                exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    private static string ResolvePayloadRoot(string extractDirectory)
    {
        string[] files = Directory.GetFiles(extractDirectory);
        string[] directories = Directory.GetDirectories(extractDirectory);
        return files.Length == 0 && directories.Length == 1 ? directories[0] : extractDirectory;
    }

    internal static string ResolvePayloadWorkerExecutable(string payloadRoot)
    {
        string legacy = Path.Combine(payloadRoot, "AIQuickPanel.exe");
        string renamed = Path.Combine(payloadRoot, "QuickPanel.exe");
        bool hasLegacy = File.Exists(legacy);
        bool hasRenamed = File.Exists(renamed);
        if (hasLegacy == hasRenamed)
        {
            throw new InvalidDataException(
                "Downloaded update must contain exactly one supported application executable at its root.");
        }
        return hasRenamed ? renamed : legacy;
    }

    internal static bool IsSupportedApplicationExecutableName(string? name) =>
        name is not null && (name.Equals("AIQuickPanel.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("QuickPanel.exe", StringComparison.OrdinalIgnoreCase));

    private static string SanitizePathPart(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }
        return string.IsNullOrWhiteSpace(value) ? "previous" : value;
    }
}
