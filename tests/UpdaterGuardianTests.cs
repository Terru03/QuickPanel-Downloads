extern alias updater;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using QuickPanel.Services;
using QuickPanel.Updater.Core;
using UpdateGuardian = updater::QuickPanel.Updater.UpdateGuardian;
using UpdateTransactionRunner = updater::QuickPanel.Updater.UpdateTransactionRunner;

internal static class UpdaterGuardianTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows() ||
            IsNestedTestProcess())
        {
            return;
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "QuickPanel.Tests",
            "updater-guardian-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ValidTransactionCommitsAndPreservesProfile(root);
            GuardianRestoresAfterRunnerTermination(root);
            StartupRecoveryRestoresAfterBothProcessesTerminate(root);
            GuardianStartFailureDoesNotRestartRunningParent(root);
            GuardianCommitsExistingRestartWithoutLaunchingDuplicate(root);
            CrossNameRecoveryUsesCorrectExecutable(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Keep a failed fixture for diagnosis.
            }
        }
    }

    private static void GuardianCommitsExistingRestartWithoutLaunchingDuplicate(string root)
    {
        Fixture fixture = CreateFixture(root, "restart-already-started", restartApplication: true);
        ReleasePayloadPolicy.CopyApplicationFiles(fixture.Install, fixture.Backup, fixture.Profile);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.RollbackVerified, 201,
            rollbackSha256: UpdateBackupFingerprint.Compute(fixture.Backup));
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.GuardianReady, 202);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.ParentExited, 202);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.ReplacementStarted, 203);
        ReleasePayloadPolicy.ReplaceApplicationFiles(fixture.Payload, fixture.Install, fixture.Profile);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.ReplacementVerified, 203);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.RestartStarted, 203);
        int duplicateRestarts = 0;
        var guardian = new UpdateGuardian(_ =>
        {
            duplicateRestarts++;
            return null;
        });
        Need(guardian.Recover(fixture.TransactionPath, fixture.StatePath, 0) == 0,
            "Guardian did not commit a target whose restart was already created.");
        Need(duplicateRestarts == 0,
            "Guardian launched a duplicate application after RestartStarted was already durable.");
        Need(UpdateTransactionStore.ReadState(fixture.StatePath).Phase == UpdateTransactionPhase.Committed,
            "Guardian did not commit the already-started verified target.");
    }

    private static void GuardianStartFailureDoesNotRestartRunningParent(string root)
    {
        Fixture fixture = CreateFixture(root, "guardian-start-failure", restartApplication: true);
        bool restartAttempted = false;
        var runner = new UpdateTransactionRunner(
            guardianStarter: _ => throw new System.ComponentModel.Win32Exception(5),
            applicationStarter: _ =>
            {
                restartAttempted = true;
                return null;
            });
        int exitCode = runner.Run(
            fixture.TransactionPath,
            fixture.StatePath,
            GetUpdaterExecutable());
        Need(exitCode == 1, "A blocked guardian did not fail the update runner.");
        Need(!restartAttempted,
            "A blocked pre-handoff guardian restarted a second application while the parent was still running.");
        Need(UpdateTransactionStore.ReadState(fixture.StatePath).Phase == UpdateTransactionPhase.Failed,
            "A pre-handoff guardian failure did not record a controlled terminal failure.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "old",
            "A pre-handoff guardian failure changed the stable install.");
    }

    private static void CrossNameRecoveryUsesCorrectExecutable(string root)
    {
        Fixture committed = CreateFixture(root, "rename-commit", restartApplication: true);
        WriteApplication(committed.Payload, "new", "QuickPanel.exe");
        UpdateTransaction targetTransaction = committed.Transaction with
        {
            PayloadManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(committed.Payload, ReleasePayloadPolicy.ApplicationManifestFileName)))),
            TargetApplicationExecutable = Path.Combine(committed.Install, "QuickPanel.exe")
        };
        File.Delete(committed.TransactionPath);
        File.Delete(committed.StatePath);
        UpdateTransactionStore.Create(committed.TransactionPath, targetTransaction);
        string? started = null;
        var runner = new UpdateTransactionRunner(applicationStarter: info =>
        {
            started = info.FileName;
            return null;
        });
        Need(runner.Run(committed.TransactionPath, committed.StatePath, GetUpdaterExecutable()) == 0,
            "Cross-name guarded update did not commit.");
        Need(started == targetTransaction.TargetApplicationExecutable,
            "Cross-name update did not restart QuickPanel.exe.");
        Need(File.Exists(Path.Combine(committed.Backup, "AIQuickPanel.exe")),
            "Cross-name update did not retain the old executable in rollback.");

        Fixture restored = CreateFixture(root, "rename-restore", restartApplication: true);
        WriteApplication(restored.Payload, "new", "QuickPanel.exe");
        UpdateTransaction restoreTransaction = restored.Transaction with
        {
            PayloadManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(restored.Payload, ReleasePayloadPolicy.ApplicationManifestFileName)))),
            TargetApplicationExecutable = Path.Combine(restored.Install, "QuickPanel.exe")
        };
        File.Delete(restored.TransactionPath);
        File.Delete(restored.StatePath);
        UpdateTransactionStore.Create(restored.TransactionPath, restoreTransaction);
        string profileHash = HashDirectory(restored.Profile);
        ReleasePayloadPolicy.CopyApplicationFiles(restored.Install, restored.Backup, restored.Profile);
        UpdateTransactionStore.Advance(restored.StatePath, restoreTransaction.Nonce,
            UpdateTransactionPhase.RollbackVerified, 101,
            rollbackSha256: UpdateBackupFingerprint.Compute(restored.Backup));
        UpdateTransactionStore.Advance(restored.StatePath, restoreTransaction.Nonce,
            UpdateTransactionPhase.GuardianReady, 102);
        UpdateTransactionStore.Advance(restored.StatePath, restoreTransaction.Nonce,
            UpdateTransactionPhase.ParentExited, 102);
        UpdateTransactionStore.Advance(restored.StatePath, restoreTransaction.Nonce,
            UpdateTransactionPhase.ReplacementStarted, 103);
        ReleasePayloadPolicy.ReplaceApplicationFiles(restored.Payload, restored.Install, restored.Profile);
        File.WriteAllText(Path.Combine(restored.Install, "marker.txt"), "corrupt");
        started = null;
        var recovery = new UpdateGuardian(info =>
        {
            started = info.FileName;
            return null;
        });
        Need(recovery.Recover(restored.TransactionPath, restored.StatePath, 0) == 0,
            "Cross-name startup recovery failed.");
        Need(started == restoreTransaction.ApplicationExecutable,
            "Cross-name recovery did not restart AIQuickPanel.exe.");
        Need(HashDirectory(restored.Profile) == profileHash,
            "Cross-name recovery changed persistent profile data.");
    }

    private static void StartupRecoveryRestoresAfterBothProcessesTerminate(string root)
    {
        Fixture fixture = CreateFixture(root, "startup-recovery", restartApplication: true);
        string profileHash = HashDirectory(fixture.Profile);
        ReleasePayloadPolicy.CopyApplicationFiles(fixture.Install, fixture.Backup, fixture.Profile);
        ReleasePayloadPolicy.VerifyApplicationFilesCopy(fixture.Install, fixture.Backup, fixture.Profile);
        UpdateTransactionStore.Advance(
            fixture.StatePath,
            fixture.Transaction.Nonce,
            UpdateTransactionPhase.RollbackVerified,
            101,
            rollbackSha256: UpdateBackupFingerprint.Compute(fixture.Backup));
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.GuardianReady, 102);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.ParentExited, 102);
        UpdateTransactionStore.Advance(
            fixture.StatePath, fixture.Transaction.Nonce, UpdateTransactionPhase.ReplacementStarted, 103);
        ReleasePayloadPolicy.ReplaceApplicationFiles(fixture.Payload, fixture.Install, fixture.Profile);
        File.WriteAllText(Path.Combine(fixture.Install, "marker.txt"), "corrupt-after-both-died");

        string restartMarker = Path.Combine(fixture.Attempt, "startup-restart.marker");
        var recovery = new UpdateGuardian(info =>
        {
            File.WriteAllText(restartMarker, info.FileName);
            return null;
        });
        Need(recovery.Recover(fixture.TransactionPath, fixture.StatePath, 0) == 0,
            "Startup recovery did not complete after both updater processes terminated.");
        Need(UpdateTransactionStore.ReadState(fixture.StatePath).Phase == UpdateTransactionPhase.Restored,
            "Startup recovery did not record a verified restore.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "old",
            "Startup recovery did not restore the previous application.");
        Need(File.Exists(restartMarker), "Startup recovery did not restart the restored application.");
        Need(HashDirectory(fixture.Profile) == profileHash,
            "Startup recovery changed canonical profile data.");
    }

    private static bool IsNestedTestProcess()
    {
        return Environment.GetEnvironmentVariable("QUICKPANEL_TEST_CHILD") == "1" ||
            Environment.GetEnvironmentVariable("QUICKPANEL_TEST_HANDOFF_WORKER") == "1" ||
            Environment.GetEnvironmentVariable("QUICKPANEL_TEST_APPLY_WORKER") == "1";
    }

    private static void ValidTransactionCommitsAndPreservesProfile(string root)
    {
        Fixture fixture = CreateFixture(root, "commit", restartApplication: false);
        string profileHash = HashDirectory(fixture.Profile);
        var runner = new UpdateTransactionRunner(
            applicationStarter: _ => null);

        int exitCode = runner.Run(
            fixture.TransactionPath,
            fixture.StatePath,
            GetUpdaterExecutable());

        Need(exitCode == 0,
            "A valid guarded transaction did not succeed. " +
            (File.Exists(fixture.Transaction.LogPath)
                ? File.ReadAllText(fixture.Transaction.LogPath)
                : "No transaction log was written."));
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "new",
            "A valid guarded transaction did not install the target payload.");
        Need(File.ReadAllText(Path.Combine(fixture.Backup, "marker.txt")) == "old",
            "A valid guarded transaction did not retain the verified previous version.");
        Need(UpdateTransactionStore.ReadState(fixture.StatePath).Phase ==
            UpdateTransactionPhase.Committed,
            "A verified update and restart did not commit its transaction.");
        Need(HashDirectory(fixture.Profile) == profileHash,
            "A successful guarded transaction changed canonical profile data.");
    }

    private static void GuardianRestoresAfterRunnerTermination(string root)
    {
        Fixture fixture = CreateFixture(root, "runner-killed", restartApplication: false);
        string profileHash = HashDirectory(fixture.Profile);
        ReleasePayloadPolicy.CopyApplicationFiles(
            fixture.Install,
            fixture.Backup,
            fixture.Profile);
        ReleasePayloadPolicy.VerifyApplicationFilesCopy(
            fixture.Install,
            fixture.Backup,
            fixture.Profile);
        string rollbackFingerprint = UpdateBackupFingerprint.Compute(fixture.Backup);
        UpdateTransactionStore.Advance(
            fixture.StatePath,
            fixture.Transaction.Nonce,
            UpdateTransactionPhase.RollbackVerified,
            Environment.ProcessId,
            rollbackSha256: rollbackFingerprint);
        Need(UpdateTransactionStore.ReadState(fixture.StatePath).RollbackSha256 == rollbackFingerprint,
            "Verified rollback evidence was not persisted for guardian recovery.");

        using Process runnerPlaceholder = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Wait-Event" }
        }) ?? throw new InvalidOperationException("Could not start runner placeholder.");

        ProcessStartInfo guardianInfo = CreateTestHostInfo();
        guardianInfo.ArgumentList.Add("--guard");
        guardianInfo.ArgumentList.Add(fixture.TransactionPath);
        guardianInfo.ArgumentList.Add(fixture.StatePath);
        guardianInfo.ArgumentList.Add(runnerPlaceholder.Id.ToString());
        using Process guardian = Process.Start(guardianInfo)
            ?? throw new InvalidOperationException("Could not start guardian child process.");

        WaitForPhase(fixture.StatePath, UpdateTransactionPhase.GuardianReady);
        UpdateTransactionStore.Advance(
            fixture.StatePath,
            fixture.Transaction.Nonce,
            UpdateTransactionPhase.ParentExited,
            Environment.ProcessId);
        UpdateTransactionStore.Advance(
            fixture.StatePath,
            fixture.Transaction.Nonce,
            UpdateTransactionPhase.ReplacementStarted,
            Environment.ProcessId);
        ReleasePayloadPolicy.ReplaceApplicationFiles(
            fixture.Payload,
            fixture.Install,
            fixture.Profile);
        File.WriteAllText(Path.Combine(fixture.Install, "marker.txt"), "corrupt-partial");

        runnerPlaceholder.Kill(entireProcessTree: true);
        Need(runnerPlaceholder.WaitForExit(5000), "Runner placeholder did not terminate.");
        Need(guardian.WaitForExit(15000), "Guardian did not recover after runner termination.");
        Need(guardian.ExitCode == 0, "Guardian recovery returned failure.");
        Need(UpdateTransactionStore.ReadState(fixture.StatePath).Phase ==
            UpdateTransactionPhase.Restored,
            "Guardian did not record a verified restore.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "old",
            "Guardian did not restore the previous application files.");
        Need(HashDirectory(fixture.Profile) == profileHash,
            "Guardian recovery changed canonical profile data.");
    }

    private static ProcessStartInfo CreateTestHostInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = GetUpdaterExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(GetUpdaterExecutable())!
        };
        return info;
    }

    private static string GetUpdaterExecutable()
    {
        string path = Path.Combine(
            Environment.CurrentDirectory,
            "QuickPanel.Updater", "bin", "Release", "net8.0-windows",
            "QuickPanel.Updater.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The real updater test executable was not built.", path);
        }
        return path;
    }

    private static Fixture CreateFixture(string root, string name, bool restartApplication)
    {
        string attempt = Path.Combine(root, name);
        string install = Path.Combine(attempt, "install");
        string payload = Path.Combine(attempt, "payload");
        string profile = Path.Combine(attempt, "profile", "data");
        string backup = Path.Combine(profile + "-maintenance", "update-backups", "before-2.4.11");
        string logs = Path.Combine(profile + "-maintenance", "update-logs");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"tabs\":[\"sentinel\"]}");
        WriteApplication(install, "old");
        WriteApplication(payload, "new");

        string manifest = Path.Combine(payload, ReleasePayloadPolicy.ApplicationManifestFileName);
        string transactionPath = Path.Combine(attempt, "transaction.json");
        string statePath = Path.Combine(attempt, "state.json");
        string version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "2.4.10";
        var transaction = new UpdateTransaction(
            UpdateTransaction.CurrentSchemaVersion,
            name,
            Guid.NewGuid().ToString("N"),
            "update",
            0,
            payload,
            install,
            profile,
            Path.Combine(install, "AIQuickPanel.exe"),
            backup,
            Path.Combine(logs, "update-" + name + ".log"),
            version,
            version,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))),
            restartApplication);
        UpdateTransactionStore.Create(transactionPath, transaction);
        return new Fixture(
            attempt,
            install,
            payload,
            profile,
            backup,
            transactionPath,
            statePath,
            transaction);
    }

    private static void WriteApplication(string directory, string marker, string executable = "AIQuickPanel.exe")
    {
        foreach (string previous in new[] { "AIQuickPanel.exe", "QuickPanel.exe" })
        {
            string path = Path.Combine(directory, previous);
            if (File.Exists(path)) File.Delete(path);
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(directory, executable));
        File.WriteAllText(Path.Combine(directory, "marker.txt"), marker);
        File.WriteAllText(
            Path.Combine(directory, ReleasePayloadPolicy.ApplicationManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entries = new[]
                {
                    executable,
                    ReleasePayloadPolicy.ApplicationManifestFileName,
                    "marker.txt"
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WaitForPhase(string statePath, UpdateTransactionPhase expected)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(statePath) && UpdateTransactionStore.ReadState(statePath).Phase == expected)
            {
                return;
            }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("Timed out waiting for update phase " + expected + ".");
    }

    private static string HashDirectory(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            byte[] relative = System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file));
            hash.AppendData(relative);
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Need(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record Fixture(
        string Attempt,
        string Install,
        string Payload,
        string Profile,
        string Backup,
        string TransactionPath,
        string StatePath,
        UpdateTransaction Transaction);
}
