using System;
using System.Collections.Generic;

namespace QuickPanel.Services;

public enum ProfileMigrationStatus
{
    NoProfile,
    CanonicalPreserved,
    Migrated,
    AlreadyComplete,
    Ambiguous,
    Unsafe,
    Failed
}

public enum ProfileMigrationInspectionMode
{
    DeepAssessment,
    MarkerFastPath
}

public sealed record ProfileMigrationCandidate(string Kind, string Directory, bool IsProductRoot);

public sealed record ProfileAccessCheck(bool IsSafe, string Message)
{
    public static ProfileAccessCheck Safe { get; } = new(true, string.Empty);
}

public sealed record ProfileMigrationRequest(
    string CanonicalDirectory,
    string WorkRoot,
    IReadOnlyList<ProfileMigrationCandidate> LegacyCandidates,
    Func<IReadOnlyCollection<string>, ProfileAccessCheck> AccessCheck,
    Action<string>? FaultInjection = null);

public sealed record ProfileAssessment(
    string Directory,
    bool Exists,
    int DurableTier,
    int SettingsItemCount,
    long SettingsBytes,
    int CookieDatabaseCount,
    long CookieBytes,
    int LocalStorageFileCount,
    int IndexedDbFileCount,
    int WebViewFileCount,
    long WebViewBytes,
    int AuxiliaryStateCount = 0);

public sealed record ProfileMigrationResult(
    ProfileMigrationStatus Status,
    string? SourceDirectory,
    string? BackupDirectory,
    string Message,
    IReadOnlyList<ProfileAssessment>? CandidateAssessments = null,
    ProfileMigrationInspectionMode InspectionMode = ProfileMigrationInspectionMode.DeepAssessment);
