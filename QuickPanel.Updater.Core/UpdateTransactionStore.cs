using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPanel.Updater.Core;

public static class UpdateTransactionStore
{
    private const int StateSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly IReadOnlyDictionary<UpdateTransactionPhase, UpdateTransactionPhase[]> Transitions =
        new Dictionary<UpdateTransactionPhase, UpdateTransactionPhase[]>
        {
            [UpdateTransactionPhase.Created] =
                [UpdateTransactionPhase.RollbackVerified, UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.RollbackVerified] =
                [UpdateTransactionPhase.GuardianReady, UpdateTransactionPhase.RestoreStarted,
                    UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.GuardianReady] =
                [UpdateTransactionPhase.ParentExited, UpdateTransactionPhase.RestoreStarted,
                    UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.ParentExited] =
                [UpdateTransactionPhase.ReplacementStarted, UpdateTransactionPhase.RestoreStarted,
                    UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.ReplacementStarted] =
                [UpdateTransactionPhase.ReplacementVerified, UpdateTransactionPhase.RestoreStarted,
                    UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.ReplacementVerified] =
                [UpdateTransactionPhase.RestartStarted, UpdateTransactionPhase.RestoreStarted,
                    UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.RestartStarted] =
                [UpdateTransactionPhase.Committed, UpdateTransactionPhase.RestoreStarted,
                    UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.RestoreStarted] =
                [UpdateTransactionPhase.Restored, UpdateTransactionPhase.Failed],
            [UpdateTransactionPhase.Committed] = [],
            [UpdateTransactionPhase.Restored] = [],
            [UpdateTransactionPhase.Failed] = []
        };

    public static void Create(string path, UpdateTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(transaction);
        transaction.Validate();

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Update transaction path has no parent directory.");
        Directory.CreateDirectory(directory);
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transaction, JsonOptions));
        using var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(json);
        stream.Flush(flushToDisk: true);
    }

    public static UpdateTransaction ReadTransaction(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            UpdateTransaction transaction = JsonSerializer.Deserialize<UpdateTransaction>(
                    File.ReadAllText(Path.GetFullPath(path)),
                    JsonOptions)
                ?? throw new InvalidDataException("Update transaction was empty.");
            transaction.Validate();
            return transaction;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update transaction JSON is malformed.", exception);
        }
    }

    public static UpdateTransactionSnapshot ReadState(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            string json;
            using (var stream = new FileStream(
                       Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                json = reader.ReadToEnd();
            }
            UpdateTransactionSnapshot snapshot = JsonSerializer.Deserialize<UpdateTransactionSnapshot>(
                    json,
                    JsonOptions)
                ?? throw new InvalidDataException("Update transaction state was empty.");
            ValidateSnapshot(snapshot);
            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update transaction state JSON is malformed.", exception);
        }
    }

    public static UpdateTransactionSnapshot Advance(
        string statePath,
        string nonce,
        UpdateTransactionPhase next,
        int processId,
        string? detail = null,
        string? rollbackSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (processId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        string fullPath = Path.GetFullPath(statePath);
        UpdateTransactionPhase current = UpdateTransactionPhase.Created;
        string? verifiedRollback = null;
        if (File.Exists(fullPath))
        {
            UpdateTransactionSnapshot existing = ReadState(fullPath);
            if (!existing.Nonce.Equals(nonce, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Update transaction state nonce does not match.");
            }
            current = existing.Phase;
            verifiedRollback = existing.RollbackSha256;
        }
        if (!string.IsNullOrWhiteSpace(rollbackSha256))
        {
            if (current != UpdateTransactionPhase.Created ||
                next != UpdateTransactionPhase.RollbackVerified ||
                rollbackSha256.Length != 64 || rollbackSha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("Verified rollback SHA256 is invalid for this transition.");
            }
            verifiedRollback = rollbackSha256.ToUpperInvariant();
        }
        if (!Transitions[current].Contains(next))
        {
            throw new InvalidOperationException($"Update transaction cannot advance from {current} to {next}.");
        }

        var snapshot = new UpdateTransactionSnapshot(
            StateSchemaVersion,
            nonce,
            next,
            processId,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            verifiedRollback);
        WriteAtomically(fullPath, snapshot);
        return snapshot;
    }

    private static void ValidateSnapshot(UpdateTransactionSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != StateSchemaVersion ||
            string.IsNullOrWhiteSpace(snapshot.Nonce) ||
            snapshot.ProcessId < 0 ||
            !Enum.IsDefined(snapshot.Phase) ||
            (snapshot.RollbackSha256 is not null &&
             (snapshot.RollbackSha256.Length != 64 || snapshot.RollbackSha256.Any(character => !Uri.IsHexDigit(character)))))
        {
            throw new InvalidDataException("Update transaction state is invalid.");
        }
    }

    private static void WriteAtomically(string path, UpdateTransactionSnapshot snapshot)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("Update transaction state path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions));
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
