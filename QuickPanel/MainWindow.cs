using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QuickPanel.Models;
using QuickPanel.Services;
using QuickPanel.Views;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace QuickPanel;

public partial class MainWindow : Window, IComponentConnector
{
	private const string NativeCodexTabId = "native:codex";

	private const string NativeCodexUsageTabId = "native:codex-usage";

	private const string NativeWindowsHelperTabId = "native:windows-helper";

	private const string NativeTripPlannerTabId = "native:trip-planner";

	private const string NativeTaskManagerTabId = "native:task-manager";

	private const string SettingsPanelId = "native:settings";

	private const string TabDragFormat = "QuickPanel.TabItem";

	private const double ZoomStep = 0.1;

	private const double MinZoomFactor = 0.5;

	private const double MaxZoomFactor = 2.0;
	private const int WmKeyDown = 0x0100;

	private const int WmSysKeyDown = 0x0104;

	private const int VirtualKeyEscape = 0x1B;

	private readonly HotkeyService _hotkeyService = new HotkeyService();

	private readonly ExternalAppRegistry _externalAppRegistry = new ExternalAppRegistry();

	private readonly SettingsService _settingsService = new SettingsService();

	private readonly TabIconService _tabIconService = new TabIconService();
	private readonly ExternalAppIconService _externalAppIconService = new ExternalAppIconService();

	private readonly StartupService _startupService = new StartupService();

	private readonly LogService _logService = new LogService();

	private readonly UpdateService _updateService = new UpdateService();

	private readonly List<AiTab> _tabs = new List<AiTab>();

	private readonly Dictionary<string, ExternalAppTabView> _externalAppViews = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ExternalAppDefinition> _sessionExternalAppDefinitions = new(StringComparer.OrdinalIgnoreCase);

	private IReadOnlyList<ExternalAppDefinition> _externalAppDefinitions = [];

	private readonly ClosedTabHistory _closedTabs = new ClosedTabHistory();

	private readonly Dictionary<string, double> _tabZoomFactors = new Dictionary<string, double>(StringComparer.Ordinal);

	private readonly DispatcherTimer _panelSizeSaveTimer = new()
	{
		Interval = TimeSpan.FromMilliseconds(350.0)
	};

	private readonly bool _startHidden;

	private readonly string? _savedTabId;

	private readonly bool _launchCodexAutomatically;

	private TrayIconService? _trayIconService;

	private SettingsWindow? _settingsWindow;

	private UpdateCheckResult? _lastUpdateCheck;

	private CancellationTokenSource? _downloadUpdateCancellation;
	private readonly UpdateOutcome? _startupUpdateOutcome;

	private Point _tabDragStart;

	private TabItem? _draggedTab;

	private bool _isLoadingTabs = true;

	private bool _isReorderingTabs;

	private TabItem? _selectedTabDuringPanelResize;

	private bool _restoreSavedTabOnNextShow;

	private double _measuredTabChromeHeight = BrowserViewportLayout.FallbackChromeHeight;

	private bool _allowPanelSizePersistence;

	private double _lastSavedPanelWidth = -1.0;

	private double _lastSavedPanelHeight = -1.0;

	public MainWindow(bool startHidden = false)
	{
		_startupUpdateOutcome = UpdateOutcomeService.TryConsumeLatest() ??
			(UpdateRecoveryService.StartupAttentionMessage is null
				? null
				: new UpdateOutcome(
					UpdateRecoveryService.StartupAttentionMessage,
					UpdateRecoveryService.DefaultTransactionsRoot));
		_startHidden = startHidden;
		_savedTabId = _settingsService.LoadLastSelectedTabId();
		_launchCodexAutomatically = _settingsService.LoadLaunchCodexAutomatically();
		_restoreSavedTabOnNextShow = startHidden;
		foreach (KeyValuePair<string, double> zoomFactor in _settingsService.LoadTabZoomFactors())
		{
			_tabZoomFactors[zoomFactor.Key] = zoomFactor.Value;
		}
		_closedTabs.Load(_settingsService.LoadClosedTabs());
		_panelSizeSaveTimer.Tick += PanelSizeSaveTimer_Tick;
		InitializeComponent();
		(double panelWidth, double panelHeight) = _settingsService.LoadPanelSize(base.Width, base.Height);
		base.Width = panelWidth;
		base.Height = panelHeight;
		LoadExternalApplications();
		base.Topmost = _settingsService.LoadAlwaysOnTop();
		LoadTabs();
	}

	private void LoadExternalApplications()
	{
		try
		{
			_externalAppDefinitions = _externalAppRegistry.GetAll();
		}
		catch (Exception exception)
		{
			_externalAppDefinitions = [];
			_logService.Error("External application definitions could not be loaded.", exception);
			ShowSetupBanner("Could not load applications", exception.Message);
		}
	}

	private ExternalAppTabView GetOrCreateExternalAppView(ExternalAppDefinition definition, nint preferredWindowHandle = default)
	{
		if (_externalAppViews.TryGetValue(definition.Id, out ExternalAppTabView? existing))
		{
			existing.PreferWindow(preferredWindowHandle);
			return existing;
		}
		ExternalAppTabView view = new(definition, _logService, preferredWindowHandle)
		{
			Margin = new Thickness(0.0, GetTabChromeHeight(), 0.0, 0.0)
		};
		_externalAppViews[definition.Id] = view;
		ExternalAppLayer.Children.Add(view);
		view.SetActive(false);
		return view;
	}

