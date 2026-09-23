using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QuickPanel.Services;

public sealed record StartupDiagnostics(
	string InstallPath,
	string RecommendedInstallDirectory,
	string SettingsDirectory,
	string StartupMethod,
	string StartupShortcutPath,
	string StartMenuShortcutPath,
	bool IsRecommendedInstallPath,
	bool IsStartupEnabled,
	bool LegacyRunKeyPresent,
	bool LegacyCommandLauncherPresent,
	string LastAttemptResult);

public sealed class StartupService
{
	private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string ValueName = "AIQuickPanel";

	private const string StartupShortcutFileName = "Quick Panel.lnk";

	private const string LegacyBrandedStartupShortcutFileName = "AI Quick Panel.lnk";

	private const string LegacyStartupLauncherFileName = "AIQuickPanel.cmd";

	private const string LegacyStartupShortcutFileName = "AIQuickPanel.lnk";

	private string _lastAttemptResult = "Not attempted.";

	public string LastAttemptResult => _lastAttemptResult;

	public static string RecommendedInstallDirectory => GetRunningInstallDirectory();

	public static string SettingsDirectory => PortableDataPaths.DataDirectory;

	public static string StartupShortcutPath => Path.Combine(
		GetSpecialFolder(Environment.SpecialFolder.Startup),
		StartupShortcutFileName);

	public static string StartMenuShortcutPath => Path.Combine(
		GetSpecialFolder(Environment.SpecialFolder.StartMenu),
		"Programs",
		"Quick Panel.lnk");

