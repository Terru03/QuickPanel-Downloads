using System.Diagnostics;
using System.Security.Cryptography;
using QuickPanel.Services;
using QuickPanel.Updater.Core;

namespace QuickPanel.Updater;

internal static class UpdateTransactionValidation
{
    internal static UpdateTransaction ReadAndValidate(
        string transactionPath,
        string statePath,
        bool requireBackup)
    {
        string transactionFile = Path.GetFullPath(transactionPath);
        string stateFile = Path.GetFullPath(statePath);
        string transactionDirectory = Path.GetDirectoryName(transactionFile)
            ?? throw new InvalidDataException("Update transaction has no parent directory.");
        if (!Path.GetDirectoryName(stateFile)!.Equals(
                transactionDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update transaction state must be beside the transaction document.");
        }
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(transactionDirectory);

        UpdateTransaction transaction = UpdateTransactionStore.ReadTransaction(transactionFile);
        string install = Path.GetFullPath(transaction.InstallDirectory);
        string payload = Path.GetFullPath(transaction.PayloadDirectory);
        string profile = Path.GetFullPath(transaction.CanonicalDataDirectory);
        string backup = Path.GetFullPath(transaction.BackupDirectory);
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
        ReleasePayloadPolicy.EnsureApplicationInstallIdentity(install, requireManifest: true);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(install, profile);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(payload, profile);
        PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(backup, install, profile);
        PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(transaction.LogPath, install, profile);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(payload);

        string manifest = Path.Combine(payload, ReleasePayloadPolicy.ApplicationManifestFileName);
        string actualManifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualManifestHash),
                Convert.FromHexString(transaction.PayloadManifestSha256)))
        {
            throw new InvalidDataException("Update payload manifest hash does not match the transaction.");
        }
        EnsureVersion(transaction.ApplicationExecutable, transaction.ExpectedCurrentVersion, "installed");
        EnsureVersion(
            Path.Combine(payload, Path.GetFileName(transaction.EffectiveTargetApplicationExecutable)),
            transaction.ExpectedTargetVersion,
            "payload");
        if (requireBackup)
        {
            ReleasePayloadPolicy.VerifyApplicationFilesCopy(install, backup, profile);
            UpdateTransactionSnapshot state = UpdateTransactionStore.ReadState(stateFile);
            if (string.IsNullOrWhiteSpace(state.RollbackSha256) ||
                !state.RollbackSha256.Equals(
                    UpdateBackupFingerprint.Compute(backup),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Verified rollback fingerprint does not match the transaction state.");
            }
        }
        return transaction;
    }

    internal static UpdateTransaction ReadAndValidateRecovery(
        string transactionPath,
        string statePath)
    {
        string transactionFile = Path.GetFullPath(transactionPath);
        string stateFile = Path.GetFullPath(statePath);
        string transactionDirectory = Path.GetDirectoryName(transactionFile)
            ?? throw new InvalidDataException("Update transaction has no parent directory.");
        if (!Path.GetDirectoryName(stateFile)!.Equals(
                transactionDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update transaction state must be beside the transaction document.");
        }
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(transactionDirectory);
        UpdateTransaction transaction = UpdateTransactionStore.ReadTransaction(transactionFile);
        UpdateTransactionSnapshot state = UpdateTransactionStore.ReadState(stateFile);
        if (!state.Nonce.Equals(transaction.Nonce, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.RollbackSha256))
        {
            throw new InvalidDataException("Incomplete update has no matching verified rollback evidence.");
        }

        string install = Path.GetFullPath(transaction.InstallDirectory);
        string payload = Path.GetFullPath(transaction.PayloadDirectory);
        string profile = Path.GetFullPath(transaction.CanonicalDataDirectory);
        string backup = Path.GetFullPath(transaction.BackupDirectory);
        PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(install);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(install, profile);
        ReleasePayloadPolicy.EnsureSafeInstallTarget(payload, profile);
        PortableUpdatePathSafety.EnsureUpdateBackupDestinationSafe(backup, install, profile);
        PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(transaction.LogPath, install, profile);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(payload);
        ReleasePayloadPolicy.EnsurePayloadContainsNoProfile(backup);
        string manifest = Path.Combine(payload, ReleasePayloadPolicy.ApplicationManifestFileName);
        string actualManifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        if (!actualManifestHash.Equals(transaction.PayloadManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update payload manifest hash does not match the transaction.");
        }
        EnsureVersion(Path.Combine(payload, Path.GetFileName(transaction.EffectiveTargetApplicationExecutable)),
            transaction.ExpectedTargetVersion, "payload");
        EnsureVersion(Path.Combine(backup, Path.GetFileName(transaction.ApplicationExecutable)),
            transaction.ExpectedCurrentVersion, "rollback");
        if (!state.RollbackSha256.Equals(
                UpdateBackupFingerprint.Compute(backup),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Verified rollback fingerprint changed before startup recovery.");
        }
        return transaction;
    }

    internal static void EnsureVersion(string executable, string expected, string kind)
    {
        string? actual = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
        if (!Version.TryParse(actual, out Version? actualVersion) ||
            !Version.TryParse(expected, out Version? expectedVersion) ||
            actualVersion != expectedVersion)
        {
            throw new InvalidDataException(
                $"The {kind} application version does not match the update transaction.");
        }
    }

    internal static void AppendLog(UpdateTransaction transaction, string stage, Exception? exception = null)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(transaction.LogPath))!;
            Directory.CreateDirectory(directory);
            string detail = exception is null
                ? string.Empty
                : $"; error={exception.GetType().Name}; hresult=0x{exception.HResult:X8}";
            File.AppendAllText(
                transaction.LogPath,
                $"[{DateTimeOffset.UtcNow:O}] stage={stage}; process-id={Environment.ProcessId}{detail}{Environment.NewLine}");
        }
        catch
        {
            // Recovery must continue even when a diagnostic sink is blocked.
        }
    }
}
