using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPanel.Models;
using QuickPanel.Services;
using QuickPanel.Views;

namespace QuickPanel;

public enum BrowserProfileDeleteMode
{
	MoveTabsToDefault,
	CloseTabs
}

public sealed record BrowserProfileUsageSnapshot(
	string Id,
	string Name,
	bool IsDefault,
	int TabCount,
	string DataPath,
	bool IsLegacySharedProfile);

public partial class MainWindow
{
	private const string ManagedProfileMenuTag = "managed-profile-menu";
	private readonly BrowserProfileRegistryService _browserProfileRegistry = new();
	private bool _profileContextMenuHooked;

	protected override void OnInitialized(EventArgs e)
	{
		try
		{
			_browserProfileRegistry.ProcessPendingDeletions();
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			_logService.Error("Pending browser-profile cleanup could not be completed.", ex);
		}
		base.OnInitialized(e);
	}

	protected override void OnContentRendered(EventArgs e)
	{
		base.OnContentRendered(e);
		SynchronizeManagedProfiles();
		if (_profileContextMenuHooked)
		{
			return;
		}
		AddHandler(
			ContextMenuService.ContextMenuOpeningEvent,
			new ContextMenuEventHandler(ManagedProfiles_ContextMenuOpening),
			handledEventsToo: true);
		_profileContextMenuHooked = true;
	}

	private void ManagedProfiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
	{
		TabItem? tabItem = FindTabItemAncestor(e.OriginalSource as DependencyObject);
		if (tabItem?.Content is not BrowserTabView || tabItem.Tag is not string tabId)
		{
			return;
		}
		AiTab? tab = _tabs.FirstOrDefault(item => SettingsService.GetTabId(item).Equals(tabId, StringComparison.Ordinal));
		if (tab == null || tabItem.ContextMenu == null)
		{
			return;
		}

		ContextMenu contextMenu = tabItem.ContextMenu;
		RemoveInjectedProfileMenus(contextMenu);
		RemoveLegacyNewProfileMenu(contextMenu);

		MenuItem duplicateWithProfile = BuildProfilePickerMenu(tab, tabItem, "Duplicate with profile…", switchCurrentTab: false);
		MenuItem openWithProfile = BuildProfilePickerMenu(tab, tabItem, "Open with profile…", switchCurrentTab: true);
		duplicateWithProfile.Tag = ManagedProfileMenuTag;
		openWithProfile.Tag = ManagedProfileMenuTag;

		int insertIndex = 0;
		for (int i = 0; i < contextMenu.Items.Count; i++)
		{
			if (contextMenu.Items[i] is MenuItem item &&
				string.Equals(item.Header?.ToString(), "Duplicate tab", StringComparison.Ordinal))
			{
				insertIndex = i + 1;
				break;
			}
		}
		contextMenu.Items.Insert(insertIndex, duplicateWithProfile);
		contextMenu.Items.Insert(insertIndex + 1, openWithProfile);
	}

	private MenuItem BuildProfilePickerMenu(AiTab tab, TabItem sourceTabItem, string header, bool switchCurrentTab)
	{
		MenuItem root = new()
		{
			Header = header
		};
		IReadOnlyList<BrowserProfileDefinition> profiles = SynchronizeManagedProfiles();
		string currentRegistryId = BrowserProfileRegistryService.GetRegistryId(tab.BrowserProfileId);
		foreach (BrowserProfileDefinition profile in profiles)
		{
			bool isCurrent = profile.Id.Equals(currentRegistryId, StringComparison.OrdinalIgnoreCase);
			string suffix = profile.IsDefault ? " (default)" : string.Empty;
			MenuItem choice = new()
			{
				Header = (isCurrent ? "✓ " : string.Empty) + profile.Name + suffix,
				Tag = profile.Id,
				IsEnabled = !switchCurrentTab || !isCurrent
			};
			choice.Click += (_, _) => ApplyManagedProfile(tab, sourceTabItem, profile, switchCurrentTab);
			root.Items.Add(choice);
		}
		root.Items.Add(new Separator());
		MenuItem create = new()
		{
			Header = "+ Create new profile…"
		};
		create.Click += (_, _) =>
		{
			BrowserProfileDefinition? profile = PromptAndCreateManagedProfile();
			if (profile != null)
			{
				ApplyManagedProfile(tab, sourceTabItem, profile, switchCurrentTab);
			}
		};
		root.Items.Add(create);
		return root;
	}

	private void ApplyManagedProfile(
		AiTab sourceTab,
		TabItem sourceTabItem,
		BrowserProfileDefinition profile,
		bool switchCurrentTab)
	{
		if (switchCurrentTab)
		{
			SwitchCurrentTabToManagedProfile(sourceTab, sourceTabItem, profile);
			return;
		}
		DuplicateTabWithManagedProfile(sourceTab, sourceTabItem, profile);
	}