	internal static IReadOnlyList<string> GetUpdateInstallCandidateExecutables()
	{
		var candidates = new List<string>();
		string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (!string.IsNullOrWhiteSpace(localApplicationData))
		{
			candidates.Add(Path.Combine(localApplicationData, "Programs", "QuickPanel", "QuickPanel.exe"));
			candidates.Add(Path.Combine(localApplicationData, "Programs", "AIQuickPanel", "AIQuickPanel.exe"));
		}

		IEnumerable<string> shortcutPaths =
		[
			StartMenuShortcutPath,
			GetLegacyStartMenuShortcutPath(),
			StartupShortcutPath,
			.. GetLegacyStartupShortcutPaths()
		];
		foreach (string shortcutPath in shortcutPaths)
		{
			if (TryReadShortcut(shortcutPath, out string? targetPath, out _) &&
				!string.IsNullOrWhiteSpace(targetPath))
			{
				candidates.Add(targetPath);
			}
		}

		string legacyCommandPath = GetLegacyStartupCommandPath();
		try
		{
			if (File.Exists(legacyCommandPath))
			{
				string? commandTarget = TryExtractExecutablePath(File.ReadAllText(legacyCommandPath));
				if (!string.IsNullOrWhiteSpace(commandTarget))
				{
					candidates.Add(commandTarget);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
		{
			// A blocked legacy startup command is not a trusted install candidate.
		}

		try
		{
			using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
			string? runTarget = TryExtractExecutablePath(registryKey?.GetValue(ValueName) as string);
			if (!string.IsNullOrWhiteSpace(runTarget))
			{
				candidates.Add(runTarget);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
		{
			// A blocked legacy Run entry is not a trusted install candidate.
		}

		return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public bool SetEnabled(bool enabled)
	{
		if (!enabled)
		{
			return DisableAllStartupEntries();
		}
		return EnableStartupShortcut();
	}

	public bool DisableAllStartupEntries()
	{
		TryDeleteFile(StartupShortcutPath);
		foreach (string path in GetLegacyStartupShortcutPaths()) TryDeleteFile(path);
		TryDeleteFile(GetLegacyStartupCommandPath());
		TryDeleteRunKey();
		bool disabled = !HasStartupShortcutRegistration() &&
			GetLegacyStartupShortcutPaths().All(path => !File.Exists(path)) &&
			!File.Exists(GetLegacyStartupCommandPath()) &&
			!HasLegacyRunKeyRegistration();
		_lastAttemptResult = disabled
			? "Startup disabled. Removed safe shortcut and attempted cleanup of legacy Run-key/CMD entries."
			: "Startup disable failed. Windows or endpoint security blocked one or more startup entries.";
		return disabled;
	}

	public bool IsEnabled()
	{
		return HasStartupShortcutRegistration();
	}

	public bool EnsureStartMenuShortcut()
	{
		string? processPath = Environment.ProcessPath;
		if (!IsSafeInstalledExecutable(processPath))
		{
			_lastAttemptResult = "Start Menu shortcut skipped because Quick Panel is not running from a safe application install folder.";
			return false;
		}
		bool created = TryWriteShortcut(StartMenuShortcutPath, processPath!, arguments: string.Empty, description: "Quick Panel");
		if (created) TryDeleteFile(GetLegacyStartMenuShortcutPath());
		_lastAttemptResult = created
			? "Start Menu shortcut is present."
			: "Start Menu shortcut could not be created.";
		return created;
	}

	public StartupDiagnostics GetDiagnostics()
	{
		string installPath = Environment.ProcessPath ?? string.Empty;
		bool legacyRunKeyPresent = HasLegacyRunKeyRegistration();
		bool legacyCommandPresent = File.Exists(GetLegacyStartupCommandPath());
		bool startupEnabled = HasStartupShortcutRegistration();
		string startupMethod = startupEnabled ? "Startup folder shortcut (.lnk)" : "Disabled";
		if (!startupEnabled && legacyRunKeyPresent)
		{
			startupMethod = "Disabled; legacy HKCU Run entry is still present";
		}
		if (!startupEnabled && legacyCommandPresent)
		{
			startupMethod = "Disabled; legacy Startup CMD launcher is still present";
		}
		return new StartupDiagnostics(
			installPath,
			RecommendedInstallDirectory,
			SettingsDirectory,
			startupMethod,
			StartupShortcutPath,
			StartMenuShortcutPath,
			IsRecommendedInstallPath(installPath),
			startupEnabled,
			legacyRunKeyPresent,
			legacyCommandPresent,
			_lastAttemptResult);
	}

	public static string BuildStartupArguments()
	{
		return "--startup";
	}

	public static string BuildStartupCommand(string executablePath)
	{
		return "\"" + executablePath + "\" " + BuildStartupArguments();
	}

	public static string? TryExtractExecutablePath(string? startupCommand)
	{
		if (string.IsNullOrWhiteSpace(startupCommand))
		{
			return null;
		}
		string command = Environment.ExpandEnvironmentVariables(startupCommand).Trim();
		foreach (Match match in Regex.Matches(command, "\"([^\"]*)\""))
		{
			string? candidate = NormalizeExecutablePath(match.Groups[1].Value);
			if (IsQuickPanelExecutableName(candidate))
			{
				return candidate;
			}
		}
		string[] parts = command.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string part in parts)
		{
			string? candidate = NormalizeExecutablePath(part.Trim('"'));
			if (IsQuickPanelExecutableName(candidate))
			{
				return candidate;
			}
		}
		return null;
	}

	private bool EnableStartupShortcut()
	{
		string? processPath = Environment.ProcessPath;
		if (!IsSafeInstalledExecutable(processPath))
		{
			_lastAttemptResult = "Startup shortcut was not created because Quick Panel is not running from " + RecommendedInstallDirectory + ".";
			return false;
		}
		TryDeleteRunKey();
		TryDeleteFile(GetLegacyStartupCommandPath());
		foreach (string path in GetLegacyStartupShortcutPaths()) TryDeleteFile(path);
		bool created = TryWriteShortcut(StartupShortcutPath, processPath!, BuildStartupArguments(), "Start Quick Panel after Windows sign-in");
		_lastAttemptResult = created
			? "Startup enabled with a per-user Startup folder shortcut."
			: "Startup shortcut could not be created. Windows or endpoint security blocked the Startup folder.";
		return created && HasStartupShortcutRegistration();
	}

	private static bool HasStartupShortcutRegistration()
	{
		if (!TryReadShortcut(StartupShortcutPath, out string? targetPath, out string? arguments))
		{
			return false;
		}
		return IsSafeInstalledExecutable(targetPath) &&
			(arguments ?? string.Empty).Contains("--startup", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryWriteShortcut(string shortcutPath, string targetPath, string arguments, string description)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath) ?? throw new InvalidOperationException("Shortcut path has no directory."));
			object shortcut = CreateShortcut(shortcutPath);
			SetShortcutProperty(shortcut, "TargetPath", targetPath);
			SetShortcutProperty(shortcut, "Arguments", arguments);
			SetShortcutProperty(shortcut, "WorkingDirectory", Path.GetDirectoryName(targetPath) ?? string.Empty);
			SetShortcutProperty(shortcut, "Description", description);
			SetShortcutProperty(shortcut, "IconLocation", targetPath + ",0");
			shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
			return true;
		}
		catch (Exception ex) when (ex is IOException ||
			ex is UnauthorizedAccessException ||
			ex is SecurityException ||
			ex is InvalidOperationException ||
			ex is TargetInvocationException ||
			ex is COMException)
		{
			return false;
		}
	}

	private static bool TryReadShortcut(string shortcutPath, out string? targetPath, out string? arguments)
	{
		targetPath = null;
		arguments = null;
		if (!File.Exists(shortcutPath))
		{
			return false;
		}
		try
		{
			object shortcut = CreateShortcut(shortcutPath);
			targetPath = GetShortcutProperty(shortcut, "TargetPath") as string;
			arguments = GetShortcutProperty(shortcut, "Arguments") as string;
			return !string.IsNullOrWhiteSpace(targetPath);
		}
		catch (Exception ex) when (ex is IOException ||
			ex is UnauthorizedAccessException ||
			ex is SecurityException ||
			ex is InvalidOperationException ||
			ex is TargetInvocationException ||
			ex is COMException)
		{
			return false;
		}
	}

	private static object CreateShortcut(string shortcutPath)
	{
		Type shellType = Type.GetTypeFromProgID("WScript.Shell") ??
			throw new InvalidOperationException("Windows Script Host shortcut service is not available.");
		object shell = Activator.CreateInstance(shellType) ??
			throw new InvalidOperationException("Could not create Windows shortcut service.");
		return shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath }) ??
			throw new InvalidOperationException("Could not create shortcut object.");
	}

	private static void SetShortcutProperty(object shortcut, string propertyName, object value)
	{
		shortcut.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, shortcut, new[] { value });
	}

