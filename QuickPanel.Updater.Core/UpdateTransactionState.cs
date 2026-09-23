using System;

namespace QuickPanel.Updater.Core;

public enum UpdateTransactionPhase
{
    Created,
    RollbackVerified,
    GuardianReady,
    ParentExited,
    ReplacementStarted,
    ReplacementVerified,
    RestartStarted,
    Committed,
    RestoreStarted,
    Restored,
    Failed
}

public sealed record UpdateTransactionSnapshot(
    int SchemaVersion,
    string Nonce,
    UpdateTransactionPhase Phase,
    int ProcessId,
    DateTimeOffset TimestampUtc,
    string? Detail,
    string? RollbackSha256 = null);
