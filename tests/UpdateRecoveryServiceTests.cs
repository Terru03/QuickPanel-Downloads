using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using QuickPanel.Services;
using QuickPanel.Updater.Core;

internal static class UpdateRecoveryServiceTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows() || IsNestedTestProcess())
        {
            return;
        }
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "QuickPanel.Tests", "startup-recovery-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IncompleteTransactionStartsStableRecovery(root);
            MalformedTransactionNeedsAttentionWithoutMutation(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void IncompleteTransactionStartsStableRecovery(string root)
    {
        RecoveryFixture fixture = CreateFixture(root, "incomplete");
        ProcessStartInfo? started = null;
        UpdateRecoveryLaunchResult result = UpdateRecoveryService.TryStartRecovery(
            fixture.ApplicationExecutable,
            771,
            fixture.TransactionsRoot,
            info =>
            {
                started = info;
                return Process.GetCurrentProcess();
            });
        Need(result == UpdateRecoveryLaunchResult.RecoveryStarted,
            "An incomplete transaction did not start recovery before normal startup.");
        Need(started is not null && started.FileName == fixture.UpdaterExecutable &&
             started.ArgumentList.SequenceEqual(new[]
             {
                 "--recover", fixture.TransactionPath, fixture.StatePath, "771"
             }),
            "Startup recovery did not launch the staged updater with structured exact arguments.");
        Need(File.ReadAllText(Path.Combine(fixture.Profile, "settings.json")) == "profile-sentinel",
            "Startup recovery inspection changed profile data.");
    }

    private static void MalformedTransactionNeedsAttentionWithoutMutation(string root)
    {
        RecoveryFixture fixture = CreateFixture(root, "malformed");
        File.WriteAllText(fixture.TransactionPath, "{");
        string installHash = HashDirectory(fixture.Install);
        string profileHash = HashDirectory(fixture.Profile);
        bool started = false;
        UpdateRecoveryLaunchResult result = UpdateRecoveryService.TryStartRecovery(
            fixture.ApplicationExecutable,
            772,
            fixture.TransactionsRoot,
            _ =>
            {
                started = true;
                return null;
            });
        Need(result == UpdateRecoveryLaunchResult.NeedsAttention && !started,
            "Malformed recovery evidence launched an updater process.");
        Need(HashDirectory(fixture.Install) == installHash && HashDirectory(fixture.Profile) == profileHash,
            "Malformed recovery evidence mutated the install or profile.");
    }

    private static RecoveryFixture CreateFixture(string root, string name)
    {
        string fixture = Path.Combine(root, name);
        string install = Path.Combine(fixture, "install");
        string payload = Path.Combine(fixture, "payload");
        string profile = Path.Combine(fixture, "profile", "data");
        string backup = Path.Combine(profile + "-maintenance", "update-backups", name);
        string transactions = Path.Combine(fixture, "transactions");
        string attempt = Path.Combine(transactions, "attempt-" + name);
        string runtime = Path.Combine(attempt, "runtime");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "profile-sentinel");
        WriteApplication(install, "old");
        WriteApplication(payload, "new");
        ReleasePayloadPolicy.CopyApplicationFiles(install, backup, profile);
        string updaterExe = Path.Combine(runtime, "QuickPanel.Updater.exe");
        File.Copy(Environment.ProcessPath!, updaterExe);
        string version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion ?? "2.4.10";
        string transactionPath = Path.Combine(attempt, "transaction.json");
        string statePath = Path.Combine(attempt, "state.json");
        string manifest = Path.Combine(payload, ReleasePayloadPolicy.ApplicationManifestFileName);
        var transaction = new UpdateTransaction(
            1, name, Guid.NewGuid().ToString("N"), "update", 0, payload, install, profile,
            Path.Combine(install, "AIQuickPanel.exe"), backup,
            Path.Combine(profile + "-maintenance", "update-logs", name + ".log"),
            version, version, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))), true);
        UpdateTransactionStore.Create(transactionPath, transaction);
        UpdateTransactionStore.Advance(
            statePath, transaction.Nonce, UpdateTransactionPhase.RollbackVerified, 10,
            rollbackSha256: UpdateBackupFingerprint.Compute(backup));
        return new RecoveryFixture(
            transactions, install, profile, Path.Combine(install, "AIQuickPanel.exe"), updaterExe,
            transactionPath, statePath);
    }

    private static void WriteApplication(string directory, string marker)
    {
        File.Copy(Environment.ProcessPath!, Path.Combine(directory, "AIQuickPanel.exe"));
        File.WriteAllText(Path.Combine(directory, "marker.txt"), marker);
        File.WriteAllText(Path.Combine(directory, ReleasePayloadPolicy.ApplicationManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entries = new[] { "AIQuickPanel.exe", "marker.txt", ReleasePayloadPolicy.ApplicationManifestFileName }
            }));
    }

    private static string HashDirectory(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsNestedTestProcess() =>
        Environment.GetEnvironmentVariable("QUICKPANEL_TEST_CHILD") == "1" ||
        Environment.GetEnvironmentVariable("QUICKPANEL_TEST_HANDOFF_WORKER") == "1" ||
        Environment.GetEnvironmentVariable("QUICKPANEL_TEST_APPLY_WORKER") == "1";

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record RecoveryFixture(
        string TransactionsRoot,
        string Install,
        string Profile,
        string ApplicationExecutable,
        string UpdaterExecutable,
        string TransactionPath,
        string StatePath);
}
