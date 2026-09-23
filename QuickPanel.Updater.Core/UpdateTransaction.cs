using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace QuickPanel.Updater.Core;

public sealed record UpdateTransaction(
    int SchemaVersion,
    string AttemptId,
    string Nonce,
    string Operation,
    int ParentProcessId,
    string PayloadDirectory,
    string InstallDirectory,
    string CanonicalDataDirectory,
    string ApplicationExecutable,
    string BackupDirectory,
    string LogPath,
    string ExpectedCurrentVersion,
    string ExpectedTargetVersion,
    string PayloadManifestSha256,
    bool RestartApplication)
{
    public const int CurrentSchemaVersion = 1;

    // Absent in transactions created before the executable rename.
    public string? TargetApplicationExecutable { get; init; }

    [JsonIgnore]
    public string EffectiveTargetApplicationExecutable =>
        TargetApplicationExecutable ?? ApplicationExecutable;

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Unsupported update transaction schema.");
        }
        RequireValue(AttemptId, nameof(AttemptId));
        RequireValue(Nonce, nameof(Nonce));
        RequireValue(Operation, nameof(Operation));
        if (!Operation.Equals("update", StringComparison.OrdinalIgnoreCase) &&
            !Operation.Equals("rollback", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unknown update transaction operation.");
        }
        if (ParentProcessId < 0)
        {
            throw new InvalidDataException("Update transaction parent process ID is invalid.");
        }

        string payload = RequireFullPath(PayloadDirectory, nameof(PayloadDirectory));
        string install = RequireFullPath(InstallDirectory, nameof(InstallDirectory));
        _ = RequireFullPath(CanonicalDataDirectory, nameof(CanonicalDataDirectory));
        string executable = RequireFullPath(ApplicationExecutable, nameof(ApplicationExecutable));
        string targetExecutable = RequireFullPath(EffectiveTargetApplicationExecutable,
            nameof(TargetApplicationExecutable));
        _ = RequireFullPath(BackupDirectory, nameof(BackupDirectory));
        _ = RequireFullPath(LogPath, nameof(LogPath));
        if (!IsSupportedInstallExecutable(executable, install) ||
            !IsSupportedInstallExecutable(targetExecutable, install))
        {
            throw new InvalidDataException(
                "Update transaction restart executable is outside the installation directory.");
        }
        if (payload.Equals(install, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update payload and installation directories must be different.");
        }

        if (!Version.TryParse(ExpectedCurrentVersion, out _) ||
            !Version.TryParse(ExpectedTargetVersion, out _))
        {
            throw new InvalidDataException("Update transaction versions are invalid.");
        }
        if (PayloadManifestSha256.Length != 64 ||
            PayloadManifestSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Update transaction manifest SHA256 is invalid.");
        }
    }

    private static bool IsSupportedInstallExecutable(string executable, string install) =>
        executable.Equals(Path.Combine(install, "AIQuickPanel.exe"), StringComparison.OrdinalIgnoreCase) ||
        executable.Equals(Path.Combine(install, "QuickPanel.exe"), StringComparison.OrdinalIgnoreCase);

    private static void RequireValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Update transaction {name} is required.");
        }
    }

    private static string RequireFullPath(string? value, string name)
    {
        RequireValue(value, name);
        try
        {
            string fullPath = Path.GetFullPath(value!);
            if (!Path.IsPathFullyQualified(fullPath))
            {
                throw new InvalidDataException($"Update transaction {name} must be absolute.");
            }
            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Update transaction {name} is invalid.", exception);
        }
    }
}