	private static object? GetShortcutProperty(object shortcut, string propertyName)
	{
		return shortcut.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, shortcut, null);
	}

	private static bool TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch (SecurityException)
		{
			return false;
		}
	}

	private static bool TryDeleteRunKey()
	{
		try
		{
			using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
			if (registryKey == null)
			{
				return true;
			}
			registryKey.DeleteValue(ValueName, throwOnMissingValue: false);
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch (SecurityException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static bool HasLegacyRunKeyRegistration()
	{
		try
		{
			using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
			return !string.IsNullOrWhiteSpace(registryKey?.GetValue(ValueName) as string);
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch (SecurityException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static string GetLegacyStartupCommandPath()
	{
		return Path.Combine(GetSpecialFolder(Environment.SpecialFolder.Startup), LegacyStartupLauncherFileName);
	}

	private static IEnumerable<string> GetLegacyStartupShortcutPaths()
	{
		string startup = GetSpecialFolder(Environment.SpecialFolder.Startup);
		yield return Path.Combine(startup, LegacyBrandedStartupShortcutFileName);
		yield return Path.Combine(startup, LegacyStartupShortcutFileName);
	}

	private static string GetLegacyStartMenuShortcutPath()
	{
		return Path.Combine(GetSpecialFolder(Environment.SpecialFolder.StartMenu), "Programs", LegacyBrandedStartupShortcutFileName);
	}

	private static string GetSpecialFolder(Environment.SpecialFolder folder)
	{
		string path = Environment.GetFolderPath(folder);
		if (!string.IsNullOrWhiteSpace(path))
		{
			return path;
		}
		string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return folder == Environment.SpecialFolder.Startup
			? Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
			: Path.Combine(appData, "Microsoft", "Windows", "Start Menu");
	}

	private static string? NormalizeExecutablePath(string executablePath)
	{
		try
		{
			return Path.GetFullPath(executablePath);
		}
		catch (ArgumentException)
		{
			return null;
		}
		catch (NotSupportedException)
		{
			return null;
		}
	}

	private static bool IsSafeInstalledExecutable(string? executablePath)
	{
		return IsEligibleShortcutTarget(executablePath) &&
			IsRecommendedInstallPath(Path.GetDirectoryName(Path.GetFullPath(executablePath!)));
	}

	internal static bool IsEligibleShortcutTarget(string? executablePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(executablePath) ||
				!IsQuickPanelExecutableName(executablePath) ||
				!File.Exists(executablePath))
			{
				return false;
			}
			string installDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? string.Empty;
			PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(installDirectory);
			ReleasePayloadPolicy.EnsureApplicationInstallIdentity(installDirectory, requireManifest: false);
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
			IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
		{
			return false;
		}
	}

	private static bool IsRecommendedInstallPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			string fullPath = Path.GetFullPath(path);
			if (File.Exists(fullPath))
			{
				fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
			}
			PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(fullPath);
			ReleasePayloadPolicy.EnsureApplicationInstallIdentity(fullPath, requireManifest: false);
			return fullPath.Equals(RecommendedInstallDirectory, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
			IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
		{
			return false;
		}
	}

	private static bool IsQuickPanelExecutableName(string? executablePath)
	{
		return !string.IsNullOrWhiteSpace(executablePath) &&
			PortableUpdateInstaller.IsSupportedApplicationExecutableName(Path.GetFileName(executablePath));
	}

	private static string GetRunningInstallDirectory()
	{
		string? processDirectory = string.IsNullOrWhiteSpace(Environment.ProcessPath)
			? null
			: Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath));
		return Path.TrimEndingDirectorySeparator(processDirectory ?? AppContext.BaseDirectory);
	}
}