	private void LoadTabs()
	{
		_isLoadingTabs = true;
		_tabs.Clear();
		_tabs.AddRange(_settingsService.LoadTabs());
		AiTabs.Items.Clear();
		bool externalAppsInserted = false;
		foreach (AiTab tab in _tabs)
		{
			if (!tab.IsPinned)
			{
				continue;
			}
			AiTabs.Items.Add(CreateTabItem(tab));
			if (!externalAppsInserted &&
				tab.Name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) &&
				_externalAppDefinitions.Count > 0)
			{
				AddPinnedExternalApplicationTabs(AiTabs.Items.Count);
				externalAppsInserted = true;
			}
		}
		if (!externalAppsInserted)
		{
			AddPinnedExternalApplicationTabs(0);
		}
		InsertDefaultNativePanels();
		RestoreTabOrder();
		ApplyPinnedOrdering();
		if (AiTabs.Items.Count > 0)
		{
			if (_startHidden)
			{
				AiTabs.SelectedIndex = 0;
			}
			else
			{
				RestoreSavedTab();
			}
		}
	}

	private void AddPinnedExternalApplicationTabs(int insertIndex)
	{
		foreach (ExternalAppDefinition definition in _externalAppDefinitions)
		{
			if (!_settingsService.LoadTabPinned(definition.Id, defaultValue: true)) continue;
			AiTabs.Items.Insert(Math.Clamp(insertIndex, 0, AiTabs.Items.Count), CreateExternalAppTabItem(definition));
			insertIndex++;
		}
	}

	private TabItem CreateExternalAppTabItem(ExternalAppDefinition definition, ExternalAppWindowCandidate? pickedWindow = null)
	{
		ExternalAppTabView externalAppView = GetOrCreateExternalAppView(definition, pickedWindow?.Handle ?? nint.Zero);
		MenuItem menuItem = new MenuItem
		{
			Header = "Undock " + definition.DisplayName,
			ToolTip = "Restore the application as its own window"
		};
		menuItem.Click += delegate
		{
			externalAppView.Undock();
		};
		MenuItem menuItem2 = new MenuItem
		{
			Header = "Dock " + definition.DisplayName,
			ToolTip = "Dock the desktop application in this panel"
		};
		menuItem2.Click += async delegate
		{
			bool useForegroundWindow = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
			await externalAppView.DockAsync(useForegroundWindow);
		};
		MenuItem cropTopBarMenuItem = new()
		{
			Header = "Hide app top bar",
			ToolTip = "Crop 32 pixels from a client-drawn title bar",
			IsCheckable = true,
			IsChecked = externalAppView.ContentTopOffsetAt96Dpi > 0
		};
		cropTopBarMenuItem.Click += delegate
		{
			externalAppView.SetContentTopOffset(cropTopBarMenuItem.IsChecked ? 32 : 0);
		};
		Grid header = new Grid
		{
			Width = 20.0,
			Height = 20.0
		};
		header.Children.Add(pickedWindow == null
			? _externalAppIconService.CreateImage(definition, 17.0)
			: _externalAppIconService.CreateImage(pickedWindow, 17.0));
		header.Children.Add(new Ellipse
		{
			Width = 5.0,
			Height = 5.0,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Fill = (Brush)FindResource("PanelAccentBrush"),
			Stroke = (Brush)FindResource("PanelChromeBrush"),
			StrokeThickness = 1.0
		});
		TabItem tabItem = new TabItem
		{
			Header = header,
			ToolTip = definition.DisplayName,
			Tag = definition.Id
		};
		tabItem.ContextMenu = IsSessionExternalAppTabId(definition.Id)
			? CreateSessionExternalAppContextMenu(tabItem, menuItem2, menuItem, cropTopBarMenuItem)
			: CreateNativeContextMenu(definition.Id, tabItem, menuItem2, menuItem, cropTopBarMenuItem);
		AutomationProperties.SetName(tabItem, definition.DisplayName);
		return tabItem;
	}

	private ContextMenu CreateSessionExternalAppContextMenu(TabItem tabItem, params MenuItem[] actions)
	{
		ContextMenu contextMenu = new();
		foreach (MenuItem action in actions)
		{
			contextMenu.Items.Add(action);
		}
		contextMenu.Items.Add(new Separator());
		MenuItem closeMenuItem = new()
		{
			Header = "Close docked tab",
			ToolTip = "Restore the app window and remove this tab"
		};
		closeMenuItem.Click += delegate
		{
			RemoveTabFromTabBar(tabItem);
			SaveTabOrder();
			QueueTabStripFadeUpdate();
		};
		contextMenu.Items.Add(closeMenuItem);
		return contextMenu;
	}

	private void InsertDefaultNativePanels()
	{
		int insertIndex = AiTabs.Items.OfType<TabItem>()
			.Select((item, index) => new { item, index })
			.Where(pair => pair.item.Tag is string tag && IsExternalAppTabId(tag))
			.Select(pair => pair.index + 1)
			.DefaultIfEmpty(AiTabs.Items.Count)
			.Max();
		foreach (string tabId in new[] { NativeCodexUsageTabId, NativeWindowsHelperTabId, NativeTripPlannerTabId, NativeTaskManagerTabId })
		{
			if (!_settingsService.LoadTabPinned(tabId, IsNativePanelPinnedByDefault(tabId)))
			{
				continue;
			}
			if (FindTabItem(tabId) != null)
			{
				continue;
			}
			AiTabs.Items.Insert(Math.Clamp(insertIndex, 0, AiTabs.Items.Count), CreateNativePanelTabItem(tabId));
			insertIndex++;
		}
	}

	private TabItem CreateNativePanelTabItem(string tabId)
	{
		(string name, UserControl content) = tabId switch
		{
			NativeCodexUsageTabId => ("Codex Usage", (UserControl)new QuickToolsTabView(_settingsService, QuickToolsPanelMode.CodexUsage)),
			NativeWindowsHelperTabId => ("Windows Helper", (UserControl)new QuickToolsTabView(_settingsService, QuickToolsPanelMode.WindowsHelper)),
			NativeTripPlannerTabId => ("Trip Planner", (UserControl)new QuickToolsTabView(_settingsService, QuickToolsPanelMode.TripPlanner)),
			NativeTaskManagerTabId => ("Task Manager", (UserControl)new TaskManagerTabView()),
			_ => throw new ArgumentOutOfRangeException(nameof(tabId), tabId, "Unknown native panel.")
		};
		TabItem tabItem = new TabItem
		{
			Header = CreateFluentIconHeader(tabId),
			ToolTip = name,
			Tag = tabId,
			Content = content
		};
		tabItem.ContextMenu = CreateNativeContextMenu(tabId, tabItem);
		AutomationProperties.SetName(tabItem, name);
		return tabItem;
	}

	private Border CreateFluentIconHeader(string tabId)
	{
		return new Border
		{
			Width = 20.0,
			Height = 20.0,
			Background = Brushes.Transparent,
			Child = new TextBlock
			{
				Text = NativePanelIconCatalog.GetGlyph(tabId),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
				FontSize = 15.0,
				Foreground = (Brush)FindResource("PanelAccentBrush")
			}
		};
	}

	private ContextMenu CreateNativeContextMenu(string tabId, TabItem tabItem, params MenuItem[] extraItems)
	{
		MenuItem pinMenuItem = new MenuItem();
		pinMenuItem.Click += delegate
		{
			ToggleNativePin(tabId, tabItem);
		};
		ContextMenu contextMenu = new ContextMenu();
		contextMenu.Items.Add(pinMenuItem);
		if (extraItems.Length > 0)
		{
			contextMenu.Items.Add(new Separator());
			foreach (MenuItem item in extraItems)
			{
				contextMenu.Items.Add(item);
			}
		}
		if (CanCloseNativePanel(tabId))
		{
			contextMenu.Items.Add(new Separator());
			MenuItem closeMenuItem = new MenuItem
			{
				Header = "Close panel",
				ToolTip = "Close this built-in panel. Add it back from the top-right list."
			};
			closeMenuItem.Click += delegate
			{
				CloseNativePanel(tabId, tabItem);
			};
			contextMenu.Items.Add(closeMenuItem);
		}
		contextMenu.Opened += delegate
		{
			bool isPinned = _settingsService.LoadTabPinned(tabId, IsNativePanelPinnedByDefault(tabId));
			pinMenuItem.Header = isPinned ? "Unpin tab" : "Pin tab";
		};
		return contextMenu;
	}

	private static bool CanCloseNativePanel(string tabId)
	{
		return tabId.Equals(NativeCodexUsageTabId, StringComparison.Ordinal) ||
			tabId.Equals(NativeWindowsHelperTabId, StringComparison.Ordinal) ||
			tabId.Equals(NativeTripPlannerTabId, StringComparison.Ordinal) ||
			tabId.Equals(NativeTaskManagerTabId, StringComparison.Ordinal);
	}

	private static bool IsNativePanelPinnedByDefault(string tabId)
	{
		return !tabId.Equals(NativeTaskManagerTabId, StringComparison.Ordinal);
	}

	private bool IsExternalAppTabId(string tabId)
	{
		return IsSessionExternalAppTabId(tabId) ||
			_externalAppDefinitions.Any(definition => definition.Id.Equals(tabId, StringComparison.OrdinalIgnoreCase));
	}

	private bool IsSessionExternalAppTabId(string tabId)
	{
		return _sessionExternalAppDefinitions.ContainsKey(tabId);
	}

	private bool IsEphemeralSessionTabId(string tabId)
	{
		return IsSessionExternalAppTabId(tabId) ||
			tabId.StartsWith("shell-explorer:", StringComparison.Ordinal);
	}

	private void CloseNativePanel(string tabId, TabItem tabItem)
	{
		if (!CanCloseNativePanel(tabId) || !AiTabs.Items.Contains(tabItem))
		{
			return;
		}
		int previousIndex = Math.Max(0, AiTabs.Items.IndexOf(tabItem));
		bool wasSelected = AiTabs.SelectedItem == tabItem;
		AiTabs.Items.Remove(tabItem);
		if (wasSelected && AiTabs.Items.Count > 0)
		{
			AiTabs.SelectedIndex = Math.Min(previousIndex, AiTabs.Items.Count - 1);
		}
		SaveTabOrder();
	}

	private async void AiTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		QueueTabStripFadeUpdate();
		if (e.OriginalSource == AiTabs && !_isLoadingTabs && !_isReorderingTabs)
		{
			if (AiTabs.SelectedItem is TabItem { Tag: string tag } tabItem)
			{
				SaveLastSelectedTab(tag);
				((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)new Action(tabItem.BringIntoView));
			}
			await ActivateSelectedTabAsync();
			RemoveInactiveUnpinnedTabs();
		}
		UpdateTitleNavigationButtons();
	}

	private async Task ActivateSelectedTabAsync(bool allowCodexDock = true)
	{
		if (AiTabs.SelectedItem is TabItem { Tag: string tag } && _externalAppViews.TryGetValue(tag, out ExternalAppTabView? selectedView))
		{
			foreach ((string id, ExternalAppTabView view) in _externalAppViews)
			{
				view.SetActive(id.Equals(tag, StringComparison.OrdinalIgnoreCase));
			}
			ExternalAppLayer.IsHitTestVisible = true;
			if (!allowCodexDock && tag.Equals(NativeCodexTabId, StringComparison.Ordinal))
			{
				_logService.Info("Codex docking skipped because automatic Codex launch is disabled.");
				return;
			}
			_logService.Info("External application docking requested: " + tag);
			await selectedView.DockAsync();
			if (AiTabs.SelectedItem is TabItem { Tag: string selectedTag } && selectedTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
			{
				selectedView.FocusApplication();
			}
		}
		else
		{
			foreach (ExternalAppTabView view in _externalAppViews.Values)
			{
				view.SetActive(false);
			}
			ExternalAppLayer.IsHitTestVisible = false;
			if (AiTabs.SelectedItem is TabItem tabItem)
			{
				QueueChromeFocusAfterTabSwitch(tabItem);
			}
		}
	}

	private void QueueChromeFocusAfterTabSwitch(TabItem tabItem)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
		{
			if (!ReferenceEquals(AiTabs.SelectedItem, tabItem) ||
				tabItem.Tag is not string tabId ||
				!FocusPolicy.ShouldFocusChromeAfterTabSwitch(IsExternalAppTabId(tabId)))
			{
				return;
			}
			if (!tabItem.Focus())
			{
				FocusSink.Focus();
			}
		});
	}

	private TabItem CreateTabItem(AiTab tab)
	{
		string tabId = SettingsService.GetTabId(tab);
		double zoomFactor = GetTabZoomFactor(tabId);
		BrowserTabView browserTabView = new BrowserTabView(tab);
		browserTabView.ZoomFactor = zoomFactor;
		AttachBrowserTabView(browserTabView);
		TabItem tabItem = new TabItem
		{
			Header = CreateWebTabHeader(tab),
			ToolTip = BuildTabToolTip(tab, zoomFactor),
			Tag = tabId,
			Content = browserTabView
		};
		AutomationProperties.SetName(tabItem, tab.Name + ", " + BrowserProfilePolicy.GetDisplayLabel(tab));
		if (tab.IsCustom)
		{
			tabItem.PreviewMouseUp += (sender, e) =>
			{
				if (e.ChangedButton == MouseButton.Middle)
				{
					ConfirmRemoveCustomTab(tab, tabItem);
					e.Handled = true;
				}
			};
		}
		tabItem.ContextMenu = CreateWebTabContextMenu(tab, tabItem);
		return tabItem;
	}

	private ContextMenu CreateWebTabContextMenu(AiTab tab, TabItem tabItem)
	{
		MenuItem duplicateMenuItem = new MenuItem
		{
			Header = "Duplicate tab"
		};
		duplicateMenuItem.Click += delegate
		{
			DuplicateTab(tab, tabItem);
		};
		MenuItem openWithNewProfileMenuItem = new MenuItem
		{
			Header = "Open current page with new profile",
			ToolTip = "Open the same page with separate cookies and login"
		};
		openWithNewProfileMenuItem.Click += delegate
		{
			OpenWithNewProfile(tab, tabItem);
		};
		MenuItem profileInfoMenuItem = new MenuItem
		{
			IsEnabled = false
		};
		MenuItem renameMenuItem = new MenuItem
		{
			Header = "Rename tab"
		};
		renameMenuItem.Click += delegate
		{
			EditCustomTab(tab, tabItem, AddTabFocusTarget.Name);
		};
		MenuItem editUrlMenuItem = new MenuItem
		{
			Header = "Edit URL"
		};
		editUrlMenuItem.Click += delegate
		{
			EditCustomTab(tab, tabItem, AddTabFocusTarget.Url);
		};
		MenuItem changeIconMenuItem = new MenuItem
		{
			Header = "Change icon"
		};
		changeIconMenuItem.Click += delegate
		{
			EditCustomTab(tab, tabItem, AddTabFocusTarget.Icon);
		};
		MenuItem pinMenuItem = new MenuItem();
		pinMenuItem.Click += delegate
		{
			TogglePin(tab, tabItem);
		};
		MenuItem reopenMenuItem = new MenuItem
		{
			Header = "Reopen closed tab",
			InputGestureText = "Ctrl+Shift+T"
		};
		reopenMenuItem.Click += delegate
		{
			RestoreClosedCustomTab();
		};
		MenuItem openExternalMenuItem = new MenuItem
		{
			Header = "Open in default browser",
			ToolTip = "Open current URL in default browser"
		};
		openExternalMenuItem.Click += delegate
		{
			OpenUrlExternally(GetCurrentTabUrl(tab, tabItem));
		};
		MenuItem copyUrlMenuItem = new MenuItem
		{
			Header = "Copy tab URL",
			ToolTip = "Copy current URL"
		};
		copyUrlMenuItem.Click += delegate
		{
			CopyUrlToClipboard(GetCurrentTabUrl(tab, tabItem));
		};
		MenuItem resetZoomMenuItem = new MenuItem
		{
			Header = "Reset zoom",
			InputGestureText = "Ctrl+0"
		};
		resetZoomMenuItem.Click += delegate
		{
			ResetTabZoom(tab, tabItem);
		};
		MenuItem clearTabDataMenuItem = new MenuItem
		{
			Header = "Clear this profile data",
			ToolTip = "Browsing data belongs to this tab's selected profile. Use Settings to clear that profile."
		};
		MenuItem? closeMenuItem = null;
		if (tab.IsCustom)
		{
			closeMenuItem = new MenuItem
			{
				Header = "Close tab",
				ToolTip = "Close custom tab (Ctrl+W)",
				StaysOpenOnClick = true
			};
			closeMenuItem.Click += (sender, e) =>
			{
				RemoveCustomTab(tab, tabItem, closeMenuItem);
			};
		}
		ContextMenu contextMenu = new ContextMenu
		{
			Items =
			{
				(object)duplicateMenuItem,
				(object)openWithNewProfileMenuItem,
				(object)profileInfoMenuItem,
				(object)new Separator(),
				(object)renameMenuItem,
				(object)editUrlMenuItem,
				(object)changeIconMenuItem,
				(object)pinMenuItem,
				(object)new Separator(),
				(object)resetZoomMenuItem,
				(object)openExternalMenuItem,
				(object)copyUrlMenuItem,
				(object)reopenMenuItem,
				(object)clearTabDataMenuItem
			}
		};
		if (closeMenuItem != null)
		{
			contextMenu.Items.Add(new Separator());
			contextMenu.Items.Add(closeMenuItem);
			contextMenu.Closed += delegate
			{
				ResetRemoveMenuItem(closeMenuItem);
			};
		}
		contextMenu.Opened += delegate
		{
			string tabId = SettingsService.GetTabId(tab);
			TabContextMenuState state = GetTabContextMenuState(tab, tabId);
			duplicateMenuItem.IsEnabled = state.CanDuplicateTab;
			openWithNewProfileMenuItem.IsEnabled = state.CanDuplicateTab;
			profileInfoMenuItem.Header = "Profile: " + BrowserProfilePolicy.GetDisplayLabel(tab);
			renameMenuItem.IsEnabled = state.CanEditCustomTab;
			editUrlMenuItem.IsEnabled = state.CanEditCustomTab;
			changeIconMenuItem.IsEnabled = state.CanEditCustomTab;
			pinMenuItem.IsEnabled = state.CanTogglePin;
			pinMenuItem.Header = state.IsPinned ? "Unpin tab" : "Pin tab";
			reopenMenuItem.IsEnabled = state.CanReopenClosedTab;
			openExternalMenuItem.IsEnabled = state.CanOpenExternally;
			copyUrlMenuItem.IsEnabled = state.CanCopyUrl;
			resetZoomMenuItem.IsEnabled = state.CanResetZoom;
			// Multiple tabs can reuse one profile, so a per-tab label would be misleading.
			// Keep this visible as a pointer to the profile-aware Settings action.
			clearTabDataMenuItem.IsEnabled = state.CanClearThisTabData;
			if (closeMenuItem != null)
			{
				closeMenuItem.IsEnabled = state.CanCloseTab;
			}
		};
		return contextMenu;
	}

	private TabContextMenuState GetTabContextMenuState(AiTab tab, string tabId)
	{
		return TabContextMenuStateFactory.Create(
			isCustomWebTab: tab.IsCustom,
			hasClosedTabs: _closedTabs.Count > 0,
			hasUrl: !string.IsNullOrWhiteSpace(tab.Url),
			zoomFactor: GetTabZoomFactor(tabId),
			isPinned: tab.IsPinned);
	}

	private string GetCurrentTabUrl(AiTab tab, TabItem tabItem)
	{
		if (tabItem.Content is BrowserTabView browserTabView)
		{
			return browserTabView.CurrentUrl;
		}
		return tab.Url;
	}

	private void UpdateTabItemForTab(TabItem tabItem, AiTab tab, bool reloadContent)
	{
		string tabId = SettingsService.GetTabId(tab);
		tabItem.Tag = tabId;
		tabItem.Header = CreateWebTabHeader(tab);
		tabItem.ToolTip = BuildTabToolTip(tab, GetTabZoomFactor(tabId));
		AutomationProperties.SetName(tabItem, tab.Name + ", " + BrowserProfilePolicy.GetDisplayLabel(tab));
		if (reloadContent)
		{
			if (tabItem.Content is BrowserTabView oldBrowserTabView)
			{
				DetachBrowserTabView(oldBrowserTabView);
				oldBrowserTabView.Dispose();
			}
			BrowserTabView browserTabView = new BrowserTabView(tab)
			{
				ZoomFactor = GetTabZoomFactor(tabId)
			};
			AttachBrowserTabView(browserTabView);
			tabItem.Content = browserTabView;
		}
		tabItem.ContextMenu = CreateWebTabContextMenu(tab, tabItem);
		UpdateTitleNavigationButtons();
	}

	private void SaveClosedTabHistory()
	{
		try
		{
			_settingsService.SaveClosedTabs(_closedTabs.Entries);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSettingsError(ex);
		}
	}

	private void BrowserTabView_NavigationStateChanged(object? sender, EventArgs e)
	{
		if (sender == (AiTabs.SelectedItem as TabItem)?.Content)
		{
			UpdateTitleNavigationButtons();
		}
	}

	private void AttachBrowserTabView(BrowserTabView browserTabView)
	{
		browserTabView.NavigationStateChanged -= BrowserTabView_NavigationStateChanged;
		browserTabView.WebsiteIconChanged -= BrowserTabView_WebsiteIconChanged;
		browserTabView.NavigationStateChanged += BrowserTabView_NavigationStateChanged;
		browserTabView.WebsiteIconChanged += BrowserTabView_WebsiteIconChanged;
	}

	private void DetachBrowserTabView(BrowserTabView browserTabView)
	{
		browserTabView.NavigationStateChanged -= BrowserTabView_NavigationStateChanged;
		browserTabView.WebsiteIconChanged -= BrowserTabView_WebsiteIconChanged;
	}

	private void BrowserTabView_WebsiteIconChanged(object? sender, WebsiteIconChangedEventArgs e)
	{
		if (sender is not BrowserTabView browserTabView)
		{
			return;
		}
		TabItem? tabItem = AiTabs.Items.OfType<TabItem>()
			.FirstOrDefault(item => ReferenceEquals(item.Content, browserTabView));
		if (tabItem?.Tag is not string tabId || FindTab(tabId) is not AiTab tab ||
			TabIconResolver.HasManualOverride(tab) || !TryGetWebTabHeaderImage(tabItem.Header, out Image image))
		{
			return;
		}
		_tabIconService.TryApplyWebsiteIcon(image, e.CachePath);
	}

	private static bool TryGetWebTabHeaderImage(object? header, out Image image)
	{
		Image? candidate = header switch
		{
			Image direct => direct,
			Panel panel => panel.Children.OfType<Image>().FirstOrDefault(),
			_ => null
		};
		image = candidate!;
		return candidate != null;
	}

	private void TitleBackButton_Click(object sender, RoutedEventArgs e)
	{
		if (AiTabs.SelectedItem is TabItem { Content: BrowserTabView browserTabView })
		{
			browserTabView.GoBack();
		}
	}

	private void TitleForwardButton_Click(object sender, RoutedEventArgs e)
	{
		if (AiTabs.SelectedItem is TabItem { Content: BrowserTabView browserTabView })
		{
			browserTabView.GoForward();
		}
	}

	private void TitleHomeButton_Click(object sender, RoutedEventArgs e)
	{
		if (AiTabs.SelectedItem is TabItem { Content: BrowserTabView browserTabView })
		{
			browserTabView.GoHome();
		}
	}

	private void TitleReloadButton_Click(object sender, RoutedEventArgs e)
	{
		ReloadSelectedBrowserTab();
	}

	private void UpdateTitleNavigationButtons()
	{
		bool isWebTab = AiTabs.SelectedItem is TabItem { Content: BrowserTabView };
		TitleNavigationDivider.Visibility = isWebTab ? Visibility.Visible : Visibility.Collapsed;
		TitleBackButton.Visibility = isWebTab ? Visibility.Visible : Visibility.Collapsed;
		TitleForwardButton.Visibility = isWebTab ? Visibility.Visible : Visibility.Collapsed;
		TitleHomeButton.Visibility = isWebTab ? Visibility.Visible : Visibility.Collapsed;
		TitleReloadButton.Visibility = isWebTab ? Visibility.Visible : Visibility.Collapsed;
		if (AiTabs.SelectedItem is TabItem { Content: BrowserTabView browserTabView })
		{
			TitleBackButton.IsEnabled = browserTabView.CanGoBack;
			TitleForwardButton.IsEnabled = browserTabView.CanGoForward;
			TitleHomeButton.IsEnabled = true;
			TitleReloadButton.IsEnabled = true;
			return;
		}
		TitleBackButton.IsEnabled = false;
		TitleForwardButton.IsEnabled = false;
		TitleHomeButton.IsEnabled = false;
		TitleReloadButton.IsEnabled = false;
	}

	private void Window_SourceInitialized(object? sender, EventArgs e)
	{
		WindowAppearanceService.Apply(this);
		if (!_startHidden)
		{
			WindowPositionService.MoveNearCursor(this);
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		WindowAppearanceService.Apply(this);
		_logService.Info("Quick Panel started. startupLaunch=" + _startHidden + "; processPath=" + (Environment.ProcessPath ?? string.Empty));
		if (!_startHidden)
		{
			_startupService.EnsureStartMenuShortcut();
		}
		ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
		AiTabs.LayoutUpdated += AiTabs_LayoutUpdated;
		QueueTabStripFadeUpdate();
		UpdateTabChromeMetrics();
		UpdateTitleNavigationButtons();
		_allowPanelSizePersistence = true;
		QueuePanelSizeSave();
		if (!_startHidden)
		{
			RestoreSavedTab();
		}
		_isLoadingTabs = false;
		((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)3, (Delegate)(Action)delegate
		{
			ActivateSelectedTabAsync(allowCodexDock: _launchCodexAutomatically);
		});
		_hotkeyService.Pressed += ToggleWindow;
		bool hotkeyEnabled = _hotkeyService.Register(this);
		bool? startWithWindowsPreference = _settingsService.LoadStartWithWindowsPreference();
		bool startupEnabled = _startupService.IsEnabled();
		if (!_startHidden && startWithWindowsPreference.HasValue)
		{
			startupEnabled = _startupService.SetEnabled(startWithWindowsPreference.Value);
		}
		InitializeTray();
		QueueStartupUpdateCheckIfEnabled();
		if (_startHidden)
		{
			QueueStartupCodexLaunchIfEnabled();
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				HidePanel();
				base.Opacity = 1.0;
				base.ShowActivated = true;
				base.ShowInTaskbar = true;
			}, (DispatcherPriority)2, Array.Empty<object>());
		}
		else
		{
			ShowSetupWarning(hotkeyEnabled, startupEnabled, startWithWindowsPreference == true);
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				WindowPositionService.MoveNearCursor(this);
			}, (DispatcherPriority)2, Array.Empty<object>());
		}
	}

	private void ShowSetupWarning(bool hotkeyEnabled, bool startupEnabled, bool startupExpected)
	{
		if (!hotkeyEnabled)
		{
			ShowSetupBanner("Hotkey unavailable", "Ctrl + Alt + G is already in use by another application.");
			return;
		}
		if (startupExpected && !startupEnabled)
		{
			ShowStartupRegistrationError();
		}
	}

	private void QueueStartupUpdateCheckIfEnabled()
	{
		if (!_settingsService.LoadCheckForUpdatesOnStartup())
		{
			return;
		}
		_logService.Info("Startup update check queued.");
		_ = Task.Run(async () =>
		{
			await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			try
			{
				UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(_settingsService.LoadUpdateManifestUrl()).ConfigureAwait(false);
				await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
				{
					ApplyUpdateCheckResult(result, _settingsWindow);
				}, DispatcherPriority.ApplicationIdle);
				_logService.Info("Startup update check completed. current=" + UpdateService.FormatVersion(result.CurrentVersion) +
					"; latest=" + UpdateService.FormatVersion(result.LatestVersion) +
					"; available=" + result.IsUpdateAvailable);
			}
			catch (Exception ex) when (IsUpdateCheckException(ex))
			{
				_logService.Error("Startup update check failed.", ex);
				await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
				{
					_settingsWindow?.SetUpdateStatus(
						UpdatePresentation.GetErrorMessage(ex, UpdateFailureStage.Check),
						canDownload: false,
						canOpenReleaseNotes: false);
					_settingsWindow?.SetReleaseNotes(null);
				}, DispatcherPriority.ApplicationIdle);
			}
		});
	}

	private void QueueStartupCodexLaunchIfEnabled()
	{
		if (!_launchCodexAutomatically)
		{
			_logService.Info("Startup Codex auto-launch skipped because the setting is disabled.");
			return;
		}
		_logService.Info("Startup Codex warm-up queued with a 45 second delay; hidden docking disabled.");
		_ = Task.Run(async () =>
		{
			await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
			await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
			{
				_logService.Info("Startup Codex delay elapsed; warming Codex without docking.");
				ExternalAppDefinition? codexDefinition = _externalAppDefinitions.FirstOrDefault(definition => definition.Id.Equals(NativeCodexTabId, StringComparison.Ordinal));
				if (codexDefinition != null)
				{
					ExternalAppTabView codexView = GetOrCreateExternalAppView(codexDefinition);
					_ = codexView.WarmUpAsync();
				}
			}, DispatcherPriority.ApplicationIdle);
		});
	}

	private void ToggleWindow()	{
		if (base.IsVisible)
		{
			HidePanel();
		}
		else
		{
			ShowPanel();
		}
	}

	private void ShowPanel()
	{
		Show();
		foreach (ExternalAppTabView view in _externalAppViews.Values)
		{
			view.SetPanelVisible(true);
		}
		base.WindowState = WindowState.Normal;
		WindowPositionService.MoveNearCursor(this);
		Activate();
		Focus();
		((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)3, (Delegate)(Action)async delegate
		{
			RestoreSavedTabOnFirstShow();
			WindowPositionService.MoveNearCursor(this);
			await ActivateSelectedTabAsync();
			FocusExternalApplicationIfSelected();
		});
	}

	private void Window_Activated(object? sender, EventArgs e)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (Mouse.DirectlyOver is not DependencyObject source || !HasButtonAncestor(source))
			{
				FocusExternalApplicationIfSelected();
			}
		}, DispatcherPriority.ApplicationIdle, Array.Empty<object>());
	}

	private void FocusExternalApplicationIfSelected()
	{
		if (_settingsWindow != null)
		{
			return;
		}
		if (AiTabs.SelectedItem is TabItem { Tag: string tag } &&
			_externalAppViews.TryGetValue(tag, out ExternalAppTabView? view) &&
			view.IsDocked)
		{
			view.RefreshApplication();
			view.FocusApplication();
		}
	}

	private void RestoreTabOrder()
	{
		IReadOnlyList<string> readOnlyList = _settingsService.LoadTabOrder();
		if (readOnlyList.Count == 0)
		{
			return;
		}
		List<TabItem> list = AiTabs.Items.OfType<TabItem>().ToList();
		Dictionary<string, TabItem> dictionary = list.Where((TabItem item) => item.Tag is string).ToDictionary<TabItem, string>((TabItem item) => (string)item.Tag, StringComparer.Ordinal);
		List<TabItem> list2 = new List<TabItem>(list.Count);
		foreach (string item in readOnlyList)
		{
			if (dictionary.Remove(item, out var value))
			{
				list2.Add(value);
			}
		}
		list2.AddRange(list.Where(dictionary.ContainsValue));
		AiTabs.Items.Clear();
		foreach (TabItem item2 in list2)
		{
			AiTabs.Items.Add(item2);
		}
		ApplyPinnedOrdering();
	}

	private void ApplyPinnedOrdering()
	{
		List<TabItem> items = AiTabs.Items.OfType<TabItem>().ToList();
		List<TabItem> ordered = items
			.Where(IsPinnedTabItem)
			.Concat(items.Where(item => !IsPinnedTabItem(item)))
			.ToList();
		if (items.SequenceEqual(ordered))
		{
			return;
		}
		_isReorderingTabs = true;
		try
		{
			object selectedItem = AiTabs.SelectedItem;
			AiTabs.Items.Clear();
			foreach (TabItem item in ordered)
			{
				AiTabs.Items.Add(item);
			}
			AiTabs.SelectedItem = selectedItem;
		}
		finally
		{
			_isReorderingTabs = false;
		}
	}

	private void RemoveInactiveUnpinnedTabs()
	{
		if (_isLoadingTabs || _isReorderingTabs)
		{
			return;
		}
		TabItem? selectedTab = AiTabs.SelectedItem as TabItem;
		List<TabItem> unpinnedTabs = AiTabs.Items
			.OfType<TabItem>()
			.Where(item => !ReferenceEquals(item, selectedTab) && !IsPinnedTabItem(item))
			.ToList();
		if (unpinnedTabs.Count == 0)
		{
			return;
		}
		_isReorderingTabs = true;
		try
		{
			foreach (TabItem tabItem in unpinnedTabs)
			{
				if (tabItem.Tag is string tabId)
				{
					ReleaseExternalAppTab(tabId);
				}
				AiTabs.Items.Remove(tabItem);
			}
		}
		finally
		{
			_isReorderingTabs = false;
		}
		SaveTabOrder();
		QueueTabStripFadeUpdate();
	}

	private void RemoveTabFromTabBar(TabItem tabItem)
	{
		if (!AiTabs.Items.Contains(tabItem))
		{
			return;
		}
		int previousIndex = Math.Max(0, AiTabs.Items.IndexOf(tabItem));
		bool wasSelected = AiTabs.SelectedItem == tabItem;
		if (tabItem.Tag is string tabId)
		{
			ReleaseExternalAppTab(tabId);
		}
		AiTabs.Items.Remove(tabItem);
		if (wasSelected && AiTabs.Items.Count > 0)
		{
			AiTabs.SelectedIndex = Math.Min(previousIndex, AiTabs.Items.Count - 1);
		}
	}

	private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		QueuePanelSizeSave();
	}

	private void PanelResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
	{
		_ = sender;
		_ = e;
		_selectedTabDuringPanelResize = AiTabs.SelectedItem as TabItem;
	}

	private void PanelResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
	{
		_ = sender;
		base.Width = PanelResizePolicy.ApplyDelta(base.ActualWidth, e.HorizontalChange, base.MinWidth);
		base.Height = PanelResizePolicy.ApplyDelta(base.ActualHeight, e.VerticalChange, base.MinHeight);
		RestorePanelResizeSelection();
	}

	private void PanelResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		_ = sender;
		_ = e;
		TabItem? selectedTab = _selectedTabDuringPanelResize;
		RestorePanelResizeSelection();
		_selectedTabDuringPanelResize = null;
		if (selectedTab == null) return;
		_ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
		{
			if (AiTabs.Items.Contains(selectedTab) && !ReferenceEquals(AiTabs.SelectedItem, selectedTab))
			{
				AiTabs.SelectedItem = selectedTab;
			}
		}));
	}

	private void RestorePanelResizeSelection()
	{
		TabItem? selectedTab = _selectedTabDuringPanelResize;
		if (selectedTab != null && AiTabs.Items.Contains(selectedTab) && !ReferenceEquals(AiTabs.SelectedItem, selectedTab))
		{
			AiTabs.SelectedItem = selectedTab;
		}
	}

	private void QueuePanelSizeSave()
	{
		if (!_allowPanelSizePersistence || base.WindowState != WindowState.Normal)
		{
			return;
		}
		_panelSizeSaveTimer.Stop();
		_panelSizeSaveTimer.Start();
	}

	private void PanelSizeSaveTimer_Tick(object? sender, EventArgs e)
	{
		_ = sender;
		_ = e;
		_panelSizeSaveTimer.Stop();
		SavePanelSize();
	}

	private void SavePanelSize()
	{
		double width = base.ActualWidth;
		double height = base.ActualHeight;
		if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0.0 || height <= 0.0 ||
			(Math.Abs(width - _lastSavedPanelWidth) < 0.1 && Math.Abs(height - _lastSavedPanelHeight) < 0.1))
		{
			return;
		}
		try
		{
			_settingsService.SavePanelSize(width, height);
			_lastSavedPanelWidth = width;
			_lastSavedPanelHeight = height;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			_logService.Error("Panel size could not be saved.", exception);
		}
	}

	private void ReleaseExternalAppTab(string tabId)
	{
		_sessionExternalAppDefinitions.Remove(tabId);
		if (_externalAppViews.Remove(tabId, out ExternalAppTabView? view))
		{
			if (view.IsHitTestVisible) ExternalAppLayer.IsHitTestVisible = false;
			ExternalAppLayer.Children.Remove(view);
			view.Dispose();
		}
	}

	private bool IsPinnedTabItem(TabItem tabItem)
	{
		if (tabItem.Tag is not string tabId)
		{
			return false;
		}
		if (IsEphemeralSessionTabId(tabId))
		{
			return true;
		}
		if (IsExternalAppTabId(tabId))
		{
			return _settingsService.LoadTabPinned(tabId, defaultValue: true);
		}
		if (CanCloseNativePanel(tabId))
		{
			return _settingsService.LoadTabPinned(tabId, IsNativePanelPinnedByDefault(tabId));
		}
		AiTab? tab = FindTab(tabId);
		return tab != null && tab.IsPinned;
	}

	private void RestoreSavedTabOnFirstShow()
	{
		if (!_restoreSavedTabOnNextShow)
		{
			return;
		}
		_restoreSavedTabOnNextShow = false;
		_isLoadingTabs = true;
		try
		{
			RestoreSavedTab();
		}
		finally
		{
			_isLoadingTabs = false;
		}
	}

	private void RestoreSavedTab()
	{
		if (string.IsNullOrWhiteSpace(_savedTabId))
		{
			AiTabs.SelectedIndex = 0;
			return;
		}
		TabItem tabItem = AiTabs.Items.OfType<TabItem>().FirstOrDefault((TabItem item) => item.Tag is string text && text.Equals(_savedTabId, StringComparison.Ordinal));
		AiTabs.SelectedItem = tabItem ?? AiTabs.Items[0];
	}

	private void SaveLastSelectedTab(string tabId)
	{
		if (IsEphemeralSessionTabId(tabId)) return;
		try
		{
			_settingsService.SaveLastSelectedTabId(tabId);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			ShowSettingsError(ex);
		}
	}

	private void SaveTabOrder()
	{
		try
		{
			_settingsService.SaveTabOrder((from item in AiTabs.Items.OfType<TabItem>()
				select item.Tag).OfType<string>().Where(tabId => !IsEphemeralSessionTabId(tabId)));
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			ShowSettingsError(ex);
		}
	}

	private void InitializeTray()
	{
		_trayIconService = new TrayIconService(_startupService.IsEnabled());
		_trayIconService.OpenRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)ShowPanel);
		};
		_trayIconService.SettingsRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)OpenSettingsFromTray);
		};
		_trayIconService.StartupChanged += delegate(bool enabled)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				SetStartWithWindows(enabled);
			});
		};
		_trayIconService.ExitRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)base.Close);
		};
	}

	private void SetStartWithWindows(bool enabled)
	{
		bool enabled2 = _startupService.IsEnabled();
		_logService.Info("Start with Windows setting requested: " + enabled);
		if (!_startupService.SetEnabled(enabled))
		{
			_trayIconService?.UpdateStartupState(enabled2);
			_logService.Info("Start with Windows update failed: " + _startupService.LastAttemptResult);
			ShowStartupRegistrationError();
			return;
		}
		try
		{
			_settingsService.SaveStartWithWindows(enabled);
			_trayIconService?.UpdateStartupState(enabled);
			_logService.Info("Start with Windows setting saved: " + enabled + "; " + _startupService.LastAttemptResult);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			_startupService.SetEnabled(enabled2);
			_trayIconService?.UpdateStartupState(enabled2);
			_logService.Error("Start with Windows setting could not be saved.", ex);
			ShowSettingsError(ex);
		}
	}

	private void SettingsButton_Click(object sender, RoutedEventArgs e)
	{
		OpenSettings();
	}

	private void DockWindowButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		DockWindowPickerWindow picker = new()
		{
			Owner = this
		};
		if (picker.ShowDialog() != true || picker.SelectedWindow is not ExternalAppWindowCandidate pickedWindow)
		{
			return;
		}
		if (ShellExplorerLocationService.IsFileExplorerWindow(pickedWindow))
		{
			OpenShellExplorerTab(pickedWindow);
			return;
		}

		ExternalAppDefinition definition = ExternalAppDefinitionFactory.CreateForWindow(pickedWindow);
		_sessionExternalAppDefinitions[definition.Id] = definition;
		TabItem? tabItem = FindTabItem(definition.Id);
		if (tabItem == null)
		{
			tabItem = CreateExternalAppTabItem(definition, pickedWindow);
			AiTabs.Items.Add(tabItem);
			ApplyPinnedOrdering();
		}
		else
		{
			GetOrCreateExternalAppView(definition, pickedWindow.Handle);
		}

		AiTabs.SelectedItem = tabItem;
		tabItem.BringIntoView();
		QueueTabStripFadeUpdate();
	}

	private void OpenShellExplorerTab(ExternalAppWindowCandidate pickedWindow)
	{
		if (!ShellExplorerLocationService.TryGetFolderPath(pickedWindow.Handle, out string folderPath))
		{
			MessageBox.Show(
				this,
				"Quick Panel could not read a physical folder from that Explorer window. The original window was not changed.",
				"File Explorer",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
			return;
		}

		ShellExplorerTabView shellView;
		try
		{
			shellView = new ShellExplorerTabView(folderPath);
		}
		catch (Exception exception)
		{
			_logService.Error("The in-panel File Explorer view could not be created.", exception);
			MessageBox.Show(
				this,
				"Quick Panel could not open that folder in its safe Explorer view. The original Explorer window was not changed.",
				"File Explorer",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
			return;
		}

		string tabId = "shell-explorer:" + Guid.NewGuid().ToString("N");
		var tabItem = new TabItem
		{
			Header = new TextBlock
			{
				Text = "\uE8B7",
				FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
				FontSize = 16,
				Foreground = (Brush)FindResource("PanelAccentBrush")
			},
			ToolTip = "File Explorer — " + folderPath,
			Tag = tabId,
			Content = shellView
		};
		var closeItem = new MenuItem { Header = "Close Explorer tab" };
		closeItem.Click += delegate { RemoveTabFromTabBar(tabItem); };
		tabItem.ContextMenu = new ContextMenu { Items = { closeItem } };
		AutomationProperties.SetName(tabItem, "File Explorer " + folderPath);
		AiTabs.Items.Add(tabItem);
		AiTabs.SelectedItem = tabItem;
		tabItem.BringIntoView();
		QueueTabStripFadeUpdate();
	}

	private void OpenSettingsFromTray()
	{
		if (!base.IsVisible)
		{
			ShowPanel();
		}
		OpenSettings();
	}

	private void OpenSettings()
	{
		if (_settingsWindow != null)
		{
			_settingsWindow.Activate();
			return;
		}
		SettingsWindow settingsWindow = new SettingsWindow(
			_startupService.IsEnabled(),
			_settingsService.LoadLaunchCodexAutomatically(),
			base.Topmost,
			_settingsService.LoadUpdateManifestUrl(),
			_settingsService.LoadCheckForUpdatesOnStartup(),
			BuildStartupDiagnostics())
		{
			Owner = this
		};
		_settingsWindow = settingsWindow;
		if (_startupUpdateOutcome is not null)
		{
			settingsWindow.SetUpdateStatus(
				_startupUpdateOutcome.Message + " Log: " + _startupUpdateOutcome.LogPath,
				canDownload: false,
				canOpenReleaseNotes: false);
		}
		settingsWindow.Closed += SettingsWindow_Closed;
		settingsWindow.ClearBrowsingDataRequested += SettingsWindow_ClearBrowsingDataRequested;
		settingsWindow.CheckForUpdatesRequested += SettingsWindow_CheckForUpdatesRequested;
		settingsWindow.DownloadUpdateRequested += SettingsWindow_DownloadUpdateRequested;
		settingsWindow.CancelDownloadRequested += SettingsWindow_CancelDownloadRequested;
		settingsWindow.OpenReleaseNotesRequested += SettingsWindow_OpenReleaseNotesRequested;
		settingsWindow.CopyDiagnosticsRequested += SettingsWindow_CopyDiagnosticsRequested;
		settingsWindow.DisableStartupRequested += SettingsWindow_DisableStartupRequested;
		settingsWindow.OpenSettingsFolderRequested += SettingsWindow_OpenSettingsFolderRequested;
		settingsWindow.ExportSettingsBackupRequested += SettingsWindow_ExportSettingsBackupRequested;
		settingsWindow.ImportSettingsBackupRequested += SettingsWindow_ImportSettingsBackupRequested;
		settingsWindow.RestorePreviousBackupRequested += SettingsWindow_RestorePreviousBackupRequested;
		settingsWindow.ResetLayoutRequested += SettingsWindow_ResetLayoutRequested;
		settingsWindow.Show();
		settingsWindow.Activate();
	}

	private void SettingsWindow_Closed(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		settingsWindow.Closed -= SettingsWindow_Closed;
		settingsWindow.ClearBrowsingDataRequested -= SettingsWindow_ClearBrowsingDataRequested;
		settingsWindow.CheckForUpdatesRequested -= SettingsWindow_CheckForUpdatesRequested;
		settingsWindow.DownloadUpdateRequested -= SettingsWindow_DownloadUpdateRequested;
		settingsWindow.CancelDownloadRequested -= SettingsWindow_CancelDownloadRequested;
		settingsWindow.OpenReleaseNotesRequested -= SettingsWindow_OpenReleaseNotesRequested;
		settingsWindow.CopyDiagnosticsRequested -= SettingsWindow_CopyDiagnosticsRequested;
		settingsWindow.DisableStartupRequested -= SettingsWindow_DisableStartupRequested;
		settingsWindow.OpenSettingsFolderRequested -= SettingsWindow_OpenSettingsFolderRequested;
		settingsWindow.ExportSettingsBackupRequested -= SettingsWindow_ExportSettingsBackupRequested;
		settingsWindow.ImportSettingsBackupRequested -= SettingsWindow_ImportSettingsBackupRequested;
		settingsWindow.RestorePreviousBackupRequested -= SettingsWindow_RestorePreviousBackupRequested;
		settingsWindow.ResetLayoutRequested -= SettingsWindow_ResetLayoutRequested;
		if (ReferenceEquals(_settingsWindow, settingsWindow))
		{
			_settingsWindow = null;
		}
		if (!settingsWindow.WasSaved)
		{
			return;
		}
		if (settingsWindow.StartWithWindows != _startupService.IsEnabled())
		{
			SetStartWithWindows(settingsWindow.StartWithWindows);
		}
		if (settingsWindow.AlwaysOnTop != base.Topmost)
		{
			SetAlwaysOnTop(settingsWindow.AlwaysOnTop);
		}
		if (settingsWindow.LaunchCodexAutomatically != _settingsService.LoadLaunchCodexAutomatically())
		{
			try
			{
				_settingsService.SaveLaunchCodexAutomatically(settingsWindow.LaunchCodexAutomatically);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
			{
				ShowSettingsError(ex);
			}
		}
		try
		{
			_settingsService.SaveUpdateManifestUrl(settingsWindow.UpdateManifestUrl);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSettingsError(ex);
		}
		if (settingsWindow.CheckForUpdatesOnStartup != _settingsService.LoadCheckForUpdatesOnStartup())
		{
			try
			{
				_settingsService.SaveCheckForUpdatesOnStartup(settingsWindow.CheckForUpdatesOnStartup);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
			{
				ShowSettingsError(ex);
			}
		}
	}

	private async void SettingsWindow_ClearBrowsingDataRequested(object? sender, EventArgs e)
	{
		try
		{
			await ClearSelectedBrowsingDataAsync();
			ShowSetupBanner("Browsing data cleared", "WebView2 data was cleared for the selected browsing profile.");
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is IOException || ex is System.Runtime.InteropServices.COMException)
		{
			ShowSetupBanner("Could not clear browsing data", ex.Message);
		}
	}

	private async Task ClearSelectedBrowsingDataAsync()
	{
		BrowserTabView? browserTabView = (AiTabs.SelectedItem as TabItem)?.Content as BrowserTabView;
		if (browserTabView == null)
		{
			throw new InvalidOperationException("Select a web tab before clearing browsing data.");
		}
		if (!browserTabView.HasBrowsingProfile)
		{
			throw new InvalidOperationException("Wait for the selected web tab to finish loading, then try again.");
		}
		await browserTabView.ClearAllBrowsingDataAsync();
	}

	private async void SettingsWindow_CheckForUpdatesRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		settingsWindow.SetUpdateChecking(true);
		settingsWindow.SetReleaseNotes(null);
		settingsWindow.SetUpdateStatus("Checking for updates...", canDownload: false, canOpenReleaseNotes: false);
		try
		{
			_settingsService.SaveUpdateManifestUrl(settingsWindow.CurrentUpdateManifestUrl);
			_logService.Info("Manual update check started. source=" + (string.IsNullOrWhiteSpace(settingsWindow.CurrentUpdateManifestUrl) ? "GitHub Releases" : "version manifest"));
			UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(settingsWindow.CurrentUpdateManifestUrl);
			ApplyUpdateCheckResult(result, settingsWindow);
			_logService.Info("Manual update check completed. current=" + UpdateService.FormatVersion(result.CurrentVersion) +
				"; latest=" + UpdateService.FormatVersion(result.LatestVersion) +
				"; available=" + result.IsUpdateAvailable);
		}
		catch (Exception ex) when (IsUpdateCheckException(ex))
		{
			_logService.Error("Manual update check failed.", ex);
			_lastUpdateCheck = null;
			settingsWindow.SetUpdateAvailableBadge(false);
			_trayIconService?.SetUpdateAvailable(false);
			settingsWindow.SetReleaseNotes(null);
			settingsWindow.SetUpdateStatus(
				UpdatePresentation.GetErrorMessage(ex, UpdateFailureStage.Check),
				canDownload: false,
				canOpenReleaseNotes: false);
		}
		finally
		{
			settingsWindow.SetUpdateChecking(false);
		}
	}

	private void ApplyUpdateCheckResult(UpdateCheckResult result, SettingsWindow? settingsWindow)
	{
		_lastUpdateCheck = result;
		UpdateVersionState state = UpdatePresentation.GetVersionState(result.CurrentVersion, result.LatestVersion);
		bool isUpdateAvailable = state == UpdateVersionState.UpdateAvailable;
		bool hasDownload = UpdatePresentation.CanDownload(
			state,
			hasDownloadAsset: !string.IsNullOrWhiteSpace(result.Manifest.DownloadUrl));
		bool hasReleasePage = !string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotesUrl);
		string latestVersion = UpdateService.FormatVersion(result.LatestVersion);
		string message = UpdatePresentation.GetStatusMessage(result.CurrentVersion, result.LatestVersion);
		settingsWindow?.SetUpdateAvailableBadge(isUpdateAvailable);
		settingsWindow?.SetReleaseNotes(result.Manifest.ReleaseNotes);
		settingsWindow?.SetUpdateStatus(message, hasDownload, hasReleasePage);
		_trayIconService?.SetUpdateAvailable(isUpdateAvailable, isUpdateAvailable ? "Update available: v" + latestVersion : null);
	}

	private async void SettingsWindow_DownloadUpdateRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow || _lastUpdateCheck == null)
		{
			return;
		}
		bool hasNotes = !string.IsNullOrWhiteSpace(_lastUpdateCheck.Manifest.ReleaseNotesUrl);
		_downloadUpdateCancellation?.Dispose();
		_downloadUpdateCancellation = new CancellationTokenSource();
		UpdateFailureStage failureStage = UpdateFailureStage.Download;
		settingsWindow.SetUpdateStatus("Downloading update...", canDownload: false, canOpenReleaseNotes: hasNotes);
		settingsWindow.SetDownloadActive(true);
		try
		{
			Progress<DownloadProgress> progress = new Progress<DownloadProgress>(downloadProgress =>
			{
				string message = downloadProgress.Percent == null
					? "Downloading update..."
					: "Downloading update... " + downloadProgress.Percent.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
				settingsWindow.SetDownloadProgress(downloadProgress.Percent, message);
			});
			DownloadUpdateResult result = await _updateService.DownloadUpdateAsync(
				_lastUpdateCheck.Manifest,
				progress: progress,
				cancellationToken: _downloadUpdateCancellation.Token);
			string verified = result.Sha256Verified ? " SHA256 verified." : string.Empty;
			settingsWindow.SetDownloadActive(false);
			settingsWindow.SetUpdateStatus("Downloaded " + System.IO.Path.GetFileName(result.FilePath) + "." + verified, canDownload: false, canOpenReleaseNotes: hasNotes);
			if (!result.Sha256Verified || string.IsNullOrWhiteSpace(_lastUpdateCheck.Manifest.Sha256))
			{
				settingsWindow.SetUpdateStatus(
					"Downloaded, but automatic installation requires a SHA256-verified release.",
					canDownload: false,
					canOpenReleaseNotes: hasNotes);
				return;
			}

			failureStage = UpdateFailureStage.InstallPreparation;
			MessageBoxResult install = MessageBox.Show(
				this,
				"The update was SHA256 verified. Install it now? Quick Panel will close and reopen. Settings, tabs, logins, cookies, and WebView sessions stay in the separate user-data directory.",
				"Install Quick Panel update",
				MessageBoxButton.YesNo,
				MessageBoxImage.Information,
				MessageBoxResult.Yes);
			if (install == MessageBoxResult.Yes)
			{
				settingsWindow.SetUpdateStatus(
					"Preparing verified rollback and update guardian...",
					canDownload: false,
					canOpenReleaseNotes: hasNotes);
				await Task.Run(() => PortableUpdateAutoApply.ApplyVerifiedDownload(
					result.FilePath,
					_lastUpdateCheck.Manifest.Sha256,
					result.Sha256Verified));
				settingsWindow.SetUpdateStatus(
					"Rollback verified. Restarting to install the update...",
					canDownload: false,
					canOpenReleaseNotes: hasNotes);
				Application.Current.Shutdown();
			}
		}
		catch (OperationCanceledException) when (failureStage == UpdateFailureStage.Download)
		{
			settingsWindow.SetDownloadActive(false);
			settingsWindow.SetUpdateStatus("Download canceled.", canDownload: true, canOpenReleaseNotes: hasNotes);
		}
		catch (Exception ex) when (ex is UpdateException || ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidOperationException || ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
		{
			settingsWindow.SetDownloadActive(false);
			settingsWindow.SetUpdateStatus(
				UpdatePresentation.GetErrorMessage(ex, failureStage),
				canDownload: true,
				canOpenReleaseNotes: hasNotes);
		}
		finally
		{
			_downloadUpdateCancellation?.Dispose();
			_downloadUpdateCancellation = null;
		}
	}

	private void SettingsWindow_CancelDownloadRequested(object? sender, EventArgs e)
	{
		_downloadUpdateCancellation?.Cancel();
	}

	private static bool IsUpdateCheckException(Exception exception)
	{
		return exception is UpdateException ||
			exception is HttpRequestException ||
			exception is TaskCanceledException ||
			exception is InvalidOperationException ||
			exception is InvalidDataException ||
			exception is JsonException ||
			exception is IOException;
	}

	private void SettingsWindow_OpenReleaseNotesRequested(object? sender, EventArgs e)
	{
		string? url = _lastUpdateCheck?.Manifest.ReleaseNotesUrl;
		if (string.IsNullOrWhiteSpace(url))
		{
			return;
		}
		if (!UpdateService.IsSecureUpdateUri(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			(sender as SettingsWindow)?.SetUpdateStatus("Release page URL must use HTTPS. Localhost HTTP is allowed for testing.", canDownload: false, canOpenReleaseNotes: false);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = uri.AbsoluteUri,
				UseShellExecute = true
			});
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			(sender as SettingsWindow)?.SetUpdateStatus("Could not open release page: " + ex.Message, canDownload: false, canOpenReleaseNotes: true);
		}
	}

	private void SettingsWindow_CopyDiagnosticsRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		try
		{
			Clipboard.SetText(BuildDiagnostics());
			settingsWindow.SetAdvancedStatus("Diagnostics copied.");
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.ExternalException)
		{
			settingsWindow.SetAdvancedStatus("Could not copy diagnostics: " + ex.Message);
		}
	}

	private void SettingsWindow_DisableStartupRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		try
		{
			bool disabled = _startupService.DisableAllStartupEntries();
			_settingsService.SaveStartWithWindows(false);
			_trayIconService?.UpdateStartupState(false);
			_logService.Info("Disable startup requested from Settings. result=" + disabled + "; " + _startupService.LastAttemptResult);
			settingsWindow.SetStartupDisabled(BuildStartupDiagnostics());
			settingsWindow.SetAdvancedStatus(disabled
				? "Startup disabled."
				: "Startup disable was blocked by Windows or endpoint security. See diagnostics below.");
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			_logService.Error("Disable startup failed.", ex);
			settingsWindow.SetAdvancedStatus("Could not disable startup: " + ex.Message);
			settingsWindow.SetDiagnosticsText(BuildStartupDiagnostics());
		}
	}

	private void SettingsWindow_OpenSettingsFolderRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(_settingsService.UserSettingsDirectory);
			Process.Start(new ProcessStartInfo
			{
				FileName = _settingsService.UserSettingsDirectory,
				UseShellExecute = true
			});
			settingsWindow.SetAdvancedStatus("Settings folder opened.");
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			settingsWindow.SetAdvancedStatus("Could not open settings folder: " + ex.Message);
		}
	}

	private void SettingsWindow_ExportSettingsBackupRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		SaveFileDialog dialog = new SaveFileDialog
		{
			Title = "Export settings backup",
			FileName = "QuickPanel-settings-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json",
			Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			_settingsService.ExportSettingsBackup(dialog.FileName);
			settingsWindow.SetAdvancedStatus("Settings backup exported.");
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			settingsWindow.SetAdvancedStatus("Could not export backup: " + ex.Message);
		}
	}

	private void SettingsWindow_ImportSettingsBackupRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		OpenFileDialog dialog = new OpenFileDialog
		{
			Title = "Import settings backup",
			Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		if (!_settingsService.TryValidateSettingsFile(dialog.FileName, out string? error))
		{
			settingsWindow.SetAdvancedStatus("Backup invalid: " + error);
			return;
		}
		try
		{
			if (!ConfirmSettingsImport(dialog.FileName, "Import settings backup?"))
			{
				return;
			}
			string? backupPath = _settingsService.ImportSettingsBackup(dialog.FileName);
			string backupMessage = string.IsNullOrWhiteSpace(backupPath) ? string.Empty : " Previous settings backed up first.";
			settingsWindow.SetAdvancedStatus("Backup imported." + backupMessage + " Restart Quick Panel to apply all settings.");
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is InvalidDataException || ex is JsonException)
		{
			settingsWindow.SetAdvancedStatus("Could not import backup: " + ex.Message);
		}
	}

	private void SettingsWindow_RestorePreviousBackupRequested(object? sender, EventArgs e)
	{
		if (sender is not SettingsWindow settingsWindow)
		{
			return;
		}
		string? backupPath = _settingsService.FindLatestPreImportBackup();
		if (string.IsNullOrWhiteSpace(backupPath))
		{
			settingsWindow.SetAdvancedStatus("No previous pre-import backup found.");
			return;
		}
		try
		{
			if (!ConfirmSettingsImport(backupPath, "Restore previous backup?"))
			{
				return;
			}
			_settingsService.ImportSettingsBackup(backupPath);
			settingsWindow.SetAdvancedStatus("Previous backup restored. Restart Quick Panel to apply all settings.");
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is InvalidDataException || ex is JsonException)
		{
			settingsWindow.SetAdvancedStatus("Could not restore backup: " + ex.Message);
		}
	}

	private bool ConfirmSettingsImport(string sourcePath, string title)
	{
		SettingsBackupSummary summary = _settingsService.GetSettingsBackupSummary(sourcePath);
		string message =
			"Custom tabs: " + summary.CustomTabCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
			"Closed tabs: " + summary.ClosedTabCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
			"Update manifest URL: " + FormatYesNo(summary.HasUpdateManifestUrl) + Environment.NewLine +
			"Startup setting: " + FormatYesNo(summary.HasStartupSetting) + Environment.NewLine +
			"Always-on-top setting: " + FormatYesNo(summary.HasAlwaysOnTopSetting) + Environment.NewLine + Environment.NewLine +
			"Current settings will be backed up first.";
		return MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
	}

	private static string FormatYesNo(bool value)
	{
		return value ? "yes" : "no";
	}

	private void SettingsWindow_ResetLayoutRequested(object? sender, EventArgs e)
	{
		WindowPositionService.MoveNearCursor(this);
		(sender as SettingsWindow)?.SetAdvancedStatus("Layout reset near cursor.");
	}

	private string BuildDiagnostics()
	{
		TabItem? selectedTab = AiTabs.SelectedItem as TabItem;
		string selectedTabId = selectedTab?.Tag as string ?? "(none)";
		string selectedTabName = selectedTab == null ? "(none)" : GetTabDisplayName(selectedTab);
		string selectedTabUrl = selectedTab?.Content is BrowserTabView browserTabView
			? browserTabView.CurrentUrl
			: GetNativePanelDisplayName(selectedTabId) ?? "Native panel";
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		StringBuilder builder = new StringBuilder();
		builder.AppendLine("Quick Panel diagnostics");
		builder.AppendLine("App version: " + UpdateService.GetCurrentVersionText());
		builder.AppendLine("Windows version: " + Environment.OSVersion.VersionString);
		builder.AppendLine("WebView2 runtime version: " + GetWebViewRuntimeVersion());
		builder.Append(BuildStartupDiagnostics());
		builder.AppendLine("Settings path: " + _settingsService.UserSettingsPath);
		builder.AppendLine("Portable data path: " + PortableDataPaths.DataDirectory);
		builder.AppendLine("Portable data active: " + FormatYesNo(PortableDataPaths.IsUnderPortableDataDirectory(_settingsService.UserSettingsPath)));
		builder.AppendLine("Log path: " + _logService.LogDirectory);
		builder.AppendLine("Current tab name: " + selectedTabName);
		builder.AppendLine("Current tab origin: " + FormatDiagnosticPageOrigin(selectedTabUrl));
		builder.AppendLine("Selected tab ID: " + selectedTabId);
		builder.AppendLine("DPI scale: " + dpi.DpiScaleX.ToString("0.##", CultureInfo.InvariantCulture) + " x " + dpi.DpiScaleY.ToString("0.##", CultureInfo.InvariantCulture));
		builder.AppendLine("Monitor size: " + SystemParameters.PrimaryScreenWidth.ToString("0", CultureInfo.InvariantCulture) + " x " + SystemParameters.PrimaryScreenHeight.ToString("0", CultureInfo.InvariantCulture));
		return builder.ToString();
	}

	internal static string FormatDiagnosticPageOrigin(string? value)
	{
		return DiagnosticPrivacy.FormatPageOrigin(value);
	}

	private string BuildStartupDiagnostics()
	{
		StartupDiagnostics diagnostics = _startupService.GetDiagnostics();
		StringBuilder builder = new StringBuilder();
		builder.AppendLine("Install path: " + diagnostics.InstallPath);
		builder.AppendLine("Recommended install path: " + diagnostics.RecommendedInstallDirectory);
		builder.AppendLine("Portable settings/log path: " + diagnostics.SettingsDirectory);
		builder.AppendLine("Installed in recommended path: " + FormatYesNo(diagnostics.IsRecommendedInstallPath));
		builder.AppendLine("Startup method: " + diagnostics.StartupMethod);
		builder.AppendLine("Startup enabled: " + FormatYesNo(diagnostics.IsStartupEnabled));
		builder.AppendLine("Startup shortcut: " + diagnostics.StartupShortcutPath);
		builder.AppendLine("Start Menu shortcut: " + diagnostics.StartMenuShortcutPath);
		builder.AppendLine("Legacy Run key present: " + FormatYesNo(diagnostics.LegacyRunKeyPresent));
		builder.AppendLine("Legacy CMD startup launcher present: " + FormatYesNo(diagnostics.LegacyCommandLauncherPresent));
		builder.AppendLine("Launch Codex automatically: " + FormatYesNo(_settingsService.LoadLaunchCodexAutomatically()));
		builder.AppendLine("Check for updates on startup: " + FormatYesNo(_settingsService.LoadCheckForUpdatesOnStartup()));
		builder.AppendLine("Startup delay for optional Codex launch: 45 seconds");
		builder.AppendLine("Last startup attempt result: " + diagnostics.LastAttemptResult);
		builder.AppendLine("Bitdefender-safe whitelist paths:");
		builder.AppendLine("  " + diagnostics.RecommendedInstallDirectory);
		builder.AppendLine("  " + diagnostics.SettingsDirectory);
		return builder.ToString();
	}

	private static string GetWebViewRuntimeVersion()
	{
		try
		{
			return CoreWebView2Environment.GetAvailableBrowserVersionString();
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is InvalidOperationException)
		{
			return "Unavailable: " + ex.Message;
		}
	}

	private static void OpenFileInExplorer(string filePath)
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = "explorer.exe",
			Arguments = "/select,\"" + filePath + "\"",
			UseShellExecute = true
		});
	}

	private void SetAlwaysOnTop(bool enabled)
	{
		try
		{
			_settingsService.SaveAlwaysOnTop(enabled);
			base.Topmost = enabled;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSettingsError(ex);
		}
	}

	private void AddTabButton_Click(object sender, RoutedEventArgs e)
	{
		OpenAddTabDialog();
	}

	private void OpenAddTabDialog()
	{
		AddTabWindow addTabWindow = new AddTabWindow
		{
			Owner = this
		};
		addTabWindow.ConfigureProfiles(GetTabsForProfileDiscovery());
		if (addTabWindow.ShowDialog() != true)
		{
			return;
		}
		AiTab tab = addTabWindow.CreatedTab;
		if (tab == null)
		{
			return;
		}
		tab = PrepareNewCustomTab(tab);
		AddCustomTab(tab);
	}

	private void AddCustomTab(AiTab tab, int? visualIndex = null)
	{
		try
		{
			_tabs.Add(tab);
			_settingsService.SaveCustomTabs(_tabs);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			_tabs.Remove(tab);
			ShowSettingsError(ex);
			return;
		}
		TabItem tabItem = CreateTabItem(tab);
		if (visualIndex.HasValue)
		{
			AiTabs.Items.Insert(Math.Clamp(visualIndex.Value, 0, AiTabs.Items.Count), tabItem);
		}
		else
		{
			AiTabs.Items.Add(tabItem);
		}
		ApplyPinnedOrdering();
		AiTabs.SelectedItem = tabItem;
		SaveTabOrder();
	}

	private AiTab PrepareNewCustomTab(AiTab tab)
	{
		HashSet<string> usedIds = new HashSet<string>(_tabs.Select(SettingsService.GetTabId), StringComparer.Ordinal);
		foreach (ExternalAppDefinition definition in _externalAppDefinitions)
		{
			usedIds.Add(definition.Id);
		}
		usedIds.Add(NativeCodexUsageTabId);
		usedIds.Add(NativeWindowsHelperTabId);
		usedIds.Add(NativeTripPlannerTabId);
		usedIds.Add(NativeTaskManagerTabId);
		return SettingsService.EnsureUniqueCustomTabId(tab, usedIds);
	}

	private void DuplicateTab(AiTab tab, TabItem sourceTabItem)
	{
		AiTab duplicate = PrepareNewCustomTab(new AiTab
		{
			Name = tab.Name,
			Url = tab.Url,
			Icon = tab.Icon,
			BrowserProfileId = tab.BrowserProfileId,
			BrowserProfileLabel = tab.BrowserProfileLabel,
			IsPinned = tab.IsPinned,
			IsCustom = true
		});
		int sourceIndex = AiTabs.Items.IndexOf(sourceTabItem);
		AddCustomTab(duplicate, sourceIndex < 0 ? null : sourceIndex + 1);
	}

	private void OpenWithNewProfile(AiTab tab, TabItem sourceTabItem)
	{
		string currentUrl = GetCurrentTabUrl(tab, sourceTabItem);
		if (!SettingsService.TryNormalizeUrl(currentUrl, out string normalizedUrl))
		{
			ShowSetupBanner("Could not open a separate profile", "The current page is not a normal HTTP or HTTPS address.");
			return;
		}
		BrowserProfileIdentity profile = BrowserProfilePolicy.CreateNew(GetTabsForProfileDiscovery());
		AiTab duplicate = PrepareNewCustomTab(new AiTab
		{
			Name = tab.Name,
			Url = normalizedUrl,
			Icon = tab.Icon,
			BrowserProfileId = profile.Id,
			BrowserProfileLabel = profile.Label,
			IsPinned = tab.IsPinned,
			IsCustom = true
		});
		int sourceIndex = AiTabs.Items.IndexOf(sourceTabItem);
		AddCustomTab(duplicate, sourceIndex < 0 ? null : sourceIndex + 1);
	}

	private void EditCustomTab(AiTab tab, TabItem tabItem, AddTabFocusTarget focusTarget)
	{
		if (!tab.IsCustom)
		{
			return;
		}
		string tabId = SettingsService.GetTabId(tab);
		AddTabWindow editWindow = new AddTabWindow
		{
			Owner = this
		};
		editWindow.ConfigureForEdit(tab, focusTarget);
		editWindow.ConfigureProfiles(GetTabsForProfileDiscovery(), tab.BrowserProfileId);
		if (editWindow.ShowDialog() != true || editWindow.CreatedTab == null)
		{
			return;
		}
		AiTab editedTab = new AiTab
		{
			Id = tabId,
			Name = editWindow.CreatedTab.Name,
			Url = editWindow.CreatedTab.Url,
			Icon = editWindow.CreatedTab.Icon,
			BrowserProfileId = editWindow.CreatedTab.BrowserProfileId,
			BrowserProfileLabel = editWindow.CreatedTab.BrowserProfileLabel,
			IsPinned = tab.IsPinned,
			IsCustom = true
		};
		int tabIndex = _tabs.FindIndex(existing =>
			existing.IsCustom && SettingsService.GetTabId(existing).Equals(tabId, StringComparison.Ordinal));
		if (tabIndex < 0)
		{
			return;
		}
		AiTab originalTab = _tabs[tabIndex];
		_tabs[tabIndex] = editedTab;
		try
		{
			_settingsService.SaveCustomTabs(_tabs);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			_tabs[tabIndex] = originalTab;
			ShowSettingsError(ex);
			return;
		}
		bool reloadContent = !originalTab.Url.Equals(editedTab.Url, StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(originalTab.BrowserProfileId, editedTab.BrowserProfileId, StringComparison.OrdinalIgnoreCase);
		UpdateTabItemForTab(tabItem, editedTab, reloadContent);
		SaveTabOrder();
	}

	private void TogglePin(AiTab tab, TabItem tabItem)
	{
		string tabId = SettingsService.GetTabId(tab);
		int tabIndex = _tabs.FindIndex(existing =>
			SettingsService.GetTabId(existing).Equals(tabId, StringComparison.Ordinal));
		if (tabIndex < 0)
		{
			return;
		}
		AiTab updatedTab = new AiTab
		{
			Id = tabId,
			Name = tab.Name,
			Url = tab.Url,
			Icon = tab.Icon,
			BrowserProfileId = tab.BrowserProfileId,
			BrowserProfileLabel = tab.BrowserProfileLabel,
			IsPinned = !tab.IsPinned,
			IsCustom = tab.IsCustom
		};
		AiTab originalTab = _tabs[tabIndex];
		_tabs[tabIndex] = updatedTab;
		try
		{
			if (updatedTab.IsCustom)
			{
				_settingsService.SaveCustomTabs(_tabs);
			}
			else
			{
				_settingsService.SaveTabPinState(tabId, updatedTab.IsPinned);
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			_tabs[tabIndex] = originalTab;
			ShowSettingsError(ex);
			return;
		}
		UpdateTabItemForTab(tabItem, updatedTab, reloadContent: false);
		if (updatedTab.IsPinned)
		{
			ApplyPinnedOrdering();
			AiTabs.SelectedItem = tabItem;
		}
		else
		{
			RemoveTabFromTabBar(tabItem);
		}
		SaveTabOrder();
	}

	private void ToggleNativePin(string tabId, TabItem tabItem)
	{
		bool isPinned = _settingsService.LoadTabPinned(tabId, IsNativePanelPinnedByDefault(tabId));
		bool nextPinned = !isPinned;
		try
		{
			_settingsService.SaveTabPinState(tabId, nextPinned);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSettingsError(ex);
			return;
		}
		if (nextPinned)
		{
			ApplyPinnedOrdering();
			AiTabs.SelectedItem = tabItem;
		}
		else
		{
			RemoveTabFromTabBar(tabItem);
		}
		SaveTabOrder();
	}

	private void RemoveCustomTab(AiTab tab, TabItem tabItem, MenuItem removeMenuItem)
	{
		if (removeMenuItem.Tag is not true)
		{
			removeMenuItem.Tag = true;
			removeMenuItem.Header = "Confirm remove \"" + tab.Name + "\"";
			removeMenuItem.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55));
			return;
		}
		tabItem.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
		ConfirmRemoveCustomTab(tab, tabItem);
	}

	private static void ResetRemoveMenuItem(MenuItem removeMenuItem)
	{
		removeMenuItem.Tag = null;
		removeMenuItem.Header = "Close tab";
		removeMenuItem.ClearValue(Control.ForegroundProperty);
	}

	private void ConfirmRemoveCustomTab(AiTab tab, TabItem tabItem)
	{
		string tabId = SettingsService.GetTabId(tab);
		int previousIndex = Math.Max(0, AiTabs.Items.IndexOf(tabItem));
		bool wasSelected = AiTabs.SelectedItem == tabItem;
		List<AiTab> tabs = _tabs.Where((AiTab existing) =>
			!SettingsService.GetTabId(existing).Equals(tabId, StringComparison.Ordinal)).ToList();
		try
		{
			_settingsService.SaveCustomTabs(tabs);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			ShowSettingsError(ex);
			return;
		}
		_closedTabs.Push(tab, previousIndex);
		SaveClosedTabHistory();
		if (tabItem.Content is BrowserTabView browserTabView)
		{
			DetachBrowserTabView(browserTabView);
			browserTabView.Dispose();
		}
		_tabs.RemoveAll((AiTab existing) => SettingsService.GetTabId(existing).Equals(tabId, StringComparison.Ordinal));
		AiTabs.Items.Remove(tabItem);
		if (wasSelected && AiTabs.Items.Count > 0)
		{
			AiTabs.SelectedIndex = Math.Min(previousIndex, AiTabs.Items.Count - 1);
		}
		SaveTabOrder();
	}

	private void RestoreClosedCustomTab()
	{
		if (!_closedTabs.TryPop(out ClosedTabEntry? entry) || entry == null)
		{
			return;
		}
		SaveClosedTabHistory();
		AiTab tab = entry.Tab;
		string tabId = SettingsService.GetTabId(tab);
		if (FindTabItem(tabId) != null)
		{
			tab = PrepareNewCustomTab(tab);
			tabId = SettingsService.GetTabId(tab);
		}
		try
		{
			_tabs.Add(tab);
			_settingsService.SaveCustomTabs(_tabs);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			_tabs.Remove(tab);
			_closedTabs.Push(entry);
			SaveClosedTabHistory();
			ShowSettingsError(ex);
			return;
		}
		TabItem tabItem = CreateTabItem(tab);
		int restoreIndex = Math.Clamp(entry.PreviousIndex, 0, AiTabs.Items.Count);
		AiTabs.Items.Insert(restoreIndex, tabItem);
		ApplyPinnedOrdering();
		AiTabs.SelectedItem = tabItem;
		SaveTabOrder();
		((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)new Action(tabItem.BringIntoView));
	}

	private TabItem? FindTabItem(string tabId)
	{
		return AiTabs.Items.OfType<TabItem>().FirstOrDefault((TabItem item) =>
			item.Tag is string tag && tag.Equals(tabId, StringComparison.Ordinal));
	}

	private bool CloseSelectedCustomTab()
	{
		if (AiTabs.SelectedItem is not TabItem tabItem || tabItem.Tag is not string tabId)
		{
			return true;
		}
		AiTab? tab = FindTab(tabId);
		if (tab == null || !tab.IsCustom)
		{
			return true;
		}
		ConfirmRemoveCustomTab(tab, tabItem);
		return true;
	}

	private void AiTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		_tabDragStart = e.GetPosition(AiTabs);
		TabControl aiTabs = AiTabs;
		object originalSource = e.OriginalSource;
		_draggedTab = ItemsControl.ContainerFromElement(aiTabs, (DependencyObject)((originalSource is DependencyObject) ? originalSource : null)) as TabItem;
	}

	private void AiTabs_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		if (e.LeftButton == MouseButtonState.Pressed && _draggedTab != null)
		{
			Point position = e.GetPosition(AiTabs);
			if (!(Math.Abs(position.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) || !(Math.Abs(position.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance))
			{
				TabItem draggedTab = _draggedTab;
				_draggedTab = null;
				DataObject data = new DataObject(TabDragFormat, draggedTab);
				DragDrop.DoDragDrop((DependencyObject)(object)draggedTab, data, DragDropEffects.Move);
			}
		}
	}

	private void AiTabs_DragOver(object sender, DragEventArgs e)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		int effects;
		if (e.Data.GetDataPresent(TabDragFormat))
		{
			Point position = e.GetPosition(AiTabs);
			if (position.Y <= GetTabChromeHeight())
			{
				effects = 2;
				goto IL_0036;
			}
		}
		effects = 0;
		goto IL_0036;
		IL_0036:
		e.Effects = (DragDropEffects)effects;
		e.Handled = true;
	}

	private void AiTabs_Drop(object sender, DragEventArgs e)
	{
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		if (!e.Data.GetDataPresent(TabDragFormat) || !(e.Data.GetData(TabDragFormat) is TabItem tabItem) || !AiTabs.Items.Contains(tabItem))
		{
			return;
		}
		Point position = e.GetPosition(AiTabs);
		if (position.Y > GetTabChromeHeight())
		{
			return;
		}
		TabControl aiTabs = AiTabs;
		object originalSource = e.OriginalSource;
		TabItem tabItem2 = ItemsControl.ContainerFromElement(aiTabs, (DependencyObject)((originalSource is DependencyObject) ? originalSource : null)) as TabItem;
		int num = AiTabs.Items.IndexOf(tabItem);
		int num2 = ((tabItem2 == null) ? AiTabs.Items.Count : AiTabs.Items.IndexOf(tabItem2));
		if (tabItem2 != null)
		{
			position = e.GetPosition(tabItem2);
			if (position.X > tabItem2.ActualWidth / 2.0)
			{
				num2++;
			}
		}
		if (num < num2)
		{
			num2--;
		}
		if (num != num2)
		{
			_isReorderingTabs = true;
			try
			{
				object selectedItem = AiTabs.SelectedItem;
				AiTabs.Items.RemoveAt(num);
				AiTabs.Items.Insert(Math.Clamp(num2, 0, AiTabs.Items.Count), tabItem);
				AiTabs.SelectedItem = selectedItem;
			}
			finally
			{
				_isReorderingTabs = false;
			}
			ApplyPinnedOrdering();
			SaveTabOrder();
			tabItem.BringIntoView();
			e.Handled = true;
		}
	}

	private void AiTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		Point position = e.GetPosition(AiTabs);
		if (!(position.Y > GetTabChromeHeight()) && AiTabs.Template.FindName("HeaderScrollViewer", AiTabs) is ScrollViewer scrollViewer && !(scrollViewer.ExtentWidth <= scrollViewer.ViewportWidth))
		{
			scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - (double)e.Delta * 0.35);
			UpdateTabStripFade();
			e.Handled = true;
		}
	}

	private void HeaderScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		UpdateTabStripFade();
	}

	private void AiTabs_LayoutUpdated(object? sender, EventArgs e)
	{
		UpdateTabChromeMetrics();
	}

	private void QueueTabStripFadeUpdate()
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)new Action(UpdateTabStripFade));
	}

	private void UpdateTabStripFade()
	{
		UpdateTabChromeMetrics();
		if (AiTabs.Template.FindName("HeaderScrollViewer", AiTabs) is not ScrollViewer scrollViewer ||
			AiTabs.Template.FindName("TabStripFade", AiTabs) is not FrameworkElement tabStripFade)
		{
			return;
		}
		bool hasHiddenTabs = scrollViewer.ExtentWidth > scrollViewer.ViewportWidth &&
			scrollViewer.HorizontalOffset + scrollViewer.ViewportWidth < scrollViewer.ExtentWidth - 0.5;
		tabStripFade.Visibility = hasHiddenTabs ? Visibility.Visible : Visibility.Collapsed;
	}

	private void UpdateTabChromeMetrics()
	{
		double measuredHeight = MeasureTabChromeHeight();
		double chromeHeight = BrowserViewportLayout.ResolveChromeHeight(measuredHeight);
		if (Math.Abs(measuredHeight - _measuredTabChromeHeight) < 0.1 &&
			_externalAppViews.Values.All(view => Math.Abs(view.Margin.Top - chromeHeight) < 0.1))
		{
			return;
		}
		_measuredTabChromeHeight = measuredHeight;
		foreach (ExternalAppTabView view in _externalAppViews.Values)
		{
			view.Margin = new Thickness(0.0, chromeHeight, 0.0, 0.0);
		}
	}

	private double MeasureTabChromeHeight()
	{
		if (AiTabs.Template.FindName("HeaderScrollViewer", AiTabs) is FrameworkElement header &&
			double.IsFinite(header.ActualHeight) &&
			header.ActualHeight > 0.0)
		{
			return header.ActualHeight;
		}
		return BrowserViewportLayout.FallbackChromeHeight;
	}

	private double GetTabChromeHeight()
	{
		return BrowserViewportLayout.ResolveChromeHeight(_measuredTabChromeHeight);
	}

	private void TabOverflowButton_Click(object sender, RoutedEventArgs e)
	{
		ContextMenu contextMenu = new ContextMenu
		{
			PlacementTarget = TabOverflowButton,
			Placement = PlacementMode.Custom,
			CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
			[
				new CustomPopupPlacement(
					new Point(targetSize.Width - popupSize.Width, targetSize.Height),
					PopupPrimaryAxis.Horizontal)
			]
		};
		AddDefaultPanelsToMenu(contextMenu);
		AddSavedCustomTabsToMenu(contextMenu);
		HashSet<string> quickAddIds = GetQuickAddListedTabIds();
		if (contextMenu.Items.Count > 0)
		{
			contextMenu.Items.Add(new Separator());
		}
		bool addedSeparator = false;
		foreach (TabItem tabItem in AiTabs.Items.OfType<TabItem>())
		{
			if (tabItem.Tag is string listedTag && quickAddIds.Contains(listedTag))
			{
				continue;
			}
			bool isSelected = AiTabs.SelectedItem == tabItem;
			if (isSelected && !addedSeparator && contextMenu.Items.Count > 0)
			{
				contextMenu.Items.Add(new Separator());
				addedSeparator = true;
			}
			MenuItem menuItem = new MenuItem
			{
				Header = GetTabDisplayName(tabItem),
				IsCheckable = true,
				IsChecked = isSelected,
				FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
				InputGestureText = isSelected ? "Selected" : null
			};
			menuItem.Click += delegate
			{
				AiTabs.SelectedItem = tabItem;
				tabItem.BringIntoView();
			};
			contextMenu.Items.Add(menuItem);
			if (isSelected && !addedSeparator)
			{
				contextMenu.Items.Add(new Separator());
				addedSeparator = true;
			}
		}
		contextMenu.IsOpen = true;
	}

	private void AddDefaultPanelsToMenu(ContextMenu contextMenu)
	{
		contextMenu.Items.Add(new MenuItem
		{
			Header = "Default panels",
			IsEnabled = false
		});
		foreach ((string id, string name) in GetDefaultPanelMenuItems())
		{
			bool isSettings = id.Equals(SettingsPanelId, StringComparison.Ordinal);
			TabItem? tabItem = isSettings ? null : FindTabItem(id);
			bool isOpen = isSettings ? _settingsWindow != null : tabItem != null;
			bool isSelected = tabItem != null && AiTabs.SelectedItem == tabItem;
			MenuItem menuItem = new MenuItem
			{
				Header = name,
				IsCheckable = true,
				IsChecked = isSelected || (isSettings && isOpen),
				FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
				InputGestureText = isSelected ? "Selected" : isOpen ? "Open" : "Add"
			};
			menuItem.Click += delegate
			{
				OpenOrFocusDefaultPanel(id);
			};
			contextMenu.Items.Add(menuItem);
		}
	}

	private void AddSavedCustomTabsToMenu(ContextMenu contextMenu)
	{
		List<AiTab> customTabs = _tabs.Where(tab => tab.IsCustom).ToList();
		if (customTabs.Count == 0)
		{
			return;
		}
		if (contextMenu.Items.Count > 0)
		{
			contextMenu.Items.Add(new Separator());
		}
		contextMenu.Items.Add(new MenuItem
		{
			Header = "Saved tabs",
			IsEnabled = false
		});
		foreach (AiTab tab in customTabs)
		{
			string tabId = SettingsService.GetTabId(tab);
			TabItem? tabItem = FindTabItem(tabId);
			bool isOpen = tabItem != null;
			bool isSelected = tabItem != null && AiTabs.SelectedItem == tabItem;
			MenuItem menuItem = new MenuItem
			{
				Header = tab.Name,
				IsCheckable = true,
				IsChecked = isSelected,
				FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
				InputGestureText = isSelected ? "Selected" : isOpen ? "Open" : "Add"
			};
			menuItem.Click += delegate
			{
				OpenOrFocusSavedTab(tabId);
			};
			contextMenu.Items.Add(menuItem);
		}
	}

	private HashSet<string> GetQuickAddListedTabIds()
	{
		HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
		foreach ((string id, string _) in GetDefaultPanelMenuItems())
		{
			ids.Add(id);
		}
		foreach (AiTab tab in _tabs.Where(tab => tab.IsCustom))
		{
			ids.Add(SettingsService.GetTabId(tab));
		}
		return ids;
	}

	private IEnumerable<(string id, string name)> GetDefaultPanelMenuItems()
	{
		foreach (ExternalAppDefinition definition in _externalAppDefinitions)
		{
			yield return (definition.Id, definition.DisplayName);
		}
		yield return (NativeCodexUsageTabId, "Codex Usage");
		yield return (NativeWindowsHelperTabId, "Windows Helper");
		yield return (NativeTripPlannerTabId, "Trip Planner");
		yield return (NativeTaskManagerTabId, "Task Manager");
		yield return (SettingsPanelId, "Settings");
		foreach (AiTab tab in _tabs.Where(tab => !tab.IsCustom))
		{
			yield return (SettingsService.GetTabId(tab), tab.Name);
		}
	}

	private void OpenOrFocusDefaultPanel(string id)
	{
		if (id.Equals(SettingsPanelId, StringComparison.Ordinal))
		{
			OpenSettings();
			return;
		}
		TabItem? existingTab = FindTabItem(id);
		if (existingTab != null)
		{
			AiTabs.SelectedItem = existingTab;
			existingTab.BringIntoView();
			return;
		}
		TabItem? tabItem = CreateRestorableDefaultPanel(id);
		if (tabItem == null)
		{
			return;
		}
		AiTabs.Items.Add(tabItem);
		ApplyPinnedOrdering();
		AiTabs.SelectedItem = tabItem;
		SaveTabOrder();
		tabItem.BringIntoView();
	}

	private void OpenOrFocusSavedTab(string id)
	{
		TabItem? existingTab = FindTabItem(id);
		if (existingTab != null)
		{
			AiTabs.SelectedItem = existingTab;
			existingTab.BringIntoView();
			return;
		}
		AiTab? tab = _tabs.FirstOrDefault(savedTab =>
			savedTab.IsCustom &&
			SettingsService.GetTabId(savedTab).Equals(id, StringComparison.Ordinal));
		if (tab == null)
		{
			return;
		}
		TabItem tabItem = CreateTabItem(tab);
		AiTabs.Items.Add(tabItem);
		ApplyPinnedOrdering();
		AiTabs.SelectedItem = tabItem;
		SaveTabOrder();
		tabItem.BringIntoView();
	}

	private TabItem? CreateRestorableDefaultPanel(string id)
	{
		ExternalAppDefinition? externalDefinition = _externalAppDefinitions.FirstOrDefault(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
		if (externalDefinition != null)
		{
			return CreateExternalAppTabItem(externalDefinition);
		}
		if (CanCloseNativePanel(id))
		{
			return CreateNativePanelTabItem(id);
		}
		AiTab? builtInTab = _tabs.FirstOrDefault(tab =>
			!tab.IsCustom && SettingsService.GetTabId(tab).Equals(id, StringComparison.Ordinal));
		return builtInTab == null ? null : CreateTabItem(builtInTab);
	}

	private void ShowSettingsError(Exception exception)
	{
		string message = exception is UnauthorizedAccessException
			? exception.Message + " If Bitdefender or Windows security just blocked Quick Panel, allow QuickPanel.exe and try again."
			: exception.Message;
		ShowSetupBanner("Could not save settings", message);
	}

	private void ShowStartupRegistrationError()
	{
		ShowSetupBanner(
			"Could not update startup setting",
			"Windows or endpoint security blocked the per-user Startup shortcut. Keep Quick Panel in a trusted portable folder, then toggle Start with Windows again.");
	}

	private void OpenUrlExternally(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			});
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			ShowSetupBanner("Could not open browser", ex.Message);
		}
	}

	private void CopyUrlToClipboard(string url)
	{
		try
		{
			Clipboard.SetText(url);
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.ExternalException)
		{
			ShowSetupBanner("Could not copy URL", ex.Message);
		}
	}

	private void ShowSetupBanner(string title, string message)
	{
		SetupWarningTitle.Text = title;
		SetupWarningMessage.Text = message;
		SetupWarningBanner.Visibility = Visibility.Visible;
	}

	private void SetupWarningBanner_CloseClick(object sender, RoutedEventArgs e)
	{
		SetupWarningBanner.Visibility = Visibility.Collapsed;
	}

	private static string GetTabDisplayName(TabItem tabItem)
	{
		return AutomationProperties.GetName(tabItem) ?? (tabItem.ToolTip as string) ?? "Tab";
	}

	private string? GetNativePanelDisplayName(string tabId)
	{
		if (_sessionExternalAppDefinitions.TryGetValue(tabId, out ExternalAppDefinition? sessionDefinition))
		{
			return sessionDefinition.DisplayName;
		}
		ExternalAppDefinition? externalDefinition = _externalAppDefinitions.FirstOrDefault(definition => definition.Id.Equals(tabId, StringComparison.OrdinalIgnoreCase));
		if (externalDefinition != null)
		{
			return externalDefinition.DisplayName;
		}
		return tabId switch
		{
			NativeCodexUsageTabId => "Codex Usage",
			NativeWindowsHelperTabId => "Windows Helper",
			NativeTripPlannerTabId => "Trip Planner",
			NativeTaskManagerTabId => "Task Manager",
			_ => null
		};
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		int virtualKey = GetVirtualKey(e);
		PanelShortcutModifiers modifiers = GetShortcutModifiers(Keyboard.Modifiers);
		if (e.Key == Key.Escape)
		{
			if (TryStopSelectedBrowserLoading())
			{
				e.Handled = true;
				return;
			}
			HidePanel();
			e.Handled = true;
			return;
		}
		if (PanelShortcutResolver.TryResolve(virtualKey, modifiers, out PanelShortcut shortcut) &&
			HandlePanelShortcut(shortcut))
		{
			e.Handled = true;
			return;
		}
		if (ShouldMoveTypingFocusToPage(virtualKey, modifiers))
		{
			FocusSelectedBrowserPage();
		}
	}

	private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
	{
		// WebView2 is hosted in a child HWND here, so PreviewKeyDown misses focused pages.
		if (handled || !base.IsVisible || !base.IsActive ||
			(msg.message != WmKeyDown && msg.message != WmSysKeyDown))
		{
			return;
		}
		int virtualKey = (int)msg.wParam;
		PanelShortcutModifiers modifiers = GetShortcutModifiers(Keyboard.Modifiers);
		if (virtualKey == VirtualKeyEscape && modifiers == PanelShortcutModifiers.None)
		{
			if (TryStopSelectedBrowserLoading())
			{
				handled = true;
				return;
			}
			HidePanel();
			handled = true;
			return;
		}
		if (PanelShortcutResolver.TryResolve(virtualKey, modifiers, out PanelShortcut shortcut) &&
			HandlePanelShortcut(shortcut))
		{
			handled = true;
		}
	}

	private bool HandlePanelShortcut(PanelShortcut shortcut)
	{
		switch (shortcut.Command)
		{
			case PanelShortcutCommand.AddTab:
				OpenAddTabDialog();
				return true;
			case PanelShortcutCommand.RestoreClosedTab:
				RestoreClosedCustomTab();
				return true;
			case PanelShortcutCommand.CloseCurrentCustomTab:
				return CloseSelectedCustomTab();
			case PanelShortcutCommand.SelectNextTab:
				CycleTab(1);
				return true;
			case PanelShortcutCommand.SelectPreviousTab:
				CycleTab(-1);
				return true;
			case PanelShortcutCommand.JumpToTab:
				JumpToTab(shortcut.Argument);
				return true;
			case PanelShortcutCommand.ZoomIn:
				return AdjustSelectedTabZoom(ZoomStep);
			case PanelShortcutCommand.ZoomOut:
				return AdjustSelectedTabZoom(-ZoomStep);
			case PanelShortcutCommand.ResetZoom:
				return ResetSelectedTabZoom();
			case PanelShortcutCommand.ReloadCurrentTab:
				return ReloadSelectedBrowserTab();
			default:
				return false;
		}
	}

	private static int GetVirtualKey(KeyEventArgs e)
	{
		Key key = e.Key == Key.System ? e.SystemKey : e.Key;
		if (key == Key.ImeProcessed)
		{
			key = e.ImeProcessedKey;
		}
		return KeyInterop.VirtualKeyFromKey(key);
	}

	private static PanelShortcutModifiers GetShortcutModifiers(ModifierKeys modifiers)
	{
		PanelShortcutModifiers shortcutModifiers = PanelShortcutModifiers.None;
		if ((modifiers & ModifierKeys.Control) != 0)
		{
			shortcutModifiers |= PanelShortcutModifiers.Control;
		}
		if ((modifiers & ModifierKeys.Shift) != 0)
		{
			shortcutModifiers |= PanelShortcutModifiers.Shift;
		}
		if ((modifiers & ModifierKeys.Alt) != 0)
		{
			shortcutModifiers |= PanelShortcutModifiers.Alt;
		}
		return shortcutModifiers;
	}

	private bool ShouldMoveTypingFocusToPage(int virtualKey, PanelShortcutModifiers modifiers)
	{
		bool hasBrowserTab = TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out _) && browserTabView != null;
		return FocusPolicy.ShouldFocusPageForTyping(
			isAppEditingControlFocused: IsAppEditingControlFocused(),
			isSelectedBrowserTab: hasBrowserTab,
			isBrowserAlreadyFocused: browserTabView?.IsKeyboardFocusWithin == true,
			virtualKey,
			modifiers);
	}

	private void FocusSelectedBrowserPage()
	{
		if (TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out _) && browserTabView != null)
		{
			browserTabView.FocusPage();
		}
	}

	private bool ReloadSelectedBrowserTab()
	{
		if (TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out _) && browserTabView != null)
		{
			browserTabView.Reload();
		}
		return true;
	}

	private bool TryStopSelectedBrowserLoading()
	{
		return TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out _) &&
			browserTabView != null &&
			browserTabView.StopLoading();
	}

	private static bool IsAppEditingControlFocused()
	{
		if (Keyboard.FocusedElement is not DependencyObject focusedElement)
		{
			return false;
		}
		return HasAncestorOrSelf<TextBoxBase>(focusedElement) ||
			HasAncestorOrSelf<PasswordBox>(focusedElement) ||
			HasAncestorOrSelf<ComboBox>(focusedElement);
	}

	private void CycleTab(int direction)
	{
		int count = AiTabs.Items.Count;
		if (count == 0)
		{
			return;
		}
		int currentIndex = Math.Max(0, AiTabs.SelectedIndex);
		AiTabs.SelectedIndex = (currentIndex + direction + count) % count;
	}

	private void JumpToTab(int index)
	{
		if (index >= 0 && index < AiTabs.Items.Count)
		{
			AiTabs.SelectedIndex = index;
		}
	}

	private bool AdjustSelectedTabZoom(double delta)
	{
		if (!TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out string? tabId) ||
			browserTabView == null ||
			tabId == null)
		{
			return true;
		}
		double nextZoom = Math.Clamp(Math.Round(browserTabView.ZoomFactor + delta, 2), MinZoomFactor, MaxZoomFactor);
		return SetBrowserZoom(tabId, browserTabView, nextZoom);
	}

	private bool ResetSelectedTabZoom()
	{
		if (!TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out string? tabId) ||
			browserTabView == null ||
			tabId == null)
		{
			return true;
		}
		return SetBrowserZoom(tabId, browserTabView, 1.0);
	}

	private void ResetTabZoom(AiTab tab, TabItem tabItem)
	{
		string tabId = SettingsService.GetTabId(tab);
		if (tabItem.Content is BrowserTabView browserTabView)
		{
			SetBrowserZoom(tabId, browserTabView, 1.0);
			return;
		}
		SetStoredZoom(tabId, tabItem, 1.0);
	}

	private bool SetBrowserZoom(string tabId, BrowserTabView browserTabView, double zoomFactor)
	{
		if (!SetStoredZoom(tabId, FindTabItem(tabId), zoomFactor))
		{
			return false;
		}
		browserTabView.ZoomFactor = Math.Clamp(zoomFactor, MinZoomFactor, MaxZoomFactor);
		return true;
	}

	private bool SetStoredZoom(string tabId, TabItem? tabItem, double zoomFactor)
	{
		double safeZoomFactor = Math.Clamp(Math.Round(zoomFactor, 2), MinZoomFactor, MaxZoomFactor);
		try
		{
			_settingsService.SaveTabZoomFactor(tabId, safeZoomFactor);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			ShowSettingsError(ex);
			return false;
		}
		if (Math.Abs(safeZoomFactor - 1.0) < 0.001)
		{
			_tabZoomFactors.Remove(tabId);
		}
		else
		{
			_tabZoomFactors[tabId] = safeZoomFactor;
		}
		if (tabItem != null && FindTab(tabId) is AiTab tab)
		{
			tabItem.ToolTip = BuildTabToolTip(tab, GetTabZoomFactor(tabId));
		}
		return true;
	}

	private bool TryGetSelectedBrowserView(out BrowserTabView? browserTabView, out string? tabId)
	{
		if (AiTabs.SelectedItem is TabItem { Content: BrowserTabView selectedBrowserTabView, Tag: string selectedTabId })
		{
			browserTabView = selectedBrowserTabView;
			tabId = selectedTabId;
			return true;
		}
		browserTabView = null;
		tabId = null;
		return false;
	}

	private AiTab? FindTab(string tabId)
	{
		return _tabs.FirstOrDefault((AiTab tab) => SettingsService.GetTabId(tab).Equals(tabId, StringComparison.Ordinal));
	}

	private double GetTabZoomFactor(string tabId)
	{
		return _tabZoomFactors.TryGetValue(tabId, out double zoomFactor) ? zoomFactor : 1.0;
	}

	private static string BuildTabToolTip(AiTab tab, double zoomFactor)
	{
		return tab.Name + Environment.NewLine +
			tab.Url + Environment.NewLine +
			"Profile: " + BrowserProfilePolicy.GetDisplayLabel(tab) + Environment.NewLine +
			"Zoom " + FormatZoomPercent(zoomFactor);
	}

	private IEnumerable<AiTab> GetTabsForProfileDiscovery()
	{
		return _tabs.Concat(_closedTabs.Entries.Select(entry => entry.Tab));
	}

	private FrameworkElement CreateWebTabHeader(AiTab tab)
	{
		Image icon = _tabIconService.CreateImage(tab);
		if (BrowserProfilePolicy.NormalizeProfileId(tab.BrowserProfileId) is null)
		{
			return icon;
		}

		var header = new Grid { Width = 22, Height = 22 };
		icon.HorizontalAlignment = HorizontalAlignment.Left;
		icon.VerticalAlignment = VerticalAlignment.Top;
		header.Children.Add(icon);
		var badge = new Border
		{
			Width = 10,
			Height = 10,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = (Brush)FindResource("PanelAccentBrush"),
			BorderBrush = (Brush)FindResource("PanelChromeBrush"),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(5),
			Child = new TextBlock
			{
				Text = "\uE77B",
				FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
				FontSize = 6,
				Foreground = Brushes.White,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		header.Children.Add(badge);
		return header;
	}

	private static string FormatZoomPercent(double zoomFactor)
	{
		return Math.Round(zoomFactor * 100.0).ToString("0") + "%";
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState == MouseButtonState.Pressed &&
			(e.OriginalSource is not DependencyObject source || !HasButtonAncestor(source)))
		{
			DragMove();
		}
	}

	private static bool HasButtonAncestor(DependencyObject source)
	{
		return HasAncestorOrSelf<ButtonBase>(source);
	}

	private static bool HasAncestorOrSelf<T>(DependencyObject source)
		where T : DependencyObject
	{
		DependencyObject? current = source;
		while (current != null)
		{
			if (current is T)
			{
				return true;
			}
			current = current is Visual
				? VisualTreeHelper.GetParent(current)
				: LogicalTreeHelper.GetParent(current);
		}
		return false;
	}

	private void HideButton_Click(object sender, RoutedEventArgs e)
	{
		HidePanel();
	}

	private void HidePanel()
	{
		_settingsWindow?.Close();
		foreach (ExternalAppTabView view in _externalAppViews.Values)
		{
			view.SetPanelVisible(false);
		}
		Hide();
	}

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		_ = sender;
		_ = e;
		// Overlay windows must be restored while the Quick Panel HWND is still
		// valid. Waiting for Closed lets Windows destroy their temporary owner
		// first, which can leave an inactive application minimized.
		DisposeExternalAppViews();
	}

	private void Window_Closed(object? sender, EventArgs e)
	{
		_panelSizeSaveTimer.Stop();
		SavePanelSize();
		_allowPanelSizePersistence = false;
		_panelSizeSaveTimer.Tick -= PanelSizeSaveTimer_Tick;
		ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;
		AiTabs.LayoutUpdated -= AiTabs_LayoutUpdated;
		_settingsWindow?.Close();
		_hotkeyService.Pressed -= ToggleWindow;
		_hotkeyService.Dispose();
		_trayIconService?.Dispose();
		foreach (BrowserTabView browserTabView in (from item in AiTabs.Items.OfType<TabItem>()
			select item.Content).OfType<BrowserTabView>())
		{
			DetachBrowserTabView(browserTabView);
			browserTabView.Dispose();
		}
		DisposeExternalAppViews();
	}

	private void DisposeExternalAppViews()
	{
		foreach (ExternalAppTabView view in _externalAppViews.Values)
		{
			view.Dispose();
		}
	}

}
