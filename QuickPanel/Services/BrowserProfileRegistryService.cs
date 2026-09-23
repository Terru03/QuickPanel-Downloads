using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed class BrowserProfileRegistryService
{
	public const string DefaultProfileId = "default";
	public const string DefaultProfileName = "Default";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	private readonly object _sync = new();
	private readonly string _registryPath;
	private readonly string _webView2Directory;

	public BrowserProfileRegistryService()
		: this(
			Path.Combine(PortableDataPaths.DataDirectory, "profiles.json"),
			PortableDataPaths.WebView2Directory)
	{
	}

	public BrowserProfileRegistryService(string registryPath, string webView2Directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(webView2Directory);
		_registryPath = Path.GetFullPath(registryPath);
		_webView2Directory = Path.GetFullPath(webView2Directory);
	}

	public string RegistryPath => _registryPath;

	public IReadOnlyList<BrowserProfileDefinition> Synchronize(IEnumerable<AiTab> tabs)
	{
		ArgumentNullException.ThrowIfNull(tabs);
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			bool changed = NormalizeDocument(document);
			Dictionary<string, BrowserProfileDefinition> profiles = document.Profiles
				.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);

			foreach (AiTab tab in tabs)
			{
				string? browserProfileId = BrowserProfilePolicy.NormalizeProfileId(tab.BrowserProfileId);
				if (browserProfileId is null || profiles.ContainsKey(browserProfileId))
				{
					continue;
				}

				string name = NormalizeName(tab.BrowserProfileLabel, BuildRecoveredProfileName(browserProfileId));
				BrowserProfileDefinition discovered = new()
				{
					Id = browserProfileId,
					Name = name,
					IsDefault = false
				};
				document.Profiles.Add(discovered);
				profiles[browserProfileId] = discovered;
				changed = true;
			}

			foreach (string onDiskProfileId in DiscoverManagedProfileIdsOnDisk())
			{
				if (profiles.ContainsKey(onDiskProfileId) ||
					document.PendingDeletionProfileIds.Contains(onDiskProfileId, StringComparer.OrdinalIgnoreCase))
				{
					continue;
				}

				BrowserProfileDefinition recovered = new()
				{
					Id = onDiskProfileId,
					Name = BuildRecoveredProfileName(onDiskProfileId),
					IsDefault = false
				};
				document.Profiles.Add(recovered);
				profiles[onDiskProfileId] = recovered;
				changed = true;
			}

			if (changed)
			{
				SaveDocument(document);
			}
			return CloneProfiles(document.Profiles);
		}
	}

	public IReadOnlyList<BrowserProfileDefinition> GetProfiles()
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			if (NormalizeDocument(document))
			{
				SaveDocument(document);
			}
			return CloneProfiles(document.Profiles);
		}
	}

	public BrowserProfileDefinition GetDefaultProfile()
	{
		return GetProfiles().First(profile => profile.IsDefault);
	}

	public BrowserProfileDefinition Create(string name)
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			NormalizeDocument(document);
			string normalizedName = NormalizeName(name, "New profile");
			string id;
			do
			{
				id = "qp-" + Guid.NewGuid().ToString("N");
			}
			while (document.Profiles.Any(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

			BrowserProfileDefinition profile = new()
			{
				Id = id,
				Name = normalizedName,
				IsDefault = false
			};
			document.Profiles.Add(profile);
			SaveDocument(document);
			return Clone(profile);
		}
	}

	public BrowserProfileDefinition Rename(string profileId, string name)
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			NormalizeDocument(document);
			BrowserProfileDefinition existing = FindRequired(document, profileId);
			BrowserProfileDefinition updated = new()
			{
				Id = existing.Id,
				Name = NormalizeName(name, existing.Name),
				IsDefault = existing.IsDefault
			};
			Replace(document, existing, updated);
			SaveDocument(document);
			return Clone(updated);
		}
	}

	public BrowserProfileDefinition SetDefault(string profileId)
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			NormalizeDocument(document);
			BrowserProfileDefinition selected = FindRequired(document, profileId);
			for (int i = 0; i < document.Profiles.Count; i++)
			{
				BrowserProfileDefinition profile = document.Profiles[i];
				document.Profiles[i] = new BrowserProfileDefinition
				{
					Id = profile.Id,
					Name = profile.Name,
					IsDefault = profile.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase)
				};
			}
			SaveDocument(document);
			return Clone(document.Profiles.First(profile => profile.IsDefault));
		}
	}

	public void Delete(string profileId)
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			NormalizeDocument(document);
			BrowserProfileDefinition profile = FindRequired(document, profileId);
			if (profile.IsDefault)
			{
				throw new InvalidOperationException("Make another profile the default before deleting this profile.");
			}
			if (profile.Id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("The original shared WebView2 profile is retained for compatibility and cannot be deleted.");
			}

			document.Profiles.RemoveAll(item => item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
			if (!TryDeleteProfileDataCore(profile.Id, out _))
			{
				if (!document.PendingDeletionProfileIds.Contains(profile.Id, StringComparer.OrdinalIgnoreCase))
				{
					document.PendingDeletionProfileIds.Add(profile.Id);
				}
			}
			SaveDocument(document);
		}
	}

	public void MarkPendingDeletion(string profileId)
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			NormalizeDocument(document);
			string? normalized = BrowserProfilePolicy.NormalizeProfileId(profileId);
			if (normalized is null)
			{
				throw new ArgumentException("Only managed separate profiles can be marked for deletion.", nameof(profileId));
			}
			document.Profiles.RemoveAll(item => item.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
			if (!document.PendingDeletionProfileIds.Contains(normalized, StringComparer.OrdinalIgnoreCase))
			{
				document.PendingDeletionProfileIds.Add(normalized);
			}
			SaveDocument(document);
		}
	}

	public void ProcessPendingDeletions()
	{
		lock (_sync)
		{
			BrowserProfileRegistryDocument document = LoadDocument();
			bool changed = NormalizeDocument(document);
			foreach (string id in document.PendingDeletionProfileIds.ToArray())
			{
				if (TryDeleteProfileDataCore(id, out _))
				{
					document.PendingDeletionProfileIds.RemoveAll(item => item.Equals(id, StringComparison.OrdinalIgnoreCase));
					changed = true;
				}
			}
			if (changed)
			{
				SaveDocument(document);
			}
		}
	}

	public string GetProfileDataPath(string profileId)
	{
		string normalizedRegistryId = NormalizeRegistryId(profileId) ??
			throw new ArgumentException("Invalid profile ID.", nameof(profileId));
		string folderName = normalizedRegistryId.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase)
			? "Default"
			: normalizedRegistryId;
		return Path.Combine(_webView2Directory, "EBWebView", folderName);
	}

	public static string GetRegistryId(string? browserProfileId)
	{
		return BrowserProfilePolicy.NormalizeProfileId(browserProfileId) ?? DefaultProfileId;
	}

	public static string? GetBrowserProfileId(string registryProfileId)
	{
		string? normalized = NormalizeRegistryId(registryProfileId);
		if (normalized is null)
		{
			throw new ArgumentException("Invalid profile ID.", nameof(registryProfileId));
		}
		return normalized.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase) ? null : normalized;
	}

	private BrowserProfileRegistryDocument LoadDocument()
	{
		try
		{
			if (!File.Exists(_registryPath))
			{
				return NewDocument();
			}
			return JsonSerializer.Deserialize<BrowserProfileRegistryDocument>(File.ReadAllText(_registryPath), JsonOptions) ?? NewDocument();
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
		{
			return NewDocument();
		}
	}

	private void SaveDocument(BrowserProfileRegistryDocument document)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_registryPath) ?? throw new InvalidOperationException("Could not resolve profile registry directory."));
		string temp = _registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
			if (File.Exists(_registryPath))
			{
				try
				{
					File.Replace(temp, _registryPath, null);
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{
					File.Copy(temp, _registryPath, overwrite: true);
				}
			}
			else
			{
				File.Move(temp, _registryPath);
			}
		}
		finally
		{
			if (File.Exists(temp))
			{
				try { File.Delete(temp); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
			}
		}
	}

	private IEnumerable<string> DiscoverManagedProfileIdsOnDisk()
	{
		string profileRoot = Path.Combine(_webView2Directory, "EBWebView");
		if (!Directory.Exists(profileRoot))
		{
			return Array.Empty<string>();
		}
		try
		{
			return Directory.EnumerateDirectories(profileRoot, "qp-*", SearchOption.TopDirectoryOnly)
				.Select(Path.GetFileName)
				.Where(name => BrowserProfilePolicy.NormalizeProfileId(name) is not null)
				.Select(name => BrowserProfilePolicy.NormalizeProfileId(name)!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			return Array.Empty<string>();
		}
	}

	private bool TryDeleteProfileDataCore(string profileId, out string? error)
	{
		error = null;
		string? normalized = BrowserProfilePolicy.NormalizeProfileId(profileId);
		if (normalized is null)
		{
			error = "Only managed separate profiles can be deleted.";
			return false;
		}
		string path = GetProfileDataPath(normalized);
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			error = ex.Message;
			return false;
		}
	}

	private static bool NormalizeDocument(BrowserProfileRegistryDocument document)
	{
		bool changed = false;
		document.Profiles ??= new List<BrowserProfileDefinition>();
		document.PendingDeletionProfileIds ??= new List<string>();

		List<BrowserProfileDefinition> normalized = new();
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		foreach (BrowserProfileDefinition profile in document.Profiles)
		{
			string? id = NormalizeRegistryId(profile.Id);
			if (id is null || !used.Add(id))
			{
				changed = true;
				continue;
			}
			string name = NormalizeName(profile.Name, id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase) ? DefaultProfileName : "Profile");
			BrowserProfileDefinition item = new()
			{
				Id = id,
				Name = name,
				IsDefault = profile.IsDefault
			};
			normalized.Add(item);
			changed |= !string.Equals(profile.Id, item.Id, StringComparison.Ordinal) ||
				!string.Equals(profile.Name, item.Name, StringComparison.Ordinal);
		}

		if (!used.Contains(DefaultProfileId))
		{
			normalized.Insert(0, new BrowserProfileDefinition
			{
				Id = DefaultProfileId,
				Name = DefaultProfileName,
				IsDefault = normalized.All(profile => !profile.IsDefault)
			});
			used.Add(DefaultProfileId);
			changed = true;
		}

		int defaultCount = normalized.Count(profile => profile.IsDefault);
		if (defaultCount != 1)
		{
			string selectedDefault = normalized.FirstOrDefault(profile => profile.IsDefault)?.Id ?? DefaultProfileId;
			for (int i = 0; i < normalized.Count; i++)
			{
				BrowserProfileDefinition profile = normalized[i];
				normalized[i] = new BrowserProfileDefinition
				{
					Id = profile.Id,
					Name = profile.Name,
					IsDefault = profile.Id.Equals(selectedDefault, StringComparison.OrdinalIgnoreCase)
				};
			}
			changed = true;
		}

		List<string> pending = document.PendingDeletionProfileIds
			.Select(BrowserProfilePolicy.NormalizeProfileId)
			.Where(id => id is not null)
			.Select(id => id!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (pending.Count != document.PendingDeletionProfileIds.Count ||
			!pending.SequenceEqual(document.PendingDeletionProfileIds, StringComparer.Ordinal))
		{
			changed = true;
		}

		document.Profiles = normalized;
		document.PendingDeletionProfileIds = pending;
		return changed;
	}

	private static BrowserProfileDefinition FindRequired(BrowserProfileRegistryDocument document, string profileId)
	{
		string? normalized = NormalizeRegistryId(profileId);
		BrowserProfileDefinition? profile = normalized is null
			? null
			: document.Profiles.FirstOrDefault(item => item.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
		return profile ?? throw new InvalidOperationException("The selected browsing profile no longer exists.");
	}

	private static void Replace(BrowserProfileRegistryDocument document, BrowserProfileDefinition existing, BrowserProfileDefinition updated)
	{
		int index = document.Profiles.FindIndex(profile => profile.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			throw new InvalidOperationException("The selected browsing profile no longer exists.");
		}
		document.Profiles[index] = updated;
	}

	private static BrowserProfileRegistryDocument NewDocument()
	{
		return new BrowserProfileRegistryDocument
		{
			Profiles = new List<BrowserProfileDefinition>
			{
				new()
				{
					Id = DefaultProfileId,
					Name = DefaultProfileName,
					IsDefault = true
				}
			}
		};
	}

	private static string? NormalizeRegistryId(string? profileId)
	{
		string value = profileId?.Trim() ?? string.Empty;
		if (value.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase))
		{
			return DefaultProfileId;
		}
		return BrowserProfilePolicy.NormalizeProfileId(value);
	}

	private static string NormalizeName(string? name, string fallback)
	{
		string value = name?.Trim() ?? string.Empty;
		if (value.Length > 40)
		{
			value = value[..40].TrimEnd();
		}
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}

	private static string BuildRecoveredProfileName(string profileId)
	{
		string suffix = profileId.Length <= 8 ? profileId : profileId[^6..];
		return "Recovered profile " + suffix;
	}

	private static IReadOnlyList<BrowserProfileDefinition> CloneProfiles(IEnumerable<BrowserProfileDefinition> profiles)
	{
		return profiles.Select(Clone).ToArray();
	}

	private static BrowserProfileDefinition Clone(BrowserProfileDefinition profile)
	{
		return new BrowserProfileDefinition
		{
			Id = profile.Id,
			Name = profile.Name,
			IsDefault = profile.IsDefault
		};
	}
}
