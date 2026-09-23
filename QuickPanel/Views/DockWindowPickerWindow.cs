using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using QuickPanel.Models;
using QuickPanel.Services;
using Microsoft.Win32;

namespace QuickPanel.Views;

public sealed class DockWindowPickerItem
{
	public required ExternalAppWindowCandidate Candidate { get; init; }
	public required WindowDockAssessment Assessment { get; init; }
	public required FrameworkElement Icon { get; init; }

	public string Title => string.IsNullOrWhiteSpace(Candidate.Title) ? Candidate.ProcessName : Candidate.Title;

	public string AppDetails => $"{Candidate.ProcessName}  ·  {Candidate.Width}×{Candidate.Height}  ·  {Candidate.ClassName}";

	public string CompatibilityLabel => Assessment.Label;

	public string CompatibilityReason => Assessment.Reason;

	public WindowDockCompatibilityLevel CompatibilityLevel => Assessment.Level;

	public bool Matches(string query)
	{
		return string.IsNullOrWhiteSpace(query) ||
			Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
			Candidate.ProcessName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
			Candidate.ClassName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
	}
}

public sealed class InstalledAppPickerItem
{
	public required ExternalAppInstalledApp App { get; init; }
	public required FrameworkElement Icon { get; init; }

	public string Name => App.Name;

	public string AppDetails => string.IsNullOrWhiteSpace(App.PackageFamilyName)
		? "Desktop app  ·  Windows Start"
		: "Packaged app  ·  Windows Start";

	public bool Matches(string query)
	{
		return string.IsNullOrWhiteSpace(query) ||
			Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
			App.AppUserModelId.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
			App.PackageFamilyName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
	}
}

public partial class DockWindowPickerWindow : Window, IComponentConnector
{
	private enum PickerMode
	{
		Windows,
		Apps
	}

	private readonly ExternalAppWindowCatalog _catalog = new();
	private readonly ExternalAppIconService _iconService = new();
	private readonly ExternalAppLauncher _launcher = new();
	private readonly ExternalAppWindowFinder _windowFinder = new();
	private readonly ObservableCollection<DockWindowPickerItem> _items = [];
	private readonly ObservableCollection<InstalledAppPickerItem> _apps = [];
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private ICollectionView? _itemsView;
	private ICollectionView? _appsView;
	private PickerMode _mode;
	private bool _appsLoaded;
	private bool _loading;
	private string? _statusMessage;

	public DockWindowPickerWindow()
	{
		InitializeComponent();
		WindowList.ItemsSource = _items;
		AppList.ItemsSource = _apps;
		_itemsView = CollectionViewSource.GetDefaultView(_items);
		_itemsView.Filter = item => item is DockWindowPickerItem pickerItem && pickerItem.Matches(SearchTextBox.Text.Trim());
		_appsView = CollectionViewSource.GetDefaultView(_apps);
		_appsView.Filter = item => item is InstalledAppPickerItem pickerItem && pickerItem.Matches(SearchTextBox.Text.Trim());
	}

	public ExternalAppWindowCandidate? SelectedWindow { get; private set; }

	private async void Window_Loaded(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		WindowAppearanceService.Apply(this);
		SearchTextBox.Focus();
		await RefreshWindowsAsync();
	}

	private void SetMode(PickerMode mode, bool clearSearch = true)
	{
		_mode = mode;
		_statusMessage = null;
		if (clearSearch) SearchTextBox.Clear();
		_itemsView?.Refresh();
		_appsView?.Refresh();
		UpdateModeUi();
		SearchTextBox.Focus();
	}

	private void UpdateModeUi()
	{
		bool windowsMode = _mode == PickerMode.Windows;
		bool hasWindows = _itemsView?.Cast<object>().Any() == true;
		bool hasApps = _appsView?.Cast<object>().Any() == true;
		bool hasItems = windowsMode ? hasWindows : hasApps;
		WindowList.Visibility = !_loading && windowsMode && hasItems ? Visibility.Visible : Visibility.Collapsed;
		AppList.Visibility = !_loading && !windowsMode && hasItems ? Visibility.Visible : Visibility.Collapsed;
		EmptyState.Visibility = !_loading && !hasItems ? Visibility.Visible : Visibility.Collapsed;
		LoadingState.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
		ModeDescriptionText.Text = windowsMode
			? "Choose a window below. Quick Panel restores its normal frame and position when you undock or close the tab."
			: "Installed apps stay off until you choose one. Open an app, then dock its window.";
		string search = SearchTextBox.Text.Trim();
		EmptyStateMessage.Text = _statusMessage ?? (windowsMode
			? string.IsNullOrWhiteSpace(search)
				? "Open an app, photo, PDF, or video, then choose Refresh."
				: "No open window matches this search."
			: string.IsNullOrWhiteSpace(search)
				? "No launchable Windows Start apps found."
				: "No installed app matches this search.");
		WindowsModeButton.Background = (Brush)FindResource(windowsMode ? "PanelAccentTintBrush" : "PanelSurfaceBrush");
		WindowsModeButton.BorderBrush = (Brush)FindResource(windowsMode ? "PanelAccentBrush" : "PanelBorderBrush");
		AppsModeButton.Background = (Brush)FindResource(!windowsMode ? "PanelAccentTintBrush" : "PanelSurfaceBrush");
		AppsModeButton.BorderBrush = (Brush)FindResource(!windowsMode ? "PanelAccentBrush" : "PanelBorderBrush");
		WindowsModeButton.Content = $"Open windows ({_items.Count})";
		AppsModeButton.Content = _appsLoaded ? $"Installed apps ({_apps.Count})" : "Installed apps";
		SearchTextBox.ToolTip = windowsMode ? "Search by window title, app, or window type" : "Search installed apps";
		DockButton.Content = windowsMode ? "Dock window" : "Open app";
		DockButton.IsEnabled = !_loading && (windowsMode ? WindowList.SelectedItem != null : AppList.SelectedItem != null);
		RefreshButton.IsEnabled = !_loading;
		OpenFileButton.IsEnabled = !_loading;
		WindowsModeButton.IsEnabled = !_loading;
		AppsModeButton.IsEnabled = !_loading;
	}

