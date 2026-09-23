using System.Diagnostics;
using QuickPanel.Services;
using QuickPanel.Updater.Core;

namespace QuickPanel.Updater;

public sealed class UpdateTransactionRunner
{
    private readonly Func<ProcessStartInfo, Process?> _guardianStarter;
    private readonly Func<ProcessStartInfo, Process?> _applicationStarter;
    private readonly bool _requiresStartedApplicationProcess;

    public UpdateTransactionRunner(
        Func<ProcessStartInfo, Process?>? guardianStarter = null,
        Func<ProcessStartInfo, Process?>? applicationStarter = null)
    {
        _guardianStarter = guardianStarter ?? Process.Start;
        _applicationStarter = applicationStarter ?? Process.Start;
        _requiresStartedApplicationProcess = applicationStarter is null;
    }

    public int Run(string transactionPath, string statePath, string guardianExecutable)
    {
        UpdateTransaction? transaction = null;
        Process? guardian = null;
        bool installMayBeMutated = false;
        try
        {
            transaction = UpdateTransactionValidation.ReadAndValidate(
                transactionPath,
                statePath,
                requireBackup: false);
            string guardianPath = Path.GetFullPath(guardianExecutable);
            if (!File.Exists(guardianPath))
            {
                throw new FileNotFoundException("Update guardian executable was not found.", guardianPath);
            }

            ReleasePayloadPolicy.CopyApplicationFiles(
                transaction.InstallDirectory,
                transaction.BackupDirectory,
                transaction.CanonicalDataDirectory);
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(
                transaction.InstallDirectory,
                transaction.BackupDirectory,
                transaction.CanonicalDataDirectory);
            string rollbackSha256 = UpdateBackupFingerprint.Compute(transaction.BackupDirectory);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.RollbackVerified,
                Environment.ProcessId,
                rollbackSha256: rollbackSha256);
            UpdateTransactionValidation.AppendLog(transaction, "rollback-verified");

            var guardianInfo = new ProcessStartInfo
            {
                FileName = guardianPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(guardianPath)!,
                CreateNoWindow = true
            };
            guardianInfo.ArgumentList.Add("--guard");
            guardianInfo.ArgumentList.Add(Path.GetFullPath(transactionPath));
            guardianInfo.ArgumentList.Add(Path.GetFullPath(statePath));
            guardianInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            guardian = _guardianStarter(guardianInfo)
                ?? throw new InvalidOperationException("Could not start update guardian.");
            WaitForGuardianReady(statePath, guardian);

            WaitForProcessExit(transaction.ParentProcessId);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.ParentExited,
                Environment.ProcessId);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.ReplacementStarted,
                Environment.ProcessId);
            installMayBeMutated = true;
            UpdateTransactionValidation.AppendLog(transaction, "replacement-started");
            UpdateRetryPolicy.Execute(
                () => ReleasePayloadPolicy.ReplaceApplicationFiles(
                    transaction.PayloadDirectory,
                    transaction.InstallDirectory,
                    transaction.CanonicalDataDirectory,
                    requireExistingInstallIdentity: false),
                (_, exception) => UpdateTransactionValidation.AppendLog(transaction, "replacement-retry", exception));
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(
                transaction.PayloadDirectory,
                transaction.InstallDirectory,
                transaction.CanonicalDataDirectory);
            UpdateTransactionValidation.EnsureVersion(
                transaction.EffectiveTargetApplicationExecutable,
                transaction.ExpectedTargetVersion,
                "installed");
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.ReplacementVerified,
                Environment.ProcessId);

            StartApplication(transaction, restored: false);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.RestartStarted,
                Environment.ProcessId,
                transaction.RestartApplication ? null : "restart not requested");
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.Committed,
                Environment.ProcessId);
            UpdateTransactionValidation.AppendLog(transaction, "committed");
            guardian.WaitForExit(5000);
            return 0;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                UpdateTransactionValidation.AppendLog(transaction, "runner-failed", exception);
                if (installMayBeMutated)
                {
                    TryRestore(transaction, statePath);
                }
                else
                {
                    TryFail(transaction, statePath);
                }
            }
            return 1;
        }
        finally
        {
            guardian?.Dispose();
        }
    }

    private void StartApplication(UpdateTransaction transaction, bool restored)
    {
        if (!transaction.RestartApplication)
        {
            return;
        }
        var info = new ProcessStartInfo
        {
            FileName = restored ? transaction.ApplicationExecutable : transaction.EffectiveTargetApplicationExecutable,
            UseShellExecute = true,
            WorkingDirectory = transaction.InstallDirectory
        };
        Process? process = _applicationStarter(info);
        if (process is null && _requiresStartedApplicationProcess)
        {
            throw new InvalidOperationException("Could not restart Quick Panel after update.");
        }
        process?.Dispose();
    }

    private void TryRestore(UpdateTransaction transaction, string statePath)
    {
        try
        {
            if (!Directory.Exists(transaction.BackupDirectory))
            {
                TryFail(transaction, statePath);
                return;
            }
            UpdateTransactionSnapshot state = UpdateTransactionStore.ReadState(statePath);
            if (state.Phase is UpdateTransactionPhase.Committed or UpdateTransactionPhase.Restored or
                UpdateTransactionPhase.Failed)
            {
                return;
            }
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.RestoreStarted,
                Environment.ProcessId);
            UpdateRetryPolicy.Execute(() => ReleasePayloadPolicy.RestoreApplicationFiles(
                transaction.BackupDirectory,
                transaction.PayloadDirectory,
                transaction.InstallDirectory,
                transaction.CanonicalDataDirectory));
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(
                transaction.BackupDirectory,
                transaction.InstallDirectory,
                transaction.CanonicalDataDirectory);
            UpdateTransactionValidation.EnsureVersion(
                transaction.ApplicationExecutable,
                transaction.ExpectedCurrentVersion,
                "restored");
            StartApplication(transaction, restored: true);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.Restored,
                Environment.ProcessId);
            UpdateTransactionValidation.AppendLog(transaction, "restored");
        }
        catch (Exception restoreException)
        {
            UpdateTransactionValidation.AppendLog(transaction, "restore-failed", restoreException);
            TryFail(transaction, statePath);
        }
    }

    private static void TryFail(UpdateTransaction transaction, string statePath)
    {
        try
        {
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.Failed,
                Environment.ProcessId);
        }
        catch
        {
        }
    }

    private static void WaitForGuardianReady(string statePath, Process guardian)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(statePath) &&
                UpdateTransactionStore.ReadState(statePath).Phase == UpdateTransactionPhase.GuardianReady)
            {
                return;
            }
            guardian.Refresh();
            if (guardian.HasExited)
            {
                throw new InvalidOperationException("Update guardian exited before becoming ready.");
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Update guardian did not become ready within 15 seconds.");
    }

    internal static void WaitForProcessExit(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            return;
        }
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
        }
    }
}
