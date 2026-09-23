using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed record SettingsBackupSummary(
	int CustomTabCount,
	int ClosedTabCount,
	bool HasUpdateManifestUrl,
	bool HasStartupSetting,
	bool HasAlwaysOnTopSetting);

public sealed class SettingsService
{
	private const string NativeCodexTabId = "native:codex";

	private const string LegacyNativeQuickToolsTabId = "native:quick-tools";

	private const string NativeCodexUsageTabId = "native:codex-usage";

	private const string NativeWindowsHelperTabId = "native:windows-helper";

	private const string NativeTripPlannerTabId = "native:trip-planner";

	private const string NativeTaskManagerTabId = "native:task-manager";

	private const int ClosedTabCapacity = 10;

	public const double MinimumPanelWidth = 420.0;

	public const double MinimumPanelHeight = 400.0;

	private const double MaximumPanelDimension = 4096.0;

	private static readonly IReadOnlyList<AiTab> DefaultTabs = new List<AiTab>
	{
		new AiTab
		{
			Name = "ChatGPT",
			Url = "https://chatgpt.com",
			Icon = "openai"
		},
		new AiTab
		{
			Name = "Codex",
			Url = "https://chatgpt.com/codex",
			Icon = "openai"
		},
		new AiTab
		{
			Name = "Gemini",
			Url = "https://gemini.google.com",
			Icon = "googlegemini"
		},
		new AiTab
		{
			Name = "Claude",
			Url = "https://claude.ai",
			Icon = "anthropic"
		},
		new AiTab
		{
			Name = "Perplexity",
			Url = "https://www.perplexity.ai",
			Icon = "perplexity"
		},
		new AiTab
		{
			Name = "Grok",
			Url = "https://grok.com",
			Icon = "xai"
		}
	};

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	private readonly string _userSettingsPath = PortableDataPaths.SettingsPath;

	private UserSettings? _cachedSettings;

	public SettingsService()
	{
	}

	public SettingsService(string userSettingsPath)
	{
		_userSettingsPath = userSettingsPath;
	}

	public string UserSettingsPath => _userSettingsPath;

	public string UserSettingsDirectory => Path.GetDirectoryName(_userSettingsPath) ??
		throw new InvalidOperationException("Could not resolve the settings directory.");

	public IReadOnlyList<AiTab> LoadTabs()
	{
		UserSettings settings = LoadUserSettings();
		IReadOnlyList<AiTab> builtInTabs = ApplyBuiltInPinStates(LoadBuiltInTabs(), settings.TabPinStates);
		return builtInTabs.Concat(settings.CustomTabs).ToList();
	}

	public string? LoadLastSelectedTabId()
	{
		return LoadUserSettings().LastSelectedTabId;
	}

	public IReadOnlyList<string> LoadTabOrder()
	{
		return LoadUserSettings().TabOrder;
	}

	public IReadOnlyList<ClosedTabEntry> LoadClosedTabs()
	{
		return LoadUserSettings().ClosedTabs;
	}

	public bool LoadStartWithWindows()
	{
		return LoadUserSettings().StartWithWindows ?? false;
	}

	public bool? LoadStartWithWindowsPreference()
	{
		return LoadUserSettings().StartWithWindows;
	}

	public bool LoadAlwaysOnTop()
	{
		return LoadUserSettings().AlwaysOnTop ?? true;
	}

	public (double Width, double Height) LoadPanelSize(double defaultWidth, double defaultHeight)
	{
		UserSettings settings = LoadUserSettings();
		return (
			settings.PanelWidth ?? NormalizePanelDimension(defaultWidth, MinimumPanelWidth),
			settings.PanelHeight ?? NormalizePanelDimension(defaultHeight, MinimumPanelHeight));
	}

	public bool LoadLaunchCodexAutomatically()
	{
		return LoadUserSettings().LaunchCodexAutomatically ?? false;
	}

	public string LoadUpdateManifestUrl()
	{
		return LoadUserSettings().UpdateManifestUrl ?? string.Empty;
	}

	public bool LoadCheckForUpdatesOnStartup()
	{
		return LoadUserSettings().CheckForUpdatesOnStartup ?? false;
	}

	public CodexResetSettings LoadCodexResetSettings()
	{
		return LoadUserSettings().CodexReset ?? new CodexResetSettings();
	}

	public TripModeSettings LoadTripModeSettings()
	{
		return LoadUserSettings().TripMode ?? new TripModeSettings();
	}

	public IReadOnlyDictionary<string, double> LoadTabZoomFactors()
	{
		return NormalizeZoomFactors(LoadUserSettings().TabZoomFactors);
	}