	private void DuplicateTabWithManagedProfile(
		AiTab sourceTab,
		TabItem sourceTabItem,
		BrowserProfileDefinition profile)
	{
		if (!SettingsService.TryNormalizeUrl(sourceTab.Url, out string normalizedUrl))
		{
			ShowSetupBanner("Could not duplicate with profile", "The tab URL is not a normal HTTP or HTTPS address.");
			return;
		}

		AiTab duplicate = PrepareNewCustomTab(new AiTab
		{
			Name = sourceTab.Name,
			Url = normalizedUrl,
			Icon = sourceTab.Icon,
			BrowserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(profile.Id),
			BrowserProfileLabel = profile.Id.Equals(BrowserProfileRegistryService.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
				? null
				: profile.Name,
			IsPinned = sourceTab.IsPinned,
			IsCustom = true
		});
		int sourceIndex = AiTabs.Items.IndexOf(sourceTabItem);
		AddCustomTab(duplicate, sourceIndex < 0 ? null : sourceIndex + 1);
		SynchronizeManagedProfiles();
	}

	private void SwitchCurrentTabToManagedProfile(
		AiTab sourceTab,
		TabItem sourceTabItem,
		BrowserProfileDefinition profile)
	{
		string currentUrl = GetCurrentTabUrl(sourceTab, sourceTabItem);
		if (!SettingsService.TryNormalizeUrl(currentUrl, out string normalizedUrl))
		{
			ShowSetupBanner("Could not open profile", "The current page is not a normal HTTP or HTTPS address.");
			return;
		}

		string tabId = SettingsService.GetTabId(sourceTab);
		int tabIndex = _tabs.FindIndex(existing =>
			SettingsService.GetTabId(existing).Equals(tabId, StringComparison.Ordinal));
		if (tabIndex < 0)
		{
			return;
		}

		string? browserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(profile.Id);
		string? profileLabel = browserProfileId == null ? null : profile.Name;
		AiTab updatedTab = CloneWithProfile(sourceTab, browserProfileId, profileLabel);
		AiTab originalTab = _tabs[tabIndex];
		_tabs[tabIndex] = updatedTab;
		try
		{
			if (updatedTab.IsCustom)
			{
				_settingsService.SaveCustomTabs(_tabs);
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			_tabs[tabIndex] = originalTab;
			ShowSettingsError(ex);
			return;
		}

		if (sourceTabItem.Content is BrowserTabView oldBrowserTabView)
		{
			DetachBrowserTabView(oldBrowserTabView);
			oldBrowserTabView.Dispose();
		}
		BrowserTabView replacement = new(updatedTab, normalizedUrl)
		{
			ZoomFactor = GetTabZoomFactor(tabId)
		};
		AttachBrowserTabView(replacement);
		sourceTabItem.Content = replacement;
		UpdateTabItemForTab(sourceTabItem, updatedTab, reloadContent: false);
		AiTabs.SelectedItem = sourceTabItem;
		SynchronizeManagedProfiles();
	}

	private BrowserProfileDefinition? PromptAndCreateManagedProfile()
	{
		ProfileNameWindow dialog = new("Create browsing profile")
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true)
		{
			return null;
		}
		try
		{
			return _browserProfileRegistry.Create(dialog.ProfileName);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSetupBanner("Could not create profile", ex.Message);
			return null;
		}
	}

	internal BrowserProfileDefinition? CreateManagedBrowserProfile(string name)
	{
		try
		{
			return _browserProfileRegistry.Create(name);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSetupBanner("Could not create profile", ex.Message);
			return null;
		}
	}

	internal bool RenameManagedBrowserProfile(string profileId, string newName, out string? error)
	{
		error = null;
		try
		{
			BrowserProfileDefinition updated = _browserProfileRegistry.Rename(profileId, newName);
			string? browserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(updated.Id);
			if (browserProfileId != null)
			{
				UpdateProfileLabelsInTabs(browserProfileId, updated.Name);
				UpdateProfileLabelsInClosedTabs(browserProfileId, updated.Name);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
		{
			error = ex.Message;
			return false;
		}
	}

	internal bool SetManagedDefaultBrowserProfile(string profileId, out string? error)
	{
		error = null;
		try
		{
			_browserProfileRegistry.SetDefault(profileId);
			return true;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
		{
			error = ex.Message;
			return false;
		}
	}

	internal bool DeleteManagedBrowserProfile(string profileId, BrowserProfileDeleteMode mode, out string? error)
	{
		error = null;
		try
		{
			IReadOnlyList<BrowserProfileDefinition> profiles = SynchronizeManagedProfiles();
			BrowserProfileDefinition deleting = profiles.First(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
			if (deleting.IsDefault)
			{
				error = "Make another profile the default before deleting this profile.";
				return false;
			}
			if (deleting.Id.Equals(BrowserProfileRegistryService.DefaultProfileId, StringComparison.OrdinalIgnoreCase))
			{
				error = "The original shared profile is retained for compatibility and cannot be deleted.";
				return false;
			}

			bool webViewDeleteRequested = TryRequestWebViewProfileDeletion(profileId);
			List<AiTab> affectedTabs = _tabs
				.Where(tab => BrowserProfileRegistryService.GetRegistryId(tab.BrowserProfileId).Equals(profileId, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (affectedTabs.Count > 0)
			{
				if (mode == BrowserProfileDeleteMode.MoveTabsToDefault)
				{
					BrowserProfileDefinition target = profiles.First(profile => profile.IsDefault);
					ReassignTabs(affectedTabs, target);
				}
				else
				{
					CloseAndForgetTabs(affectedTabs);
				}
			}

			RemoveOrReassignClosedTabs(profileId, mode, profiles.First(profile => profile.IsDefault));
			if (webViewDeleteRequested)
			{
				_browserProfileRegistry.MarkPendingDeletion(profileId);
			}
			else
			{
				_browserProfileRegistry.Delete(profileId);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
		{
			error = ex.Message;
			return false;
		}
	}

	internal IReadOnlyList<BrowserProfileUsageSnapshot> GetBrowserProfileUsageSnapshot()
	{
		IReadOnlyList<BrowserProfileDefinition> profiles = SynchronizeManagedProfiles();
		return profiles.Select(profile => new BrowserProfileUsageSnapshot(
			profile.Id,
			profile.Name,
			profile.IsDefault,
			_tabs.Count(tab => BrowserProfileRegistryService.GetRegistryId(tab.BrowserProfileId).Equals(profile.Id, StringComparison.OrdinalIgnoreCase)),
			_browserProfileRegistry.GetProfileDataPath(profile.Id),
			profile.Id.Equals(BrowserProfileRegistryService.DefaultProfileId, StringComparison.OrdinalIgnoreCase)))
			.ToArray();
	}

	private IReadOnlyList<BrowserProfileDefinition> SynchronizeManagedProfiles()
	{
		IEnumerable<AiTab> discoveryTabs = _tabs.Concat(_closedTabs.Entries.Select(entry => entry.Tab));
		try
		{
			return _browserProfileRegistry.Synchronize(discoveryTabs);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			_logService.Error("Browser profile registry could not be synchronized.", ex);
			return _browserProfileRegistry.GetProfiles();
		}
	}

	private bool TryRequestWebViewProfileDeletion(string registryProfileId)
	{
		string? browserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(registryProfileId);
		if (browserProfileId is null)
		{
			return false;
		}
		BrowserTabView? view = AiTabs.Items.OfType<TabItem>()
			.Select(item => item.Content)
			.OfType<BrowserTabView>()
			.FirstOrDefault(candidate => string.Equals(candidate.BrowserProfileId, browserProfileId, StringComparison.OrdinalIgnoreCase));
		if (view?.HasBrowsingProfile != true)
		{
			return false;
		}
		try
		{
			return view.DeleteBrowsingProfile();
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
		{
			_logService.Error("WebView2 profile deletion request failed; falling back to deferred filesystem cleanup.", ex);
			return false;
		}
	}

	private void UpdateProfileLabelsInTabs(string browserProfileId, string newName)
	{
		for (int i = 0; i < _tabs.Count; i++)
		{
			AiTab tab = _tabs[i];
			if (!string.Equals(BrowserProfilePolicy.NormalizeProfileId(tab.BrowserProfileId), browserProfileId, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			AiTab updated = CloneWithProfile(tab, browserProfileId, newName);
			_tabs[i] = updated;
			TabItem? tabItem = AiTabs.Items.OfType<TabItem>()
				.FirstOrDefault(item => item.Tag is string id && id.Equals(SettingsService.GetTabId(tab), StringComparison.Ordinal));
			if (tabItem != null)
			{
				UpdateTabItemForTab(tabItem, updated, reloadContent: false);
			}
		}
		_settingsService.SaveCustomTabs(_tabs);
	}

	private void UpdateProfileLabelsInClosedTabs(string browserProfileId, string newName)
	{
		List<ClosedTabEntry> updated = _closedTabs.Entries.Select(entry =>
		{
			if (!string.Equals(BrowserProfilePolicy.NormalizeProfileId(entry.Tab.BrowserProfileId), browserProfileId, StringComparison.OrdinalIgnoreCase))
			{
				return entry;
			}
			return new ClosedTabEntry(CloneWithProfile(entry.Tab, browserProfileId, newName), entry.PreviousIndex, entry.ClosedAt);
		}).ToList();
		_closedTabs.Load(updated);
		SaveClosedTabHistory();
	}

	private void ReassignTabs(IEnumerable<AiTab> affectedTabs, BrowserProfileDefinition target)
	{
		string? targetBrowserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(target.Id);
		HashSet<string> affectedIds = affectedTabs.Select(SettingsService.GetTabId).ToHashSet(StringComparer.Ordinal);
		for (int i = 0; i < _tabs.Count; i++)
		{
			AiTab tab = _tabs[i];
			string tabId = SettingsService.GetTabId(tab);
			if (!affectedIds.Contains(tabId))
			{
				continue;
			}
			AiTab updated = CloneWithProfile(tab, targetBrowserProfileId, targetBrowserProfileId == null ? null : target.Name);
			_tabs[i] = updated;
			TabItem? tabItem = AiTabs.Items.OfType<TabItem>()
				.FirstOrDefault(item => item.Tag is string id && id.Equals(tabId, StringComparison.Ordinal));
			if (tabItem != null)
			{
				UpdateTabItemForTab(tabItem, updated, reloadContent: true);
			}
		}
		_settingsService.SaveCustomTabs(_tabs);
	}

	private void CloseAndForgetTabs(IEnumerable<AiTab> affectedTabs)
	{
		HashSet<string> affectedIds = affectedTabs.Select(SettingsService.GetTabId).ToHashSet(StringComparer.Ordinal);
		foreach (TabItem tabItem in AiTabs.Items.OfType<TabItem>()
			.Where(item => item.Tag is string id && affectedIds.Contains(id))
			.ToList())
		{
			if (tabItem.Content is BrowserTabView browserView)
			{
				DetachBrowserTabView(browserView);
				browserView.Dispose();
			}
			AiTabs.Items.Remove(tabItem);
		}
		_tabs.RemoveAll(tab => affectedIds.Contains(SettingsService.GetTabId(tab)) && tab.IsCustom);
		_settingsService.SaveCustomTabs(_tabs);
		SaveTabOrder();
		if (AiTabs.SelectedItem == null && AiTabs.Items.Count > 0)
		{
			AiTabs.SelectedIndex = 0;
		}
	}

	private void RemoveOrReassignClosedTabs(string profileId, BrowserProfileDeleteMode mode, BrowserProfileDefinition defaultProfile)
	{
		string? targetBrowserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(defaultProfile.Id);
		List<ClosedTabEntry> entries = new();
		foreach (ClosedTabEntry entry in _closedTabs.Entries)
		{
			if (!BrowserProfileRegistryService.GetRegistryId(entry.Tab.BrowserProfileId).Equals(profileId, StringComparison.OrdinalIgnoreCase))
			{
				entries.Add(entry);
				continue;
			}
			if (mode == BrowserProfileDeleteMode.MoveTabsToDefault)
			{
				entries.Add(new ClosedTabEntry(
					CloneWithProfile(entry.Tab, targetBrowserProfileId, targetBrowserProfileId == null ? null : defaultProfile.Name),
					entry.PreviousIndex,
					entry.ClosedAt));
			}
		}
		_closedTabs.Load(entries);
		SaveClosedTabHistory();
	}

	private static AiTab CloneWithProfile(AiTab tab, string? browserProfileId, string? profileLabel)
	{
		return new AiTab
		{
			Id = tab.Id,
			Name = tab.Name,
			Url = tab.Url,
			Icon = tab.Icon,
			BrowserProfileId = browserProfileId,
			BrowserProfileLabel = browserProfileId == null ? null : profileLabel,
			IsPinned = tab.IsPinned,
			IsCustom = tab.IsCustom
		};
	}

	private static void RemoveInjectedProfileMenus(ContextMenu contextMenu)
	{
		foreach (object item in contextMenu.Items.Cast<object>()
			.Where(item => item is MenuItem menuItem && Equals(menuItem.Tag, ManagedProfileMenuTag))
			.ToList())
		{
			contextMenu.Items.Remove(item);
		}
	}

	private static void RemoveLegacyNewProfileMenu(ContextMenu contextMenu)
	{
		foreach (MenuItem item in contextMenu.Items.OfType<MenuItem>()
			.Where(item => string.Equals(item.Header?.ToString(), "Open current page with new profile", StringComparison.Ordinal))
			.ToList())
		{
			contextMenu.Items.Remove(item);
		}
	}

	private static TabItem? FindTabItemAncestor(DependencyObject? source)
	{
		DependencyObject? current = source;
		while (current != null)
		{
			if (current is TabItem tabItem)
			{
				return tabItem;
			}
			DependencyObject? visualParent = current is Visual || current is System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(current)
				: null;
			current = visualParent ?? LogicalTreeHelper.GetParent(current);
		}
		return null;
	}
}
