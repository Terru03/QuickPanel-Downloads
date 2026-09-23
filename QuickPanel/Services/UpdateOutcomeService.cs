using System;
using System.IO;
using System.Linq;
using QuickPanel.Updater.Core;

namespace QuickPanel.Services;

internal sealed record UpdateOutcome(string Message, string LogPath);

internal static class UpdateOutcomeService
{
    internal static UpdateOutcome? TryConsumeLatest(
        string? currentExecutable = null,
        string? transactionsRoot = null)
    {
        string root = Path.GetFullPath(transactionsRoot ?? UpdateRecoveryService.DefaultTransactionsRoot);
        if (!Directory.Exists(root))
        {
            return null;
        }
        string executable = Path.GetFullPath(currentExecutable ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The running application path is unavailable."));
        foreach (string attempt in Directory.EnumerateDirectories(root)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            string consumed = Path.Combine(attempt, "outcome-consumed");
            if (File.Exists(consumed))
            {
                continue;
            }
            try
            {
                UpdateTransaction transaction = UpdateTransactionStore.ReadTransaction(
                    Path.Combine(attempt, "transaction.json"));
                UpdateTransactionSnapshot state = UpdateTransactionStore.ReadState(
                    Path.Combine(attempt, "state.json"));
                if (!transaction.ApplicationExecutable.Equals(executable, StringComparison.OrdinalIgnoreCase) ||
                    !state.Nonce.Equals(transaction.Nonce, StringComparison.Ordinal) ||
                    state.Phase is not (UpdateTransactionPhase.Committed or
                        UpdateTransactionPhase.Restored or UpdateTransactionPhase.Failed))
                {
                    continue;
                }
                string message = state.Phase switch
                {
                    UpdateTransactionPhase.Committed =>
                        "Quick Panel updated successfully to v" + transaction.ExpectedTargetVersion + ".",
                    UpdateTransactionPhase.Restored =>
                        "The update could not finish, so Quick Panel automatically restored v" +
                        transaction.ExpectedCurrentVersion + ". Your profile was preserved.",
                    _ => "The last update needs attention. Your profile and recovery files were preserved."
                };
                using (File.Open(consumed, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return new UpdateOutcome(message, transaction.LogPath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or
                InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Preserve malformed evidence for diagnosis and continue to older valid outcomes.
            }
        }
        return null;
    }
}