	private void SetLoading(bool loading, string message)
	{
		_loading = loading;
		LoadingMessageText.Text = message;
		UpdateModeUi();
	}

	private async Task RefreshWindowsAsync()
	{
		if (_loading) return;
		_statusMessage = null;
		nint selectedHandle = (WindowList.SelectedItem as DockWindowPickerItem)?.Candidate.Handle ?? nint.Zero;
		SetLoading(true, "Looking for open windows…");
		try
		{
			IReadOnlyList<DockableWindowEntry> windows = await Task.Run(
				_catalog.GetDockableWindows,
				_lifetimeCancellation.Token);
			_lifetimeCancellation.Token.ThrowIfCancellationRequested();
			_items.Clear();
			foreach (DockableWindowEntry window in windows)
			{
				_items.Add(new DockWindowPickerItem
				{
					Candidate = window.Candidate,
					Assessment = window.Assessment,
					Icon = _iconService.CreateImage(window.Candidate, 24)
				});
			}
			_itemsView?.Refresh();
			DockWindowPickerItem? previous = _items.FirstOrDefault(item => item.Candidate.Handle == selectedHandle);
			if (previous != null) WindowList.SelectedItem = previous;
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
		{
			_items.Clear();
			_statusMessage = "Quick Panel could not list windows: " + exception.Message;
		}
		finally
		{
			SetLoading(false, "Looking for open windows…");
		}
	}

	private async Task RefreshInstalledAppsAsync(bool force = false)
	{
		if (_loading) return;
		if (_appsLoaded && !force)
		{
			UpdateModeUi();
			return;
		}
		_statusMessage = null;
		string selectedId = (AppList.SelectedItem as InstalledAppPickerItem)?.App.AppUserModelId ?? string.Empty;
		SetLoading(true, "Reading installed apps and icons…");
		try
		{
			IReadOnlyList<ExternalAppInstalledApp> apps = await Task.Run(
				_launcher.EnumerateInstalledApps,
				_lifetimeCancellation.Token);
			_lifetimeCancellation.Token.ThrowIfCancellationRequested();
			_apps.Clear();
			foreach (ExternalAppInstalledApp app in apps)
			{
				_apps.Add(new InstalledAppPickerItem
				{
					App = app,
					Icon = _iconService.CreateImage(app, 24)
				});
			}
			_appsLoaded = true;
			_appsView?.Refresh();
			InstalledAppPickerItem? previous = _apps.FirstOrDefault(item => item.App.AppUserModelId.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
			if (previous != null) AppList.SelectedItem = previous;
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
		{
			_apps.Clear();
			_statusMessage = "Quick Panel could not list installed apps: " + exception.Message;
		}
		finally
		{
			SetLoading(false, "Reading installed apps and icons…");
		}
	}

	private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		_itemsView?.Refresh();
		_appsView?.Refresh();
		_statusMessage = null;
		if (!_loading) UpdateModeUi();
	}

	private void WindowsModeButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		SetMode(PickerMode.Windows);
	}

	private async void AppsModeButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		SetMode(PickerMode.Apps);
		await RefreshInstalledAppsAsync();
	}

