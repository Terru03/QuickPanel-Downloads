using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using QuickPanel.Updater.Core;

namespace QuickPanel.Services;

internal enum UpdateRecoveryLaunchResult
{
    None,
    RecoveryStarted,
    NeedsAttention
}

internal static class UpdateRecoveryService
{
    internal static string? StartupAttentionMessage { get; private set; }

    internal static string DefaultTransactionsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "QuickPanel-Update", "transactions");

    internal static string LegacyTransactionsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "AIQuickPanel-Update", "transactions");

    internal static UpdateRecoveryLaunchResult TryStartRecovery(
        string? currentExecutable = null,
        int? currentProcessId = null,
        string? transactionsRoot = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        StartupAttentionMessage = null;
        string[] roots = transactionsRoot is null
            ? [DefaultTransactionsRoot, LegacyTransactionsRoot]
            : [Path.GetFullPath(transactionsRoot)];
        if (!roots.Any(Directory.Exists))
        {
            return UpdateRecoveryLaunchResult.None;
        }
        try
        {
            foreach (string root in roots.Where(Directory.Exists))
            {
                ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(root);
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return NeedsAttention();
        }

        string executable = Path.GetFullPath(currentExecutable ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The running application path is unavailable."));
        var candidates = new List<(string Transaction, string State, string Updater)>();
        bool invalidMatchingEvidence = false;
        foreach (string attempt in roots.Where(Directory.Exists)
                     .SelectMany(root => Directory.EnumerateDirectories(root))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            string transactionPath = Path.Combine(attempt, "transaction.json");
            string statePath = Path.Combine(attempt, "state.json");
            if (!File.Exists(transactionPath) || !File.Exists(statePath))
            {
                continue;
            }
            try
            {
                ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(attempt);
                UpdateTransaction transaction = UpdateTransactionStore.ReadTransaction(transactionPath);
                UpdateTransactionSnapshot state = UpdateTransactionStore.ReadState(statePath);
                if (!transaction.ApplicationExecutable.Equals(executable, StringComparison.OrdinalIgnoreCase) &&
                    !transaction.EffectiveTargetApplicationExecutable.Equals(executable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!state.Nonce.Equals(transaction.Nonce, StringComparison.Ordinal))
                {
                    invalidMatchingEvidence = true;
                    continue;
                }
                if (state.Phase is UpdateTransactionPhase.Committed or
                    UpdateTransactionPhase.Restored or UpdateTransactionPhase.Failed)
                {
                    continue;
                }
                string runtime = Path.Combine(attempt, "runtime");
                string[] updaterCandidates = ["QuickPanel.Updater.exe", "AIQuickPanel.Updater.exe"];
                string[] found = updaterCandidates.Select(name => Path.Combine(runtime, name))
                    .Where(File.Exists).ToArray();
                if (found.Length != 1)
                {
                    invalidMatchingEvidence = true;
                    continue;
                }
                string updater = found[0];
                ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(updater);
                if (!File.Exists(updater))
                {
                    invalidMatchingEvidence = true;
                    continue;
                }
                candidates.Add((transactionPath, statePath, updater));
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                invalidMatchingEvidence = true;
            }
        }
        if (candidates.Count == 0)
        {
            return invalidMatchingEvidence
                ? NeedsAttention()
                : UpdateRecoveryLaunchResult.None;
        }
        if (candidates.Count != 1)
        {
            return NeedsAttention();
        }

        (string selectedTransaction, string selectedState, string updaterExe) = candidates[0];
        var info = new ProcessStartInfo
        {
            FileName = updaterExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterExe)!
        };
        info.ArgumentList.Add("--recover");
        info.ArgumentList.Add(selectedTransaction);
        info.ArgumentList.Add(selectedState);
        info.ArgumentList.Add((currentProcessId ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture));
        Process? process = (processStarter ?? Process.Start)(info);
        if (process is null)
        {
            return NeedsAttention();
        }
        process.Dispose();
        return UpdateRecoveryLaunchResult.RecoveryStarted;
    }

    private static UpdateRecoveryLaunchResult NeedsAttention()
    {
        StartupAttentionMessage =
            "An interrupted update needs attention. Quick Panel left the profile and recovery files unchanged.";
        return UpdateRecoveryLaunchResult.NeedsAttention;
    }

    private static bool IsExpected(Exception exception) =>
        exception is ArgumentException or NotSupportedException or IOException or
            InvalidDataException or UnauthorizedAccessException or InvalidOperationException;
}
