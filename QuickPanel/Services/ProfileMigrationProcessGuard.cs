using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace QuickPanel.Services;

public static class ProfileMigrationProcessGuard
{
    public static ProfileAccessCheck Check(IReadOnlyCollection<string> profileDirectories)
    {
        ArgumentNullException.ThrowIfNull(profileDirectories);

        try
        {
            int currentProcessId = Environment.ProcessId;
            foreach (string name in new[] { "AIQuickPanel", "QuickPanel" }.Where(IsQuickPanelProcessName))
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        if (process.Id != currentProcessId)
                        {
                            return new ProfileAccessCheck(
                                false,
                                "Another Quick Panel instance is running. Close it before profile recovery.");
                        }
                    }
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                return ProfileAccessCheck.Safe;
            }

            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");
            using ManagementObjectCollection processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                using (process)
                {
                    string? commandLine = process["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(commandLine))
                    {
                        return new ProfileAccessCheck(
                            false,
                            "A WebView2 process could not be attributed safely. Close Quick Panel and try again.");
                    }
                    if (ReferencesAnyProfile(commandLine, profileDirectories))
                    {
                        return new ProfileAccessCheck(
                            false,
                            "A WebView2 process is still using a profile selected for recovery. Close Quick Panel and try again.");
                    }
                }
            }

            return ProfileAccessCheck.Safe;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ProfileAccessCheck(
                false,
                $"Profile process usage could not be verified safely ({exception.GetType().Name}).");
        }
    }

    internal static bool IsQuickPanelProcessName(string? name) =>
        name is not null && (name.Equals("QuickPanel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AIQuickPanel", StringComparison.OrdinalIgnoreCase));

    public static bool ReferencesAnyProfile(string? commandLine, IReadOnlyCollection<string> profileDirectories)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        string normalizedCommandLine = commandLine.Replace('\\', '/');
        return profileDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Select(path => path.Replace('\\', '/'))
            .Any(path => normalizedCommandLine.Contains(path, StringComparison.OrdinalIgnoreCase));
    }
}
