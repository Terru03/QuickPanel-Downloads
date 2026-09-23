using System.Diagnostics;
using QuickPanel.Services;
using QuickPanel.Updater.Core;

namespace QuickPanel.Updater;

public sealed class UpdateGuardian
{
    private readonly Func<ProcessStartInfo, Process?> _applicationStarter;
    private readonly bool _requiresStartedApplicationProcess;

    public UpdateGuardian(Func<ProcessStartInfo, Process?>? applicationStarter = null)
    {
        _applicationStarter = applicationStarter ?? Process.Start;
        _requiresStartedApplicationProcess = applicationStarter is null;
    }

    public int Run(string transactionPath, string statePath, int runnerProcessId)
    {
        UpdateTransaction? transaction = null;
        try
        {
            transaction = UpdateTransactionValidation.ReadAndValidate(
                transactionPath,
                statePath,
                requireBackup: true);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.GuardianReady,
                Environment.ProcessId);
            UpdateTransactionValidation.AppendLog(transaction, "guardian-ready");

            while (IsProcessRunning(runnerProcessId))
            {
                UpdateTransactionPhase phase = UpdateTransactionStore.ReadState(statePath).Phase;
                if (phase is UpdateTransactionPhase.Committed or UpdateTransactionPhase.Restored)
                {
                    return 0;
                }
                if (phase == UpdateTransactionPhase.Failed)
                {
                    return 1;
                }
                Thread.Sleep(100);
            }

            UpdateTransactionPhase lastPhase = UpdateTransactionStore.ReadState(statePath).Phase;
            if (lastPhase is UpdateTransactionPhase.Committed or UpdateTransactionPhase.Restored)
            {
                return 0;
            }
            UpdateTransactionRunner.WaitForProcessExit(transaction.ParentProcessId);
            if (InstalledTargetIsComplete(transaction))
            {
                CompleteVerifiedTarget(
                    statePath, transaction, lastPhase, "guardian recovered runner termination");
                UpdateTransactionValidation.AppendLog(transaction, "guardian-committed");
                return 0;
            }

            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.RestoreStarted,
                Environment.ProcessId,
                "runner exited without a verified target");
            UpdateRetryPolicy.Execute(
                () => ReleasePayloadPolicy.RestoreApplicationFiles(
                    transaction.BackupDirectory,
                    transaction.PayloadDirectory,
                    transaction.InstallDirectory,
                    transaction.CanonicalDataDirectory),
                (_, exception) => UpdateTransactionValidation.AppendLog(transaction, "guardian-restore-retry", exception));
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
                Environment.ProcessId,
                "guardian restored after runner termination");
            UpdateTransactionValidation.AppendLog(transaction, "guardian-restored");
            return 0;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                UpdateTransactionValidation.AppendLog(transaction, "guardian-failed", exception);
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
            return 1;
        }
    }

    public int Recover(string transactionPath, string statePath, int applicationProcessId)
    {
        UpdateTransaction? transaction = null;
        try
        {
            transaction = UpdateTransactionValidation.ReadAndValidateRecovery(transactionPath, statePath);
            UpdateTransactionRunner.WaitForProcessExit(applicationProcessId);
            UpdateTransactionPhase phase = UpdateTransactionStore.ReadState(statePath).Phase;
            if (phase is UpdateTransactionPhase.Committed or UpdateTransactionPhase.Restored)
            {
                return 0;
            }
            if (phase == UpdateTransactionPhase.Failed)
            {
                return 1;
            }

            if (InstalledTargetIsComplete(transaction))
            {
                CompleteVerifiedTarget(
                    statePath, transaction, phase, "startup recovery committed verified target");
                UpdateTransactionValidation.AppendLog(transaction, "startup-recovery-committed");
                return 0;
            }

            if (phase != UpdateTransactionPhase.RestoreStarted)
            {
                UpdateTransactionStore.Advance(
                    statePath, transaction.Nonce, UpdateTransactionPhase.RestoreStarted,
                    Environment.ProcessId, "startup recovery restoring verified rollback");
            }
            UpdateRetryPolicy.Execute(
                () => ReleasePayloadPolicy.RestoreApplicationFiles(
                    transaction.BackupDirectory,
                    transaction.PayloadDirectory,
                    transaction.InstallDirectory,
                    transaction.CanonicalDataDirectory),
                (_, exception) => UpdateTransactionValidation.AppendLog(
                    transaction, "startup-recovery-retry", exception));
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
                statePath, transaction.Nonce, UpdateTransactionPhase.Restored,
                Environment.ProcessId, "startup recovery restored verified rollback");
            UpdateTransactionValidation.AppendLog(transaction, "startup-recovery-restored");
            return 0;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                UpdateTransactionValidation.AppendLog(transaction, "startup-recovery-failed", exception);
                try
                {
                    UpdateTransactionStore.Advance(
                        statePath, transaction.Nonce, UpdateTransactionPhase.Failed,
                        Environment.ProcessId);
                }
                catch
                {
                }
            }
            return 1;
        }
    }

    private void CompleteVerifiedTarget(
        string statePath,
        UpdateTransaction transaction,
        UpdateTransactionPhase phase,
        string commitDetail)
    {
        AdvanceTargetRecovery(statePath, transaction, phase);
        UpdateTransactionPhase current = UpdateTransactionStore.ReadState(statePath).Phase;
        if (current == UpdateTransactionPhase.ReplacementVerified)
        {
            StartApplication(transaction, restored: false);
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.RestartStarted,
                Environment.ProcessId,
                "guardian started verified target");
            current = UpdateTransactionPhase.RestartStarted;
        }
        if (current != UpdateTransactionPhase.RestartStarted)
        {
            throw new InvalidOperationException(
                "Verified target recovery reached an unexpected transaction phase: " + current + ".");
        }
        UpdateTransactionStore.Advance(
            statePath,
            transaction.Nonce,
            UpdateTransactionPhase.Committed,
            Environment.ProcessId,
            commitDetail);
    }

    private static bool InstalledTargetIsComplete(UpdateTransaction transaction)
    {
        try
        {
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(
                transaction.PayloadDirectory,
                transaction.InstallDirectory,
                transaction.CanonicalDataDirectory);
            UpdateTransactionValidation.EnsureVersion(
                transaction.EffectiveTargetApplicationExecutable,
                transaction.ExpectedTargetVersion,
                "installed");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void AdvanceTargetRecovery(
        string statePath,
        UpdateTransaction transaction,
        UpdateTransactionPhase phase)
    {
        if (phase == UpdateTransactionPhase.RollbackVerified)
        {
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.GuardianReady,
                Environment.ProcessId);
            phase = UpdateTransactionPhase.GuardianReady;
        }
        if (phase == UpdateTransactionPhase.GuardianReady)
        {
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.ParentExited,
                Environment.ProcessId);
            phase = UpdateTransactionPhase.ParentExited;
        }
        if (phase == UpdateTransactionPhase.ParentExited)
        {
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.ReplacementStarted,
                Environment.ProcessId);
            phase = UpdateTransactionPhase.ReplacementStarted;
        }
        if (phase == UpdateTransactionPhase.ReplacementStarted)
        {
            UpdateTransactionStore.Advance(
                statePath,
                transaction.Nonce,
                UpdateTransactionPhase.ReplacementVerified,
                Environment.ProcessId);
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
            throw new InvalidOperationException("Could not restart Quick Panel during guardian recovery.");
        }
        process?.Dispose();
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