	private async void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		if (_mode == PickerMode.Windows) await RefreshWindowsAsync();
		else await RefreshInstalledAppsAsync(force: true);
	}

	private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		OpenFileDialog dialog = new()
		{
			Title = "Open an app or file, then dock its window",
			Filter = "Apps and common files (*.exe;*.lnk;*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mkv;*.mov;*.avi)|*.exe;*.lnk;*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mkv;*.mov;*.avi|All files (*.*)|*.*",
			CheckFileExists = true,
			Multiselect = false
		};
		if (dialog.ShowDialog(this) != true) return;
		try
		{
			SetMode(PickerMode.Windows);
			Process.Start(new ProcessStartInfo
			{
				FileName = dialog.FileName,
				UseShellExecute = true
			});
			await Task.Delay(1200, _lifetimeCancellation.Token);
			await RefreshWindowsAsync();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
		{
			_statusMessage = "Windows could not open that item: " + exception.Message;
			UpdateModeUi();
		}
	}

	private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		UpdateModeUi();
	}

	private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		UpdateModeUi();
	}

	private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		_ = sender;
		_ = e;
		ConfirmSelection();
	}

	private async void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		_ = sender;
		_ = e;
		await OpenSelectedInstalledAppAsync();
	}

	private async void DockButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		if (_mode == PickerMode.Windows) ConfirmSelection();
		else await OpenSelectedInstalledAppAsync();
	}

	private async Task OpenSelectedInstalledAppAsync()
	{
		if (_loading || AppList.SelectedItem is not InstalledAppPickerItem selected) return;
		HashSet<nint> baseline = _windowFinder.CaptureTopLevelHandles();
		ExternalAppDefinition definition = ExternalAppDefinitionFactory.CreateForInstalledApp(selected.App);
		ExternalAppWindowCandidate? candidate = null;
		string? error = null;
		SetLoading(true, "Opening " + selected.Name + "…");
		try
		{
			ExternalAppLaunchResult result = await Task.Run(
				() => _launcher.LaunchInstalledApp(selected.App),
				_lifetimeCancellation.Token);
			if (!result.Success)
			{
				error = result.Error ?? "Windows could not open this app.";
			}
			else
			{
				nint stableHandle = nint.Zero;
				int stableScans = 0;
				long deadline = Environment.TickCount64 + 15_000;
				while (Environment.TickCount64 < deadline)
				{
					await Task.Delay(300, _lifetimeCancellation.Token);
					ExternalAppWindowMatch? match = await Task.Run(
						() => _windowFinder.FindBest(definition, baseline, result.Identity),
						_lifetimeCancellation.Token);
					ExternalAppWindowCandidate? current = match is { IsValid: true } &&
						ExternalAppWindowPickerPolicy.Evaluate(match.Candidate, Environment.ProcessId).IsDockable
						? match.Candidate
						: null;
					if (current == null)
					{
						IReadOnlyList<DockableWindowEntry> windows = await Task.Run(
							_catalog.GetDockableWindows,
							_lifetimeCancellation.Token);
						current = windows
							.Where(window => !baseline.Contains(window.Candidate.Handle) || window.Candidate.IsForeground)
							.OrderByDescending(window => window.Candidate.IsForeground)
							.ThenByDescending(window => (long)window.Candidate.Width * window.Candidate.Height)
							.Select(window => window.Candidate)
							.FirstOrDefault();
					}
					if (current == null)
					{
						stableHandle = nint.Zero;
						stableScans = 0;
						continue;
					}
					if (current.Handle == stableHandle) stableScans++;
					else
					{
						stableHandle = current.Handle;
						stableScans = 1;
					}
					if (stableScans >= 3)
					{
						candidate = current;
						break;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		finally
		{
			SetLoading(false, "Opening app…");
		}

		SetMode(PickerMode.Windows);
		await RefreshWindowsAsync();
		if (candidate != null)
		{
			DockWindowPickerItem? item = _items.FirstOrDefault(value => value.Candidate.Handle == candidate.Handle);
			if (item != null)
			{
				WindowList.SelectedItem = item;
				WindowList.ScrollIntoView(item);
				return;
			}
		}
		_statusMessage = error == null
			? selected.Name + " opened. Pick its window from the list, then choose Dock window."
			: "Windows could not open " + selected.Name + ": " + error;
		UpdateModeUi();
	}

	private void ConfirmSelection()
	{
		if (WindowList.SelectedItem is not DockWindowPickerItem item) return;
		if (!ExternalAppNativeMethods.IsValidWindow(item.Candidate.Handle))
		{
			_statusMessage = "That window just closed. Choose Refresh and pick another window.";
			UpdateModeUi();
			return;
		}
		SelectedWindow = item.Candidate;
		DialogResult = true;
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		DialogResult = false;
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		_ = sender;
		if (e.Key == Key.Escape)
		{
			DialogResult = false;
			e.Handled = true;
		}
		else if (e.Key == Key.F5)
		{
			if (_mode == PickerMode.Windows) _ = RefreshWindowsAsync();
			else _ = RefreshInstalledAppsAsync(force: true);
			e.Handled = true;
		}
		else if (e.Key == Key.Enter && !SearchTextBox.IsKeyboardFocused)
		{
			if (_mode == PickerMode.Windows && WindowList.SelectedItem != null) ConfirmSelection();
			else if (_mode == PickerMode.Apps && AppList.SelectedItem != null) _ = OpenSelectedInstalledAppAsync();
			e.Handled = true;
		}
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_ = sender;
		if (e.ButtonState == MouseButtonState.Pressed) DragMove();
	}

	private void Window_Closed(object? sender, EventArgs e)
	{
		_ = sender;
		_ = e;
		_lifetimeCancellation.Cancel();
	}
}
