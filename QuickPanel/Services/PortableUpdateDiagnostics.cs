using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickPanel.Services;

internal sealed record PortableUpdateHandoff(
    int SchemaVersion,
    int ProcessId,
    string RequestPath,
    DateTimeOffset StartedAtUtc);

internal sealed class PortableUpdateDiagnostics
{
    internal const string StartupLogFileName = "update-startup.log";
    internal const string HandoffFileName = "worker-handoff.json";
    internal const string HandoffRequiredFileName = "worker-handoff.required";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private PortableUpdateDiagnostics(
        string requestPath,
        string temporaryLogPath,
        string durableLogPath,
        string startupLogPath,
        string handoffPath,
        string handoffRequiredPath)
    {
        RequestPath = requestPath;
        TemporaryLogPath = temporaryLogPath;
        DurableLogPath = durableLogPath;
        StartupLogPath = startupLogPath;
        HandoffPath = handoffPath;
        HandoffRequiredPath = handoffRequiredPath;
    }

    internal string RequestPath { get; }

    internal string TemporaryLogPath { get; }

    internal string DurableLogPath { get; }

    internal string StartupLogPath { get; }

    internal string HandoffPath { get; }

    internal string HandoffRequiredPath { get; }

    internal bool ParentRequiresHandoff => File.Exists(HandoffRequiredPath);

    internal static bool RequestRequiresHandoff(string requestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        string fullRequestPath = Path.GetFullPath(requestPath);
        string requestDirectory = Path.GetDirectoryName(fullRequestPath)
            ?? throw new InvalidOperationException("Portable update request has no parent directory.");
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(requestDirectory);
        return File.Exists(Path.Combine(requestDirectory, HandoffRequiredFileName));
    }

    internal static PortableUpdateDiagnostics Create(
        string requestPath,
        PortableUpdateRequest request)
    {
        return CreateCore(requestPath, request, legacyResolutionFailure: false);
    }

    internal static PortableUpdateDiagnostics CreateLegacyResolutionFailure(
        string requestPath,
        PortableUpdateRequest request)
    {
        return CreateCore(requestPath, request, legacyResolutionFailure: true);
    }

    private static PortableUpdateDiagnostics CreateCore(
        string requestPath,
        PortableUpdateRequest request,
        bool legacyResolutionFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        ArgumentNullException.ThrowIfNull(request);

        string fullRequestPath = Path.GetFullPath(requestPath);
        string requestDirectory = Path.GetDirectoryName(fullRequestPath)
            ?? throw new InvalidOperationException("Portable update request has no parent directory.");
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(requestDirectory);

        string install = Path.GetFullPath(request.InstallDirectory);
        string physicalData = Path.GetFullPath(request.CanonicalDataDirectory);
        string temporaryLog = Path.GetFullPath(request.LogPath);
        if (!Path.GetDirectoryName(temporaryLog)!.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).Equals(
                    requestDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Portable updater temporary logs must be direct children of the request directory.");
        }
        string durableLogDirectory = PortableDataPaths.GetUpdateLogDirectory(physicalData);
        string durableLog = Path.Combine(
            durableLogDirectory,
            "update-" + GetAttemptId(fullRequestPath) + ".log");
        if (legacyResolutionFailure)
        {
            PortableUpdatePathSafety.EnsureLegacyFailureLogDestinationSafe(
                durableLog,
                install,
                physicalData);
        }
        else
        {
            PortableUpdatePathSafety.EnsureUpdateLogDestinationSafe(
                durableLog,
                install,
                physicalData);
        }
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(temporaryLog);

        return new PortableUpdateDiagnostics(
            fullRequestPath,
            temporaryLog,
            durableLog,
            Path.Combine(requestDirectory, StartupLogFileName),
            Path.Combine(requestDirectory, HandoffFileName),
            Path.Combine(requestDirectory, HandoffRequiredFileName));
    }

    internal static string GetStartupLogPath(string requestPath)
    {
        string fullRequestPath = Path.GetFullPath(requestPath);
        return Path.Combine(
            Path.GetDirectoryName(fullRequestPath)
                ?? throw new InvalidOperationException("Portable update request has no parent directory."),
            StartupLogFileName);
    }

    internal static void WriteStartup(string requestPath, string message)
    {
        try
        {
            string path = GetStartupLogPath(requestPath);
            string directory = Path.GetDirectoryName(path)!;
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
            File.AppendAllText(path, FormatLine("startup", message));
        }
        catch
        {
            // This is the last diagnostic path available before request parsing.
        }
    }

    internal void PrepareParentHandoff()
    {
        string directory = Path.GetDirectoryName(HandoffRequiredPath)!;
        ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(directory);
        if (File.Exists(HandoffPath) || File.Exists(HandoffRequiredPath))
        {
            throw new InvalidOperationException("Portable updater handoff files already exist.");
        }
        File.WriteAllText(
            HandoffRequiredPath,
            "Quick Panel must remain open until the updater worker acknowledges this request." +
            Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal void SignalHandoff()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HandoffPath)!);
        var handoff = new PortableUpdateHandoff(
            1,
            Environment.ProcessId,
            RequestPath,
            DateTimeOffset.UtcNow);
        string temporary = HandoffPath + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(handoff, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, HandoffPath, overwrite: false);
        TryLog("handoff", "worker ownership acknowledged; worker-pid=" + Environment.ProcessId);
    }

    internal bool TryReadHandoff(int expectedProcessId, out PortableUpdateHandoff? handoff)
    {
        handoff = null;
        try
        {
            if (!File.Exists(HandoffPath))
            {
                return false;
            }
            handoff = JsonSerializer.Deserialize<PortableUpdateHandoff>(
                File.ReadAllText(HandoffPath),
                JsonOptions);
            return handoff is not null &&
                handoff.SchemaVersion == 1 &&
                handoff.ProcessId == expectedProcessId &&
                Path.GetFullPath(handoff.RequestPath).Equals(
                    RequestPath,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal void Log(string stage, string message)
    {
        string line = FormatLine(stage, message);
        Exception? firstFailure = null;
        foreach (string path in new[] { TemporaryLogPath, DurableLogPath, StartupLogPath }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Append(path, line);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= exception;
            }
        }
        if (firstFailure is not null)
        {
            throw new IOException(
                "One or more portable updater diagnostics could not be written.",
                firstFailure);
        }
    }

    internal void TryLog(string stage, string message)
    {
        try
        {
            Log(stage, message);
        }
        catch
        {
            // An update failure must still attempt rollback/restart if one log is unavailable.
        }
    }

    internal static string ReadVersion(string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath))
            {
                return "missing";
            }
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
            return info.ProductVersion ?? info.FileVersion ?? "unknown";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unavailable-" + exception.GetType().Name;
        }
    }

    private static void Append(string path, string line)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        File.AppendAllText(path, line);
    }

    private static string FormatLine(string stage, string message)
    {
        string safeStage = Sanitize(stage);
        string safeMessage = Sanitize(message);
        return $"[{DateTimeOffset.UtcNow:O}] stage={safeStage}; {safeMessage}{Environment.NewLine}";
    }

    private static string Sanitize(string value)
    {
        return (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static string GetAttemptId(string requestPath)
    {
        string? directoryName = Path.GetFileName(Path.GetDirectoryName(requestPath));
        if (!string.IsNullOrWhiteSpace(directoryName) &&
            Guid.TryParseExact(directoryName, "N", out Guid attempt))
        {
            return attempt.ToString("N");
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(requestPath)));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