	public bool LoadTabPinned(string tabId, bool defaultValue)
	{
		Dictionary<string, bool> pinStates = LoadUserSettings().TabPinStates;
		return pinStates.TryGetValue(tabId, out bool isPinned) ? isPinned : defaultValue;
	}

	public void SaveTabZoomFactor(string tabId, double zoomFactor)
	{
		UserSettings userSettings = LoadUserSettings();
		Dictionary<string, double> zoomFactors = NormalizeZoomFactors(userSettings.TabZoomFactors);
		if (Math.Abs(zoomFactor - 1.0) < 0.001)
		{
			zoomFactors.Remove(tabId);
		}
		else
		{
			zoomFactors[tabId] = Math.Clamp(Math.Round(zoomFactor, 2), 0.25, 3.0);
		}
		SaveUserSettings(CloneSettings(userSettings, tabZoomFactors: zoomFactors));
	}

	public void SaveTabPinState(string tabId, bool isPinned)
	{
		UserSettings userSettings = LoadUserSettings();
		Dictionary<string, bool> pinStates = NormalizeTabPinStates(userSettings.TabPinStates, out _);
		string? normalizedTabId = NormalizeTabId(tabId);
		if (normalizedTabId == null)
		{
			return;
		}
		pinStates[normalizedTabId] = isPinned;
		SaveUserSettings(CloneSettings(userSettings, tabPinStates: pinStates));
	}

