using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using QuickPanel.Services;
using QuickPanel.Updater.Core;

internal static class UpdaterBootstrapTests
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
            "updater-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            HandoffWaitsForGuardianReadiness(root);
            FailedGuardianLeavesParentOpen(root);
            RollbackCreatesGuardedRecoveryAndCommits(root);
            TamperedRollbackRecoveryNeverAcknowledgesParent(root);
            RollbackUsesUpdaterRuntimeFromCurrentRecoveryCopy(root);
            MissingUpdaterRuntimeNeverAcknowledgesParent(root);
            LegacyInstallAcceptsRenamedPayload(root);
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

    private static void MissingUpdaterRuntimeNeverAcknowledgesParent(string root)
    {
        Fixture fixture = CreateFixture(root, "missing-runtime");
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath,
            fixture.Request);
        diagnostics.PrepareParentHandoff();
        int exitCode = PortableUpdateWorker.ApplyRequestFile(fixture.RequestPath);
        Need(exitCode == 1, "A release without the guarded updater runtime did not fail closed.");
        Need(!File.Exists(diagnostics.HandoffPath),
            "A release without the guarded updater runtime acknowledged parent shutdown.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "old",
            "A release without the guarded updater runtime changed the stable install.");
    }

    private static void LegacyInstallAcceptsRenamedPayload(string root)
    {
        Fixture fixture = CreateFixture(root, "renamed-payload");
        File.Delete(Path.Combine(fixture.Request.PayloadDirectory, "AIQuickPanel.exe"));
        File.Copy(Environment.ProcessPath!, Path.Combine(fixture.Request.PayloadDirectory, "QuickPanel.exe"));
        WriteApplicationManifest(fixture.Request.PayloadDirectory, includeUpdaterRuntime: false,
            executable: "QuickPanel.exe");
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath, fixture.Request);
        diagnostics.PrepareParentHandoff();
        UpdaterBootstrapResult result = UpdaterBootstrap.PrepareAndLaunch(
            fixture.Request, fixture.RequestPath, diagnostics, fixture.UpdaterRuntime,
            transactionRoot: Path.Combine(fixture.Attempt, "transactions"));
        using Process runner = Process.GetProcessById(result.RunnerProcessId);
        Need(runner.WaitForExit(15000) &&
            UpdateTransactionStore.ReadState(result.StatePath).Phase == UpdateTransactionPhase.Committed,
            "Legacy install did not commit a renamed payload.");
        UpdateTransaction transaction = UpdateTransactionStore.ReadTransaction(result.TransactionPath);
        Need(transaction.TargetApplicationExecutable == Path.Combine(fixture.Install, "QuickPanel.exe") &&
            File.Exists(transaction.TargetApplicationExecutable),
            "Renamed payload did not write the target executable identity.");
    }

    private static void RollbackCreatesGuardedRecoveryAndCommits(string root)
    {
        Fixture fixture = CreateFixture(root, "rollback");
        string recovery = Path.Combine(fixture.Attempt, "temporary-current-copy");
        ReleasePayloadPolicy.CopyApplicationFiles(
            fixture.Install,
            recovery,
            fixture.Request.CanonicalDataDirectory);
        PortableUpdateRequest rollbackRequest = fixture.Request with
        {
            Operation = "rollback",
            RollbackDirectory = null,
            RecoveryDirectory = recovery
        };
        PortableUpdateWorker.WriteRequest(fixture.RequestPath, rollbackRequest);
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath,
            rollbackRequest);
        diagnostics.PrepareParentHandoff();

        UpdaterBootstrapResult result = UpdaterBootstrap.PrepareAndLaunch(
            rollbackRequest,
            fixture.RequestPath,
            diagnostics,
            fixture.UpdaterRuntime,
            transactionRoot: Path.Combine(fixture.Attempt, "transactions"));
        using Process runner = Process.GetProcessById(result.RunnerProcessId);
        Need(runner.WaitForExit(15000), "The guarded rollback runner did not finish.");
        UpdateTransaction transaction = UpdateTransactionStore.ReadTransaction(result.TransactionPath);
        Need(UpdateTransactionStore.ReadState(result.StatePath).Phase == UpdateTransactionPhase.Committed,
            "The guarded rollback did not commit.");
        Need(Directory.Exists(transaction.BackupDirectory) &&
             Path.GetDirectoryName(transaction.BackupDirectory)!.Equals(
                 PortableDataPaths.GetUpdateBackupDirectory(fixture.Request.CanonicalDataDirectory),
                 StringComparison.OrdinalIgnoreCase),
            "The guarded rollback did not retain a durable verified pre-rollback backup.");
    }

    private static void RollbackUsesUpdaterRuntimeFromCurrentRecoveryCopy(string root)
    {
        Fixture fixture = CreateFixture(root, "rollback-current-runtime");
        File.WriteAllText(Path.Combine(fixture.Install, "marker.txt"), "newer");
        File.WriteAllText(Path.Combine(fixture.Request.PayloadDirectory, "marker.txt"), "older");
        CopyDirectory(
            fixture.UpdaterRuntime,
            Path.Combine(fixture.Install, "UpdaterRuntime"));
        WriteApplicationManifest(fixture.Install, includeUpdaterRuntime: true);

        string recovery = Path.Combine(fixture.Attempt, "temporary-current-copy");
        ReleasePayloadPolicy.CopyApplicationFiles(
            fixture.Install,
            recovery,
            fixture.Request.CanonicalDataDirectory);
        PortableUpdateRequest rollbackRequest = fixture.Request with
        {
            Operation = "rollback",
            RollbackDirectory = null,
            RecoveryDirectory = recovery
        };
        PortableUpdateWorker.WriteRequest(fixture.RequestPath, rollbackRequest);
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath,
            rollbackRequest);
        diagnostics.PrepareParentHandoff();

        int exitCode = PortableUpdateWorker.ApplyRequestFile(fixture.RequestPath);

        Need(exitCode == 0,
            "Rollback did not start when only the verified current recovery copy contained the updater runtime.");
        Need(File.Exists(diagnostics.HandoffPath),
            "Rollback did not acknowledge the parent after its guarded updater became ready.");
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15) &&
               (!FileContains(Path.Combine(fixture.Install, "marker.txt"), "older") ||
                !FileContains(diagnostics.DurableLogPath, "stage=committed")))
        {
            Thread.Sleep(50);
        }
        Need(FileContains(Path.Combine(fixture.Install, "marker.txt"), "older"),
            "Rollback did not install the selected older application backup.");
        Need(FileContains(diagnostics.DurableLogPath, "stage=committed"),
            "Rollback did not commit its guarded transaction.");
    }

    private static void TamperedRollbackRecoveryNeverAcknowledgesParent(string root)
    {
        Fixture fixture = CreateFixture(root, "rollback-tampered-recovery");
        File.WriteAllText(Path.Combine(fixture.Install, "marker.txt"), "newer");
        File.WriteAllText(Path.Combine(fixture.Request.PayloadDirectory, "marker.txt"), "older");
        CopyDirectory(
            fixture.UpdaterRuntime,
            Path.Combine(fixture.Install, "UpdaterRuntime"));
        CopyDirectory(
            fixture.UpdaterRuntime,
            Path.Combine(fixture.Request.PayloadDirectory, "UpdaterRuntime"));
        WriteApplicationManifest(fixture.Install, includeUpdaterRuntime: true);
        WriteApplicationManifest(fixture.Request.PayloadDirectory, includeUpdaterRuntime: true);

        string recovery = Path.Combine(fixture.Attempt, "temporary-current-copy");
        ReleasePayloadPolicy.CopyApplicationFiles(
            fixture.Install,
            recovery,
            fixture.Request.CanonicalDataDirectory);
        File.WriteAllText(Path.Combine(recovery, "marker.txt"), "tampered");
        PortableUpdateRequest rollbackRequest = fixture.Request with
        {
            Operation = "rollback",
            RollbackDirectory = null,
            RecoveryDirectory = recovery
        };
        PortableUpdateWorker.WriteRequest(fixture.RequestPath, rollbackRequest);
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath,
            rollbackRequest);
        diagnostics.PrepareParentHandoff();

        int exitCode = PortableUpdateWorker.ApplyRequestFile(fixture.RequestPath);

        Need(exitCode == 1, "Rollback accepted a recovery copy that did not match the current install.");
        Need(!File.Exists(diagnostics.HandoffPath),
            "Rollback acknowledged parent shutdown before validating its recovery copy.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "newer",
            "Rollback changed the stable install after rejecting its recovery copy.");
    }

    private static bool IsNestedTestProcess()
    {
        return Environment.GetEnvironmentVariable("QUICKPANEL_TEST_CHILD") == "1" ||
            Environment.GetEnvironmentVariable("QUICKPANEL_TEST_HANDOFF_WORKER") == "1" ||
            Environment.GetEnvironmentVariable("QUICKPANEL_TEST_APPLY_WORKER") == "1";
    }

    private static void HandoffWaitsForGuardianReadiness(string root)
    {
        Fixture fixture = CreateFixture(root, "ready");
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath,
            fixture.Request);
        diagnostics.PrepareParentHandoff();

        UpdaterBootstrapResult result = UpdaterBootstrap.PrepareAndLaunch(
            fixture.Request,
            fixture.RequestPath,
            diagnostics,
            fixture.UpdaterRuntime,
            transactionRoot: Path.Combine(fixture.Attempt, "transactions"));

        Need(File.Exists(diagnostics.HandoffPath),
            "The bootstrap did not acknowledge a guarded updater.");
        UpdateTransactionSnapshot acknowledged = UpdateTransactionStore.ReadState(result.StatePath);
        Need(acknowledged.Phase >= UpdateTransactionPhase.GuardianReady,
            "The bootstrap acknowledged before the guardian was ready.");
        using Process runner = Process.GetProcessById(result.RunnerProcessId);
        Need(runner.WaitForExit(15000), "The guarded update runner did not finish.");
        Need(UpdateTransactionStore.ReadState(result.StatePath).Phase == UpdateTransactionPhase.Committed,
            "The guarded bootstrap update did not commit.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "new",
            "The guarded bootstrap did not update the stable install.");
    }

    private static void FailedGuardianLeavesParentOpen(string root)
    {
        Fixture fixture = CreateFixture(root, "guardian-failure");
        PortableUpdateDiagnostics diagnostics = PortableUpdateDiagnostics.Create(
            fixture.RequestPath,
            fixture.Request);
        diagnostics.PrepareParentHandoff();

        NeedThrows<InvalidOperationException>(() => UpdaterBootstrap.PrepareAndLaunch(
            fixture.Request,
            fixture.RequestPath,
            diagnostics,
            fixture.UpdaterRuntime,
            Path.Combine(fixture.Attempt, "missing-guardian.exe"),
            Path.Combine(fixture.Attempt, "transactions")));
        Need(!File.Exists(diagnostics.HandoffPath),
            "A failed guardian start acknowledged the parent.");
        Need(File.ReadAllText(Path.Combine(fixture.Install, "marker.txt")) == "old",
            "A failed guardian start mutated the stable install.");
    }

    private static Fixture CreateFixture(string root, string name)
    {
        string attempt = Path.Combine(root, name);
        string install = Path.Combine(attempt, "install");
        string payload = Path.Combine(attempt, "payload");
        string profile = Path.Combine(attempt, "profile", "data");
        string rollback = Path.Combine(profile + "-maintenance", "update-backups", name);
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(profile);
        WriteApplication(install, "old");
        WriteApplication(payload, "new");
        string requestPath = Path.Combine(attempt, "update-request.json");
        string logPath = Path.Combine(attempt, "update.log");
        var request = new PortableUpdateRequest(
            "update", 0, payload, install, profile,
            Path.Combine(install, "AIQuickPanel.exe"), rollback, null, logPath,
            RestartApplication: false);
        PortableUpdateWorker.WriteRequest(requestPath, request);
        string updaterRuntime = Path.Combine(
            Environment.CurrentDirectory,
            "QuickPanel.Updater", "bin", "Release", "net8.0-windows");
        Need(File.Exists(Path.Combine(updaterRuntime, "QuickPanel.Updater.exe")),
            "The updater runtime fixture was not built.");
        return new Fixture(attempt, install, requestPath, updaterRuntime, request);
    }

    private static void WriteApplication(string directory, string marker)
    {
        File.Copy(Environment.ProcessPath!, Path.Combine(directory, "AIQuickPanel.exe"));
        File.WriteAllText(Path.Combine(directory, "marker.txt"), marker);
        WriteApplicationManifest(directory, includeUpdaterRuntime: false);
    }

    private static void WriteApplicationManifest(string directory, bool includeUpdaterRuntime,
        string executable = "AIQuickPanel.exe")
    {
        File.WriteAllText(
            Path.Combine(directory, ReleasePayloadPolicy.ApplicationManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entries = includeUpdaterRuntime
                    ? new[]
                    {
                        executable,
                        ReleasePayloadPolicy.ApplicationManifestFileName,
                        "marker.txt",
                        "UpdaterRuntime"
                    }
                    : new[]
                    {
                        executable,
                        ReleasePayloadPolicy.ApplicationManifestFileName,
                        "marker.txt"
                    }
            }));
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static bool FileContains(string path, string expected)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Contains(expected, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void NeedThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
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
        string RequestPath,
        string UpdaterRuntime,
        PortableUpdateRequest Request);
}
