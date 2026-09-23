using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using QuickPanel.Updater.Core;

namespace QuickPanel.Services;

internal sealed record UpdaterBootstrapResult(
    int RunnerProcessId,
    string TransactionPath,
    string StatePath,
    string RuntimeDirectory);

internal static class UpdaterBootstrap
{
    private const string RuntimeDirectoryName = "UpdaterRuntime";
    private static readonly string[] UpdaterExecutableNames = ["QuickPanel.Updater.exe", "AIQuickPanel.Updater.exe"];

    internal static bool IsAvailable(PortableUpdateRequest request)
    {
        try
        {
            _ = ResolveRuntimeSource(request);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static UpdaterBootstrapResult PrepareAndLaunch(
        PortableUpdateRequest request,
        string requestPath,
        PortableUpdateDiagnostics diagnostics,
        string? updaterRuntimeSource = null,
        string? guardianExecutableOverride = null,
        string? transactionRoot = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);
        string payload = Path.GetFullPath(request.PayloadDirectory);
        string install = Path.GetFullPath(request.InstallDirectory);
        string profile = Path.GetFullPath(request.CanonicalDataDirectory);
        string appExe = Path.GetFullPath(request.ApplicationExecutable);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(install, requireManifest: true);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(payload, requireManifest: true);
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(install, profile);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(payload, profile);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(payload);
        if (!IsSupportedExecutableInDirectory(appExe, install))
        {
            throw new InvalidDataException("The guarded updater restart target is not the stable installation.");
        }

        string attemptId = Guid.NewGuid().ToString("N");
        string operation = request.Operation.Trim().ToLowerInvariant();
        if (operation == "rollback")
        {
            string recovery = Path.GetFullPath(request.RecoveryDirectory
                ?? throw new InvalidDataException("Rollback request has no verified current recovery copy."));
            ReleasePayloadPolicy.EnsureSafeInstallTarget(recovery, profile);
            ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(recovery);
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(install, recovery, profile);
        }
        string backup = operation switch
        {
            "update" => Path.GetFullPath(request.RollbackDirectory
                ?? throw new InvalidDataException("Update request has no rollback destination.")),
            "rollback" => Path.Combine(
                PortableDataPaths.GetUpdateBackupDirectory(profile),
                "before-rollback-" + attemptId),
            _ => throw new InvalidDataException("Unknown portable update operation.")
        };
        PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(backup, install, profile);

        string sourceRuntime = updaterRuntimeSource is null
            ? ResolveRuntimeSource(request)
            : Path.GetFullPath(updaterRuntimeSource);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(sourceRuntime);
        string attemptsRoot = transactionRoot is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "QuickPanel-Update", "transactions")
            : Path.GetFullPath(transactionRoot);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(attemptsRoot, profile);
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(
            Directory.Exists(attemptsRoot) ? attemptsRoot : Path.GetDirectoryName(attemptsRoot)!);
        string attemptDirectory = Path.Combine(attemptsRoot, attemptId);
        string stagedRuntime = Path.Combine(attemptDirectory, "runtime");
        CopyRuntime(sourceRuntime, stagedRuntime);
        string updaterExe = ResolveUpdaterExecutable(stagedRuntime);
        if (!File.Exists(updaterExe))
        {
            throw new InvalidDataException("The staged updater runtime has no updater executable.");
        }

        string manifest = Path.Combine(payload, ReleasePayloadPolicy.ApplicationManifestFileName);
        string transactionPath = Path.Combine(attemptDirectory, "transaction.json");
        string statePath = Path.Combine(attemptDirectory, "state.json");
        string currentVersion = ReadVersion(appExe);
        string targetExecutable = PortableUpdateInstaller.ResolvePayloadWorkerExecutable(payload);
        string targetVersion = ReadVersion(targetExecutable);
        var transaction = new UpdateTransaction(
            UpdateTransaction.CurrentSchemaVersion,
            attemptId,
            Guid.NewGuid().ToString("N"),
            operation,
            request.ProcessId,
            payload,
            install,
            profile,
            appExe,
            backup,
            diagnostics.DurableLogPath,
            currentVersion,
            targetVersion,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))),
            request.RestartApplication)
        {
            TargetApplicationExecutable = Path.Combine(install, Path.GetFileName(targetExecutable))
        };
        UpdateTransactionStore.Create(transactionPath, transaction);
        diagnostics.TryLog("transaction-created", "attempt=" + attemptId);

        string guardianExe = guardianExecutableOverride is null
            ? updaterExe
            : Path.GetFullPath(guardianExecutableOverride);
        var info = new ProcessStartInfo
        {
            FileName = updaterExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stagedRuntime
        };
        info.ArgumentList.Add("--apply");
        info.ArgumentList.Add(transactionPath);
        info.ArgumentList.Add(statePath);
        info.ArgumentList.Add(guardianExe);
        using Process runner = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the guarded update runner.");
        int runnerId = runner.Id;
        WaitForGuardianReady(runner, statePath, transaction.Nonce);
        if (diagnostics.ParentRequiresHandoff)
        {
            diagnostics.SignalHandoff();
        }
        diagnostics.TryLog("guardian-ready", "runner-pid=" + runnerId);
        return new UpdaterBootstrapResult(
            runnerId,
            transactionPath,
            statePath,
            stagedRuntime);
    }

    private static string ResolveRuntimeSource(string payloadDirectory)
    {
        string payload = Path.GetFullPath(payloadDirectory);
        string dedicated = Path.Combine(payload, RuntimeDirectoryName);
        if (UpdaterExecutableNames.Any(name => File.Exists(Path.Combine(dedicated, name))))
        {
            return dedicated;
        }
        if (UpdaterExecutableNames.Any(name => File.Exists(Path.Combine(payload, name))))
        {
            return payload;
        }
        throw new InvalidDataException("The update payload does not contain the guarded updater runtime.");
    }

    private static string ResolveRuntimeSource(PortableUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sourceRoot = request.Operation.Trim().ToLowerInvariant() switch
        {
            "update" => request.PayloadDirectory,
            "rollback" => request.RecoveryDirectory
                ?? throw new InvalidDataException("Rollback request has no verified current recovery copy."),
            _ => throw new InvalidDataException("Unknown portable update operation.")
        };
        return ResolveRuntimeSource(sourceRoot);
    }

    private static void CopyRuntime(string source, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("The guarded updater runtime destination already exists.");
        }
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(directory);
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(file);
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void WaitForGuardianReady(Process runner, string statePath, string nonce)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (File.Exists(statePath))
            {
                UpdateTransactionSnapshot state = UpdateTransactionStore.ReadState(statePath);
                if (!state.Nonce.Equals(nonce, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Guarded updater readiness nonce did not match.");
                }
                if (state.Phase is UpdateTransactionPhase.GuardianReady or
                    UpdateTransactionPhase.ParentExited or UpdateTransactionPhase.ReplacementStarted or
                    UpdateTransactionPhase.ReplacementVerified or UpdateTransactionPhase.RestartStarted or
                    UpdateTransactionPhase.Committed)
                {
                    return;
                }
                if (state.Phase is UpdateTransactionPhase.RestoreStarted or
                    UpdateTransactionPhase.Restored or UpdateTransactionPhase.Failed)
                {
                    throw new InvalidOperationException(
                        "The guarded updater could not establish a safe handoff. State: " + state.Phase + ".");
                }
            }
            runner.Refresh();
            if (runner.HasExited)
            {
                throw new InvalidOperationException(
                    "The guarded update runner exited before rollback and guardian readiness. Exit code: " +
                    runner.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
            }
            Thread.Sleep(50);
        }
        try
        {
            runner.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        throw new TimeoutException("The guarded updater was not ready within 30 seconds.");
    }

    private static string ReadVersion(string executable)
    {
        string? value = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
        if (!Version.TryParse(value, out Version? version))
        {
            throw new InvalidDataException("An updater executable has no valid product version.");
        }
        return version.ToString();
    }

    private static string ResolveUpdaterExecutable(string runtimeDirectory)
    {
        string[] found = UpdaterExecutableNames
            .Select(name => Path.Combine(runtimeDirectory, name))
            .Where(File.Exists)
            .ToArray();
        if (found.Length != 1)
        {
            throw new InvalidDataException("The staged updater runtime must contain one supported updater executable.");
        }
        return found[0];
    }

    private static bool IsSupportedExecutableInDirectory(string executable, string directory) =>
        executable.Equals(Path.Combine(directory, "AIQuickPanel.exe"), StringComparison.OrdinalIgnoreCase) ||
        executable.Equals(Path.Combine(directory, "QuickPanel.exe"), StringComparison.OrdinalIgnoreCase);
}