	public void SaveCustomTabs(IEnumerable<AiTab> tabs)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(
			userSettings,
			customTabs: NormalizeCustomTabs(tabs.Where(tab => tab.IsCustom), out _)));
	}

	public void SaveClosedTabs(IEnumerable<ClosedTabEntry> closedTabs)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(
			userSettings,
			closedTabs: NormalizeClosedTabs(closedTabs, out _)));
	}

	public void SaveLastSelectedTabId(string tabId)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, lastSelectedTabId: tabId));
	}

	public void SaveTabOrder(IEnumerable<string> tabIds)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(
			userSettings,
			tabOrder: tabIds
				.Where(id => !string.IsNullOrWhiteSpace(id))
				.Distinct(StringComparer.Ordinal)
				.ToList()));
	}

	public void SaveStartWithWindows(bool enabled)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, startWithWindows: enabled));
	}

	public void SaveAlwaysOnTop(bool enabled)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, alwaysOnTop: enabled));
	}

	public void SavePanelSize(double width, double height)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(
			userSettings,
			panelWidth: NormalizePanelDimension(width, MinimumPanelWidth),
			panelHeight: NormalizePanelDimension(height, MinimumPanelHeight)));
	}

	public void SaveLaunchCodexAutomatically(bool enabled)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, launchCodexAutomatically: enabled));
	}

	public void SaveUpdateManifestUrl(string? manifestUrl)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(
			userSettings,
			updateManifestUrl: NormalizeUpdateManifestUrl(manifestUrl, out _),
			updateManifestUrlSet: true));
	}

	public void SaveCheckForUpdatesOnStartup(bool enabled)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, checkForUpdatesOnStartup: enabled));
	}

	public void SaveCodexResetSettings(CodexResetSettings settings)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, codexReset: NormalizeCodexResetSettings(settings)));
	}

	public void SaveTripModeSettings(TripModeSettings settings)
	{
		UserSettings userSettings = LoadUserSettings();
		SaveUserSettings(CloneSettings(userSettings, tripMode: NormalizeTripModeSettings(settings)));
	}

	public string BackupAndResetPanelPreferences()
	{
		UserSettings settings = LoadUserSettings();
		if (!File.Exists(_userSettingsPath))
		{
			SaveUserSettings(settings);
		}
		string backupPath = BackupExistingSettings("pre-reset");
		SaveUserSettings(new UserSettings
		{
			CustomTabs = settings.CustomTabs,
			ClosedTabs = settings.ClosedTabs,
			LastSelectedTabId = settings.LastSelectedTabId,
			TabOrder = settings.TabOrder,
			TabZoomFactors = NormalizeZoomFactors(settings.TabZoomFactors),
			TabPinStates = NormalizeTabPinStates(settings.TabPinStates, out _),
			StartWithWindows = false,
			AlwaysOnTop = true,
			LaunchCodexAutomatically = false,
			UpdateManifestUrl = null,
			CheckForUpdatesOnStartup = false,
			CodexReset = settings.CodexReset,
			TripMode = settings.TripMode
		});
		return backupPath;
	}

	public void ExportSettingsBackup(string targetPath)
	{
		UserSettings settings = LoadUserSettings();
		if (!File.Exists(_userSettingsPath))
		{
			SaveUserSettings(settings);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Could not resolve the backup directory."));
		File.Copy(_userSettingsPath, targetPath, overwrite: true);
	}

	public SettingsBackupSummary GetSettingsBackupSummary(string sourcePath)
	{
		UserSettings imported = NormalizeUserSettings(ReadUserSettingsFile(sourcePath), out _);
		return new SettingsBackupSummary(
			imported.CustomTabs.Count,
			imported.ClosedTabs.Count,
			!string.IsNullOrWhiteSpace(imported.UpdateManifestUrl),
			imported.StartWithWindows != null,
			imported.AlwaysOnTop != null);
	}

	public string? FindLatestPreImportBackup()
	{
		if (!Directory.Exists(UserSettingsDirectory))
		{
			return null;
		}
		return Directory.GetFiles(UserSettingsDirectory, "settings.pre-import-*.json")
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	public string? ImportSettingsBackup(string sourcePath)
	{
		UserSettings imported = ReadUserSettingsFile(sourcePath);
		UserSettings normalized = NormalizeUserSettings(imported, out _);
		string? backupPath = null;
		if (File.Exists(_userSettingsPath))
		{
			backupPath = BackupExistingSettings("pre-import");
		}
		SaveUserSettings(normalized);
		return backupPath;
	}

	public bool TryValidateSettingsFile(string sourcePath, out string? error)
	{
		try
		{
			_ = NormalizeUserSettings(ReadUserSettingsFile(sourcePath), out _);
			error = null;
			return true;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException || ex is InvalidDataException)
		{
			error = ex.Message;
			return false;
		}
	}

	public static string CreateCustomTabId()
	{
		return "custom:" + Guid.NewGuid().ToString("N");
	}

	public static string GetTabId(AiTab tab)
	{
		return string.IsNullOrWhiteSpace(tab.Id) ? GetLegacyTabId(tab) : tab.Id.Trim();
	}

	public static string GetLegacyTabId(AiTab tab)
	{
		return "web:" + tab.Url.Trim().TrimEnd('/').ToLowerInvariant();
	}

	public static AiTab EnsureUniqueCustomTabId(AiTab tab, ISet<string> usedIds)
	{
		string id = NormalizeTabId(tab.Id) ?? CreateCustomTabId();
		while (!usedIds.Add(id))
		{
			id = CreateCustomTabId();
		}
		return CloneCustomTab(tab, id);
	}

	public static bool TryNormalizeUrl(string input, out string normalizedUrl)
	{
		string text = input.Trim();
		if (!text.Contains("://", StringComparison.Ordinal))
		{
			text = "https://" + text;
		}
		if (Uri.TryCreate(text, UriKind.Absolute, out Uri? result) &&
			(result.Scheme == Uri.UriSchemeHttps || result.Scheme == Uri.UriSchemeHttp))
		{
			normalizedUrl = result.AbsoluteUri;
			return true;
		}
		normalizedUrl = string.Empty;
		return false;
	}

	public static Dictionary<string, double> NormalizeZoomFactors(IDictionary<string, double>? zoomFactors)
	{
		Dictionary<string, double> normalized = new Dictionary<string, double>(StringComparer.Ordinal);
		if (zoomFactors == null)
		{
			return normalized;
		}
		foreach (KeyValuePair<string, double> item in zoomFactors)
		{
			string? tabId = NormalizeTabId(item.Key);
			if (tabId == null ||
				!double.IsFinite(item.Value) ||
				Math.Abs(item.Value - 1.0) < 0.001)
			{
				continue;
			}
			normalized[tabId] = Math.Clamp(Math.Round(item.Value, 2), 0.25, 3.0);
		}
		return normalized;
	}

	private static UserSettings CloneSettings(
		UserSettings settings,
		List<AiTab>? customTabs = null,
		List<ClosedTabEntry>? closedTabs = null,
		string? lastSelectedTabId = null,
		List<string>? tabOrder = null,
		Dictionary<string, double>? tabZoomFactors = null,
		Dictionary<string, bool>? tabPinStates = null,
		bool? startWithWindows = null,
		bool? alwaysOnTop = null,
		double? panelWidth = null,
		double? panelHeight = null,
		bool? launchCodexAutomatically = null,
		string? updateManifestUrl = null,
		bool updateManifestUrlSet = false,
		bool? checkForUpdatesOnStartup = null,
		CodexResetSettings? codexReset = null,
		TripModeSettings? tripMode = null)
	{
		return new UserSettings
		{
			CustomTabs = customTabs ?? settings.CustomTabs,
			ClosedTabs = closedTabs ?? settings.ClosedTabs,
			LastSelectedTabId = lastSelectedTabId ?? settings.LastSelectedTabId,
			TabOrder = tabOrder ?? settings.TabOrder,
			TabZoomFactors = tabZoomFactors ?? NormalizeZoomFactors(settings.TabZoomFactors),
			TabPinStates = tabPinStates ?? NormalizeTabPinStates(settings.TabPinStates, out _),
			StartWithWindows = startWithWindows ?? settings.StartWithWindows,
			AlwaysOnTop = alwaysOnTop ?? settings.AlwaysOnTop,
			PanelWidth = panelWidth ?? settings.PanelWidth,
			PanelHeight = panelHeight ?? settings.PanelHeight,
			LaunchCodexAutomatically = launchCodexAutomatically ?? settings.LaunchCodexAutomatically,
			UpdateManifestUrl = updateManifestUrlSet ? updateManifestUrl : settings.UpdateManifestUrl,
			CheckForUpdatesOnStartup = checkForUpdatesOnStartup ?? settings.CheckForUpdatesOnStartup,
			CodexReset = codexReset ?? settings.CodexReset,
			TripMode = tripMode ?? settings.TripMode
		};
	}

	private void SaveUserSettings(UserSettings settings)
	{
		Directory.CreateDirectory(UserSettingsDirectory);
		string tempPath = _userSettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
			CommitSettingsFile(tempPath);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				try
				{
					File.Delete(tempPath);
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
		_cachedSettings = settings;
	}

	private void CommitSettingsFile(string tempPath)
	{
		if (!File.Exists(_userSettingsPath))
		{
			File.Move(tempPath, _userSettingsPath);
			return;
		}
		try
		{
			File.Replace(tempPath, _userSettingsPath, null);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			// Some endpoint security tools allow normal writes but block atomic replace.
			File.Copy(tempPath, _userSettingsPath, overwrite: true);
		}
	}

	private static IReadOnlyList<AiTab> LoadBuiltInTabs()
	{
		IReadOnlyList<AiTab> tabs = ReadTabs(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
		if (tabs.Count == 0)
		{
			tabs = DefaultTabs;
		}
		return AssignBuiltInIds(tabs);
	}

	private IReadOnlyList<AiTab> LoadCustomTabs()
	{
		return LoadUserSettings().CustomTabs;
	}

	private UserSettings LoadUserSettings()
	{
		if (_cachedSettings != null)
		{
			return _cachedSettings;
		}
		UserSettings settings;
		bool forceSave = false;
		try
		{
			settings = File.Exists(_userSettingsPath) ? ReadUserSettingsFile(_userSettingsPath) : new UserSettings();
		}
		catch (IOException)
		{
			settings = new UserSettings();
		}
		catch (UnauthorizedAccessException)
		{
			settings = new UserSettings();
		}
		catch (JsonException)
		{
			BackupCorruptSettings();
			settings = new UserSettings();
			forceSave = true;
		}
		catch (NotSupportedException)
		{
			BackupCorruptSettings();
			settings = new UserSettings();
			forceSave = true;
		}
		_cachedSettings = NormalizeUserSettings(settings, out bool changed);
		if (changed || forceSave)
		{
			try
			{
				SaveUserSettings(_cachedSettings);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (InvalidOperationException)
			{
			}
		}
		return _cachedSettings;
	}

	private static UserSettings ReadUserSettingsFile(string settingsPath)
	{
		return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath), JsonOptions) ?? new UserSettings();
	}

	private void BackupCorruptSettings()
	{
		if (File.Exists(_userSettingsPath))
		{
			BackupExistingSettings("corrupt");
		}
	}

	private string BackupExistingSettings(string label)
	{
		Directory.CreateDirectory(UserSettingsDirectory);
		string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		string backupPath = Path.Combine(UserSettingsDirectory, "settings." + label + "-" + timestamp + ".json");
		int suffix = 2;
		while (File.Exists(backupPath))
		{
			backupPath = Path.Combine(UserSettingsDirectory, "settings." + label + "-" + timestamp + "-" + suffix.ToString(CultureInfo.InvariantCulture) + ".json");
			suffix++;
		}
		File.Copy(_userSettingsPath, backupPath);
		return backupPath;
	}

	private static UserSettings NormalizeUserSettings(UserSettings settings, out bool changed)
	{
		Dictionary<string, List<string>> legacyToIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		HashSet<string> knownIds = new HashSet<string>(StringComparer.Ordinal)
		{
			NativeCodexTabId,
			NativeCodexUsageTabId,
			NativeWindowsHelperTabId,
			NativeTripPlannerTabId,
			NativeTaskManagerTabId
		};
		try
		{
			foreach (ExternalAppDefinition definition in new ExternalAppRegistry().GetAll())
			{
				knownIds.Add(definition.Id);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
		{
			// Invalid application definitions get reported by UI. Settings still load safely.
		}
		AddLegacyMapping(legacyToIds, LegacyNativeQuickToolsTabId, NativeCodexUsageTabId);
		AddLegacyMapping(legacyToIds, LegacyNativeQuickToolsTabId, NativeWindowsHelperTabId);
		AddLegacyMapping(legacyToIds, LegacyNativeQuickToolsTabId, NativeTripPlannerTabId);
		foreach (AiTab builtIn in LoadBuiltInTabs())
		{
			string tabId = GetTabId(builtIn);
			knownIds.Add(tabId);
			AddLegacyMapping(legacyToIds, GetLegacyTabId(builtIn), tabId);
		}

		List<AiTab> customTabs = NormalizeCustomTabs(settings.CustomTabs, out bool customTabsChanged);
		foreach (AiTab customTab in customTabs)
		{
			string tabId = GetTabId(customTab);
			knownIds.Add(tabId);
			AddLegacyMapping(legacyToIds, GetLegacyTabId(customTab), tabId);
		}

		string? selectedTabId = NormalizeSingleTabId(settings.LastSelectedTabId, knownIds, legacyToIds, out bool selectedChanged);
		List<string> tabOrder = NormalizeTabOrder(settings.TabOrder, knownIds, legacyToIds, out bool orderChanged);
		Dictionary<string, double> zoomFactors = NormalizeZoomFactors(settings.TabZoomFactors, knownIds, legacyToIds, out bool zoomChanged);
		Dictionary<string, bool> tabPinStates = NormalizeTabPinStates(settings.TabPinStates, knownIds, legacyToIds, out bool pinChanged);
		List<ClosedTabEntry> closedTabs = NormalizeClosedTabs(settings.ClosedTabs, out bool closedChanged);
		string? updateManifestUrl = NormalizeUpdateManifestUrl(settings.UpdateManifestUrl, out bool updateManifestChanged);
		double? panelWidth = NormalizePanelDimension(settings.PanelWidth, MinimumPanelWidth, out bool panelWidthChanged);
		double? panelHeight = NormalizePanelDimension(settings.PanelHeight, MinimumPanelHeight, out bool panelHeightChanged);

		changed = customTabsChanged ||
			selectedChanged ||
			orderChanged ||
			zoomChanged ||
			pinChanged ||
			closedChanged ||
			updateManifestChanged ||
			panelWidthChanged ||
			panelHeightChanged;

		return new UserSettings
		{
			CustomTabs = customTabs,
			ClosedTabs = closedTabs,
			LastSelectedTabId = selectedTabId,
			TabOrder = tabOrder,
			TabZoomFactors = zoomFactors,
			TabPinStates = tabPinStates,
			StartWithWindows = settings.StartWithWindows,
			AlwaysOnTop = settings.AlwaysOnTop,
			PanelWidth = panelWidth,
			PanelHeight = panelHeight,
			LaunchCodexAutomatically = settings.LaunchCodexAutomatically,
			UpdateManifestUrl = updateManifestUrl,
			CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup,
			CodexReset = settings.CodexReset,
			TripMode = settings.TripMode
		};
	}

	private static double NormalizePanelDimension(double value, double minimum)
	{
		return double.IsFinite(value)
			? Math.Clamp(Math.Round(value, 1), minimum, MaximumPanelDimension)
			: minimum;
	}

	private static double? NormalizePanelDimension(double? value, double minimum, out bool changed)
	{
		if (!value.HasValue)
		{
			changed = false;
			return null;
		}
		double normalized = NormalizePanelDimension(value.Value, minimum);
		changed = Math.Abs(normalized - value.Value) > 0.01;
		return normalized;
	}

	private static CodexResetSettings NormalizeCodexResetSettings(CodexResetSettings settings)
	{
		return new CodexResetSettings
		{
			WindowStartedAt = settings.WindowStartedAt,
			WindowEndedAt = settings.WindowEndedAt,
			WeeklyResetAt = settings.WeeklyResetAt,
			ResetExpiresAt = settings.ResetExpiresAt,
			BankedResets = Math.Clamp(settings.BankedResets, 0, 99),
			TokensUsed = Math.Max(0, settings.TokensUsed),
			TokenLimit = Math.Max(0, settings.TokenLimit),
			Notes = TrimOrNull(settings.Notes)
		};
	}

	private static TripModeSettings NormalizeTripModeSettings(TripModeSettings settings)
	{
		List<TripPlan> trips = (settings.Trips ?? new List<TripPlan>())
			.Select(NormalizeTripPlan)
			.Where(trip => !string.IsNullOrWhiteSpace(trip.Name))
			.Take(25)
			.ToList();
		string? selectedTripId = trips.Any(trip => string.Equals(trip.Id, settings.SelectedTripId, StringComparison.Ordinal))
			? settings.SelectedTripId
			: trips.FirstOrDefault()?.Id;
		return new TripModeSettings
		{
			SelectedTripId = selectedTripId,
			Trips = trips
		};
	}

	private static TripPlan NormalizeTripPlan(TripPlan trip)
	{
		return new TripPlan
		{
			Id = string.IsNullOrWhiteSpace(trip.Id) ? Guid.NewGuid().ToString("N") : trip.Id.Trim(),
			Name = string.IsNullOrWhiteSpace(trip.Name) ? "Trip" : trip.Name.Trim(),
			PackingItems = (trip.PackingItems ?? new List<TripChecklistItem>())
				.Select(item => new TripChecklistItem
				{
					Text = item.Text.Trim(),
					IsPacked = item.IsPacked
				})
				.Where(item => !string.IsNullOrWhiteSpace(item.Text))
				.Take(100)
				.ToList(),
			SavedMapsLinks = TrimOrNull(trip.SavedMapsLinks),
			CampingLinks = TrimOrNull(trip.CampingLinks),
			ParkingSpots = TrimOrNull(trip.ParkingSpots),
			FuelEstimate = TrimOrNull(trip.FuelEstimate),
			DailyBudget = TrimOrNull(trip.DailyBudget),
			EmergencyDocs = TrimOrNull(trip.EmergencyDocs),
			OfflineNotes = TrimOrNull(trip.OfflineNotes)
		};
	}

	private static string? TrimOrNull(string? value)
	{
		string? trimmed = value?.Trim();
		return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
	}

	private static List<AiTab> NormalizeCustomTabs(IEnumerable<AiTab>? tabs, out bool changed)
	{
		changed = false;
		List<AiTab> normalized = new List<AiTab>();
		HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
		if (tabs == null)
		{
			return normalized;
		}
		foreach (AiTab tab in tabs.Where(IsValid))
		{
			string? tabId = NormalizeTabId(tab.Id);
			if (tabId == null || !usedIds.Add(tabId))
			{
				tabId = CreateCustomTabId();
				while (!usedIds.Add(tabId))
				{
					tabId = CreateCustomTabId();
				}
				changed = true;
			}
			AiTab normalizedTab = CloneCustomTab(tab, tabId);
			if (!TabEqualsForSettings(tab, normalizedTab))
			{
				changed = true;
			}
			normalized.Add(normalizedTab);
		}
		return normalized;
	}

	private static string? NormalizeUpdateManifestUrl(string? manifestUrl, out bool changed)
	{
		changed = false;
		string? trimmed = manifestUrl?.Trim();
		if (string.IsNullOrWhiteSpace(trimmed))
		{
			changed = !string.IsNullOrEmpty(manifestUrl);
			return null;
		}
		if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
			(uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
		{
			changed = true;
			return null;
		}
		string normalized = uri.AbsoluteUri;
		changed = !string.Equals(manifestUrl, normalized, StringComparison.Ordinal);
		return normalized;
	}

	private static List<ClosedTabEntry> NormalizeClosedTabs(IEnumerable<ClosedTabEntry>? entries, out bool changed)
	{
		changed = false;
		List<ClosedTabEntry> normalized = new List<ClosedTabEntry>();
		HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
		if (entries == null)
		{
			return normalized;
		}
		foreach (ClosedTabEntry entry in entries.Where(entry => entry.Tab != null && IsValid(entry.Tab)).TakeLast(ClosedTabCapacity))
		{
			AiTab tab = entry.Tab;
			string? tabId = NormalizeTabId(tab.Id);
			if (tabId == null || !usedIds.Add(tabId))
			{
				tabId = CreateCustomTabId();
				while (!usedIds.Add(tabId))
				{
					tabId = CreateCustomTabId();
				}
				changed = true;
			}
			AiTab normalizedTab = CloneCustomTab(tab, tabId);
			normalized.Add(new ClosedTabEntry(normalizedTab, Math.Max(0, entry.PreviousIndex), entry.ClosedAt));
			if (!TabEqualsForSettings(tab, normalizedTab))
			{
				changed = true;
			}
		}
		if (entries.Count() > ClosedTabCapacity)
		{
			changed = true;
		}
		return normalized;
	}

	private static string? NormalizeSingleTabId(
		string? tabId,
		ISet<string> knownIds,
		IReadOnlyDictionary<string, List<string>> legacyToIds,
		out bool changed)
	{
		changed = false;
		string? normalized = NormalizeTabId(tabId);
		if (normalized == null)
		{
			return null;
		}
		if (knownIds.Contains(normalized))
		{
			return normalized;
		}
		if (legacyToIds.TryGetValue(normalized, out List<string>? migratedIds) && migratedIds.Count > 0)
		{
			changed = true;
			return migratedIds[0];
		}
		changed = true;
		return null;
	}

	private static List<string> NormalizeTabOrder(
		IEnumerable<string>? tabOrder,
		ISet<string> knownIds,
		IReadOnlyDictionary<string, List<string>> legacyToIds,
		out bool changed)
	{
		changed = false;
		List<string> normalized = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		if (tabOrder == null)
		{
			return normalized;
		}
		foreach (string rawId in tabOrder)
		{
			string? tabId = NormalizeTabId(rawId);
			if (tabId == null)
			{
				changed = true;
				continue;
			}
			IEnumerable<string> ids = knownIds.Contains(tabId)
				? new[] { tabId }
				: legacyToIds.TryGetValue(tabId, out List<string>? migratedIds)
					? migratedIds
					: Array.Empty<string>();
			bool found = false;
			foreach (string id in ids)
			{
				found = true;
				if (knownIds.Contains(id) && seen.Add(id))
				{
					normalized.Add(id);
				}
			}
			if (!found || !string.Equals(rawId, tabId, StringComparison.Ordinal))
			{
				changed = true;
			}
		}
		return normalized;
	}

	private static Dictionary<string, double> NormalizeZoomFactors(
		IDictionary<string, double>? zoomFactors,
		ISet<string> knownIds,
		IReadOnlyDictionary<string, List<string>> legacyToIds,
		out bool changed)
	{
		changed = false;
		Dictionary<string, double> normalized = new Dictionary<string, double>(StringComparer.Ordinal);
		if (zoomFactors == null)
		{
			return normalized;
		}
		foreach (KeyValuePair<string, double> item in zoomFactors)
		{
			string? tabId = NormalizeTabId(item.Key);
			if (tabId == null ||
				!double.IsFinite(item.Value) ||
				Math.Abs(item.Value - 1.0) < 0.001)
			{
				changed = true;
				continue;
			}
			double zoomFactor = Math.Clamp(Math.Round(item.Value, 2), 0.25, 3.0);
			IEnumerable<string> ids = knownIds.Contains(tabId)
				? new[] { tabId }
				: legacyToIds.TryGetValue(tabId, out List<string>? migratedIds)
					? migratedIds
					: Array.Empty<string>();
			bool found = false;
			foreach (string id in ids)
			{
				found = true;
				normalized[id] = zoomFactor;
			}
			if (!found || Math.Abs(zoomFactor - item.Value) > 0.001 || !string.Equals(tabId, item.Key, StringComparison.Ordinal))
			{
				changed = true;
			}
		}
		return normalized;
	}

	private static Dictionary<string, bool> NormalizeTabPinStates(IDictionary<string, bool>? pinStates, out bool changed)
	{
		changed = false;
		Dictionary<string, bool> normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
		if (pinStates == null)
		{
			return normalized;
		}
		foreach (KeyValuePair<string, bool> item in pinStates)
		{
			string? tabId = NormalizeTabId(item.Key);
			if (tabId == null)
			{
				changed = true;
				continue;
			}
			normalized[tabId] = item.Value;
			if (!string.Equals(tabId, item.Key, StringComparison.Ordinal))
			{
				changed = true;
			}
		}
		return normalized;
	}

	private static Dictionary<string, bool> NormalizeTabPinStates(
		IDictionary<string, bool>? pinStates,
		ISet<string> knownIds,
		IReadOnlyDictionary<string, List<string>> legacyToIds,
		out bool changed)
	{
		changed = false;
		Dictionary<string, bool> normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
		if (pinStates == null)
		{
			return normalized;
		}
		foreach (KeyValuePair<string, bool> item in pinStates)
		{
			string? tabId = NormalizeTabId(item.Key);
			if (tabId == null)
			{
				changed = true;
				continue;
			}
			IEnumerable<string> ids = knownIds.Contains(tabId)
				? new[] { tabId }
				: legacyToIds.TryGetValue(tabId, out List<string>? migratedIds)
					? migratedIds
					: Array.Empty<string>();
			bool found = false;
			foreach (string id in ids)
			{
				found = true;
				normalized[id] = item.Value;
			}
			if (!found || !string.Equals(tabId, item.Key, StringComparison.Ordinal))
			{
				changed = true;
			}
		}
		return normalized;
	}

	private static IReadOnlyList<AiTab> AssignBuiltInIds(IReadOnlyList<AiTab> tabs)
	{
		List<AiTab> assigned = new List<AiTab>(tabs.Count);
		HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < tabs.Count; index++)
		{
			AiTab tab = tabs[index];
			string baseId = NormalizeTabId(tab.Id) ?? "builtin:" + Slugify(tab.Name, "tab-" + (index + 1).ToString());
			string id = baseId;
			int suffix = 2;
			while (!usedIds.Add(id))
			{
				id = baseId + "-" + suffix.ToString();
				suffix++;
			}
			assigned.Add(new AiTab
			{
				Id = id,
				Name = tab.Name,
				Url = tab.Url,
				Icon = tab.Icon,
				IsPinned = true
			});
		}
		return assigned;
	}

	private static IReadOnlyList<AiTab> ApplyBuiltInPinStates(IReadOnlyList<AiTab> tabs, IReadOnlyDictionary<string, bool> pinStates)
	{
		return tabs.Select(tab =>
		{
			string tabId = GetTabId(tab);
			bool isPinned = pinStates.TryGetValue(tabId, out bool savedPinned) ? savedPinned : tab.IsPinned;
			return new AiTab
			{
				Id = tab.Id,
				Name = tab.Name,
				Url = tab.Url,
				Icon = tab.Icon,
				IsPinned = isPinned
			};
		}).ToList();
	}

	private static IReadOnlyList<AiTab> ReadTabs(string settingsPath)
	{
		try
		{
			return JsonSerializer.Deserialize<List<AiTab>>(File.ReadAllText(settingsPath), JsonOptions)?.Where(IsValid).ToList() ?? new List<AiTab>();
		}
		catch (IOException)
		{
			return Array.Empty<AiTab>();
		}
		catch (JsonException)
		{
			return Array.Empty<AiTab>();
		}
	}

	private static AiTab CloneCustomTab(AiTab tab, string id)
	{
		string? profileId = BrowserProfilePolicy.NormalizeProfileId(tab.BrowserProfileId);
		return new AiTab
		{
			Id = id,
			Name = tab.Name,
			Url = tab.Url,
			Icon = tab.Icon,
			BrowserProfileId = profileId,
			BrowserProfileLabel = BrowserProfilePolicy.NormalizeProfileLabel(tab.BrowserProfileLabel, profileId),
			IsPinned = tab.IsPinned,
			IsCustom = true
		};
	}

	private static bool TabEqualsForSettings(AiTab left, AiTab right)
	{
		return string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
			string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
			string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
			string.Equals(left.Icon, right.Icon, StringComparison.Ordinal) &&
			string.Equals(left.BrowserProfileId, right.BrowserProfileId, StringComparison.Ordinal) &&
			string.Equals(left.BrowserProfileLabel, right.BrowserProfileLabel, StringComparison.Ordinal) &&
			left.IsPinned == right.IsPinned &&
			left.IsCustom == right.IsCustom;
	}

	private static void AddLegacyMapping(Dictionary<string, List<string>> legacyToIds, string legacyId, string tabId)
	{
		if (!legacyToIds.TryGetValue(legacyId, out List<string>? tabIds))
		{
			tabIds = new List<string>();
			legacyToIds[legacyId] = tabIds;
		}
		tabIds.Add(tabId);
	}

	private static string? NormalizeTabId(string? id)
	{
		string? trimmed = id?.Trim();
		return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
	}

	private static string Slugify(string value, string fallback)
	{
		StringBuilder builder = new StringBuilder(value.Length);
		bool previousDash = false;
		foreach (char character in value.Trim().ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(character))
			{
				builder.Append(character);
				previousDash = false;
			}
			else if (!previousDash)
			{
				builder.Append('-');
				previousDash = true;
			}
		}
		string slug = builder.ToString().Trim('-');
		return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
	}

	private static bool IsValid(AiTab tab)
	{
		return !string.IsNullOrWhiteSpace(tab.Name) &&
			Uri.TryCreate(tab.Url, UriKind.Absolute, out Uri? result) &&
			(result.Scheme == Uri.UriSchemeHttps || result.Scheme == Uri.UriSchemeHttp);
	}
}
