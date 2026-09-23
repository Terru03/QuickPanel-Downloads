using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickPanel.Services;

public static class PortableDataPaths
{
	public static string ProductDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"QuickPanel");

	public static string LegacyProductDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"AIQuickPanel");

	public static string DataDirectory => Path.Combine(ProductDirectory, "data");

	public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

	public static string LogDirectory => Path.Combine(DataDirectory, "logs");

	public static string TabIconCacheDirectory => Path.Combine(DataDirectory, "IconCache");

	public static string WebsiteIconCacheDirectory => Path.Combine(TabIconCacheDirectory, "Websites");

	public static string WebView2Directory => Path.Combine(DataDirectory, "WebView2");

	public static string MaintenanceDirectory => GetValidatedMaintenanceDirectory(
		DataDirectory,
		AppContext.BaseDirectory);

	public static string MigrationBackupDirectory => Path.Combine(MaintenanceDirectory, "migration-backups");

	public static string MigrationLogDirectory => Path.Combine(MaintenanceDirectory, "migration-logs");

	public static string UpdateBackupDirectory => Path.Combine(MaintenanceDirectory, "update-backups");

	public static string GetMaintenanceDirectory(string physicalDataDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(physicalDataDirectory);
		return NormalizeDirectory(physicalDataDirectory) + "-maintenance";
	}

	internal static string GetValidatedMaintenanceDirectory(
		string canonicalDataDirectory,
		string installDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
		string physicalDataDirectory = ResolveDataDirectoryForMaintenance(canonicalDataDirectory);
		string maintenanceDirectory = GetMaintenanceDirectory(physicalDataDirectory);
		PortableUpdatePathSafety.EnsureMaintenancePathSafe(
			maintenanceDirectory,
			installDirectory,
			physicalDataDirectory);
		return maintenanceDirectory;
	}

	public static string GetUpdateBackupDirectory(string physicalDataDirectory)
	{
		return Path.Combine(GetMaintenanceDirectory(physicalDataDirectory), "update-backups");
	}

	public static string GetUpdateLogDirectory(string physicalDataDirectory)
	{
		return Path.Combine(GetMaintenanceDirectory(physicalDataDirectory), "update-logs");
	}

	public static string GetLegacyUpdateBackupDirectory(
		string canonicalDataDirectory,
		string physicalDataDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDataDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(physicalDataDirectory);
		string canonical = NormalizeDirectory(canonicalDataDirectory);
		string physical = NormalizeDirectory(physicalDataDirectory);
		string resolvedData = PhysicalDirectoryResolver.ResolveExistingDirectory(canonical);
		if (!PathsEqual(resolvedData, physical))
		{
			throw new InvalidOperationException(
				"The legacy backup root does not belong to the resolved persistent-data alias.");
		}

		string? canonicalProductDirectory = Path.GetDirectoryName(canonical);
		if (string.IsNullOrWhiteSpace(canonicalProductDirectory))
		{
			throw new InvalidOperationException(
				"The canonical persistent-data directory must have a product directory.");
		}
		string physicalProductDirectory = PhysicalDirectoryResolver.ResolveExistingDirectory(
			canonicalProductDirectory);
		ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(physicalProductDirectory);
		return Path.Combine(physicalProductDirectory, "update-backups");
	}

	public static IReadOnlyList<ProfileMigrationCandidate> GetLegacyProfileCandidates(string executableDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var candidates = new[]
		{
			new ProfileMigrationCandidate("legacy-canonical-data", Path.Combine(LegacyProductDirectory, "data"), IsProductRoot: false),
			new ProfileMigrationCandidate("legacy-root", LegacyProductDirectory, IsProductRoot: true),
			new ProfileMigrationCandidate("legacy-nested", Path.Combine(LegacyProductDirectory, "AIQuickPanel"), IsProductRoot: false),
			new ProfileMigrationCandidate(
				"legacy-install-data",
				Path.Combine(localAppData, "Programs", "AIQuickPanel", "data"),
				IsProductRoot: false),
			new ProfileMigrationCandidate(
				"legacy-executable-data",
				Path.Combine(Path.GetFullPath(executableDirectory), "data"),
				IsProductRoot: false)
		};

		return candidates
			.Where(candidate => !PathsEqual(candidate.Directory, DataDirectory))
			.GroupBy(candidate => Path.GetFullPath(candidate.Directory), StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
	}

	public static bool IsUnderPortableDataDirectory(string path)
	{
		string fullDataDirectory = Path.GetFullPath(DataDirectory)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(path);
		return fullPath.StartsWith(fullDataDirectory, StringComparison.OrdinalIgnoreCase);
	}

	private static bool PathsEqual(string left, string right)
	{
		return Path.GetFullPath(left)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			.Equals(
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
	}

	private static string ResolveDataDirectoryForMaintenance(string dataDirectory)
	{
		string full = NormalizeDirectory(dataDirectory);
		if (File.Exists(full))
		{
			throw new InvalidOperationException("The persistent user-data path must be a directory.");
		}
		if (Directory.Exists(full))
		{
			return PhysicalDirectoryResolver.ResolveExistingDirectory(full);
		}

		try
		{
			for (string? current = full;
				 !string.IsNullOrWhiteSpace(current);
				 current = Path.GetDirectoryName(current))
			{
				if (new DirectoryInfo(current).LinkTarget is not null)
				{
					throw new InvalidOperationException(
						"Cannot derive maintenance storage through an unresolved persistent-data alias.");
				}
			}
		}
		catch (InvalidOperationException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new InvalidOperationException(
				"Could not validate the persistent-data path before deriving maintenance storage.",
				exception);
		}

		ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(full);
		return full;
	}

	private static string NormalizeDirectory(string path)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}
}
