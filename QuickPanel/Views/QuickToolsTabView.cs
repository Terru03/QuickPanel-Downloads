using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPanel.Models;
using QuickPanel.Services;

namespace QuickPanel.Views;

public enum QuickToolsPanelMode
{
	CodexUsage,
	WindowsHelper,
	TripPlanner
}

public partial class QuickToolsTabView : UserControl, IComponentConnector
{
	private readonly SettingsService _settingsService;

	private readonly CodexUsageService _codexUsageService;

	private readonly QuickToolsPanelMode _mode;

	private readonly DispatcherTimer _codexUsageRefreshTimer;

	private TripModeSettings _tripSettings = new TripModeSettings();

	private CancellationTokenSource? _codexUsageRefreshCancellation;

	private DateTimeOffset? _lastCodexUsageRefresh;

	private bool _isRefreshingCodexUsage;

	private bool _hasCodexUsageSnapshot;

	private bool _loadingTrip;

	public QuickToolsTabView()
		: this(new SettingsService(), new CodexUsageService(), QuickToolsPanelMode.CodexUsage)
	{
	}

	public QuickToolsTabView(SettingsService settingsService)
		: this(settingsService, QuickToolsPanelMode.CodexUsage)
	{
	}

	public QuickToolsTabView(SettingsService settingsService, QuickToolsPanelMode mode)
		: this(settingsService, new CodexUsageService(), mode)
	{
	}

	public QuickToolsTabView(SettingsService settingsService, CodexUsageService codexUsageService)
		: this(settingsService, codexUsageService, QuickToolsPanelMode.CodexUsage)
	{
	}

	public QuickToolsTabView(SettingsService settingsService, CodexUsageService codexUsageService, QuickToolsPanelMode mode)
	{
		_settingsService = settingsService;
		_codexUsageService = codexUsageService;
		_mode = mode;
		_codexUsageRefreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMinutes(5)
		};
		_codexUsageRefreshTimer.Tick += async delegate
		{
			await RefreshCodexUsageAsync();
		};
		InitializeComponent();
		ApplyPanelMode();
		if (_mode == QuickToolsPanelMode.TripPlanner)
		{
			LoadTripMode();
		}
		Loaded += QuickToolsTabView_Loaded;
		Unloaded += QuickToolsTabView_Unloaded;
	}

	private void ApplyPanelMode()
	{
		CodexUsageSection.Visibility = _mode == QuickToolsPanelMode.CodexUsage ? Visibility.Visible : Visibility.Collapsed;
		WindowsHelperSection.Visibility = _mode == QuickToolsPanelMode.WindowsHelper ? Visibility.Visible : Visibility.Collapsed;
		TripPlannerSection.Visibility = _mode == QuickToolsPanelMode.TripPlanner ? Visibility.Visible : Visibility.Collapsed;
		switch (_mode)
		{
			case QuickToolsPanelMode.CodexUsage:
				PanelTitleText.Text = "Codex Usage";
				PanelSubtitleText.Text = "Read-only usage view with API bars and 5-minute refresh.";
				UsageSnapshotBorder.Visibility = Visibility.Collapsed;
				SetCodexUsagePendingState("Refresh runs when this panel opens.");
				break;
			case QuickToolsPanelMode.WindowsHelper:
				PanelTitleText.Text = "Windows Helper";
				PanelSubtitleText.Text = "Safe local actions with confirmations for risky steps.";
				break;
			case QuickToolsPanelMode.TripPlanner:
				PanelTitleText.Text = "Trip Planner";
				PanelSubtitleText.Text = "Local packing, maps, camping, parking, budget, and offline notes.";
				break;
		}
	}

	private async void QuickToolsTabView_Loaded(object sender, RoutedEventArgs e)
	{
		if (_mode != QuickToolsPanelMode.CodexUsage)
		{
			return;
		}
		_codexUsageRefreshTimer.Start();
		if (_lastCodexUsageRefresh == null || DateTimeOffset.Now - _lastCodexUsageRefresh.Value >= TimeSpan.FromMinutes(5))
		{
			await RefreshCodexUsageAsync();
		}
	}

	private void QuickToolsTabView_Unloaded(object sender, RoutedEventArgs e)
	{
		if (_mode != QuickToolsPanelMode.CodexUsage)
		{
			return;
		}
		_codexUsageRefreshTimer.Stop();
		_codexUsageRefreshCancellation?.Cancel();
	}

	private void LoadTripMode()
	{
		_tripSettings = _settingsService.LoadTripModeSettings();
		if (_tripSettings.Trips.Count == 0)
		{
			_tripSettings = new TripModeSettings
			{
				SelectedTripId = "default",
				Trips = new List<TripPlan>
				{
					new TripPlan
					{
						Id = "default",
						Name = "Weekend trip"
					}
				}
			};
		}
		RefreshTripSelector(_tripSettings.SelectedTripId);
	}

	private void RefreshTripSelector(string? selectedTripId)
	{
		_loadingTrip = true;
		try
		{
			TripSelector.ItemsSource = null;
			TripSelector.ItemsSource = _tripSettings.Trips;
			TripSelector.SelectedValue = selectedTripId ?? _tripSettings.Trips.FirstOrDefault()?.Id;
		}
		finally
		{
			_loadingTrip = false;
		}
		LoadSelectedTripIntoForm();
	}

	private void LoadSelectedTripIntoForm()
	{
		TripPlan? trip = GetSelectedTrip();
		if (trip == null)
		{
			TripNameTextBox.Text = string.Empty;
			PackingItemsPanel.Children.Clear();
			SavedMapsTextBox.Text = string.Empty;
			CampingLinksTextBox.Text = string.Empty;
			ParkingSpotsTextBox.Text = string.Empty;
			FuelEstimateTextBox.Text = string.Empty;
			DailyBudgetTextBox.Text = string.Empty;
			EmergencyDocsTextBox.Text = string.Empty;
			OfflineNotesTextBox.Text = string.Empty;
			return;
		}

		TripNameTextBox.Text = trip.Name;
		PackingItemsPanel.Children.Clear();
		foreach (TripChecklistItem item in trip.PackingItems)
		{
			AddPackingCheckBox(item.Text, item.IsPacked);
		}
		SavedMapsTextBox.Text = trip.SavedMapsLinks ?? string.Empty;
		CampingLinksTextBox.Text = trip.CampingLinks ?? string.Empty;
		ParkingSpotsTextBox.Text = trip.ParkingSpots ?? string.Empty;
		FuelEstimateTextBox.Text = trip.FuelEstimate ?? string.Empty;
		DailyBudgetTextBox.Text = trip.DailyBudget ?? string.Empty;
		EmergencyDocsTextBox.Text = trip.EmergencyDocs ?? string.Empty;
		OfflineNotesTextBox.Text = trip.OfflineNotes ?? string.Empty;
	}

	private async void RefreshCodexUsageButton_Click(object sender, RoutedEventArgs e)
	{
		await RefreshCodexUsageAsync(force: true);
	}

	private async Task RefreshCodexUsageAsync(bool force = false)
	{
		if (_isRefreshingCodexUsage && !force)
		{
			return;
		}
		if (_isRefreshingCodexUsage)
		{
			_codexUsageRefreshCancellation?.Cancel();
		}
		_codexUsageRefreshCancellation = new CancellationTokenSource();
		CancellationTokenSource refreshCancellation = _codexUsageRefreshCancellation;
		CancellationToken cancellationToken = refreshCancellation.Token;
		_isRefreshingCodexUsage = true;
		RefreshCodexUsageButton.IsEnabled = false;
		UsageSnapshotBorder.Visibility = Visibility.Collapsed;
		SetRefreshAnimation(true);
		if (_hasCodexUsageSnapshot)
		{
			ResetActionStatusText.Text = "Refreshing Codex usage...";
		}
		else
		{
			SetCodexUsagePendingState("Refreshing Codex usage...");
		}
		try
		{
			CodexUsageSnapshot snapshot = await _codexUsageService.RefreshAsync(cancellationToken);
			UsageSnapshotBorder.Visibility = Visibility.Collapsed;
			ApplyCodexUsageSnapshot(snapshot);
			_hasCodexUsageSnapshot = true;
			_lastCodexUsageRefresh = DateTimeOffset.Now;
			string resetApiStatus = snapshot.ResetCreditsApiSucceeded
				? "OK"
				: snapshot.ResetCreditsApiStatus;
			ResetActionStatusText.Text = "Usage API: OK | Reset credits API: " + resetApiStatus +
				" | Last refresh: " + _lastCodexUsageRefresh.Value.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
		}
		catch (CodexUsageException ex)
		{
			UsageSnapshotBorder.Visibility = Visibility.Collapsed;
			if (_hasCodexUsageSnapshot)
			{
				ResetActionStatusText.Text = "Could not refresh Codex usage: " + ex.Message;
			}
			else
			{
				SetCodexUsageErrorState(ex.Message);
			}
		}
		catch (OperationCanceledException)
		{
			if (ReferenceEquals(_codexUsageRefreshCancellation, refreshCancellation))
			{
				ResetActionStatusText.Text = "Codex usage refresh was canceled.";
			}
		}
		finally
		{
			if (ReferenceEquals(_codexUsageRefreshCancellation, refreshCancellation))
			{
				_isRefreshingCodexUsage = false;
				RefreshCodexUsageButton.IsEnabled = true;
				SetRefreshAnimation(false);
			}
		}
	}

	private void SetRefreshAnimation(bool isRefreshing)
	{
		RefreshActivityBar.Visibility = isRefreshing ? Visibility.Visible : Visibility.Collapsed;
		RefreshCodexUsageButton.Content = isRefreshing ? "Refreshing..." : "Refresh Codex Usage";
	}

	private void SetCodexUsagePendingState(string message)
	{
		UsageWindowsGrid.Columns = 2;
		PrimaryWindowBorder.Margin = new Thickness(0, 0, 6, 0);
		SecondaryWindowBorder.Visibility = Visibility.Visible;
		WindowTitleText.Text = "Rate-limit window";
		SecondaryWindowTitleText.Text = "Second rate-limit window";
		WindowStatusText.Text = "loading";
		WeeklyStatusText.Text = "loading";
		BankedStatusText.Text = "waiting";
		TokenStatusText.Text = "waiting";
		WindowDetailText.Text = message;
		WeeklyDetailText.Text = "Waiting for API data.";
		SetUsageBar(WindowProgressBar, null);
		SetUsageBar(WeeklyProgressBar, null);
		BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 76, 37));
		ResetActionStatusText.Text = message;
	}

	private void SetCodexUsageErrorState(string message)
	{
		UsageWindowsGrid.Columns = 1;
		PrimaryWindowBorder.Margin = new Thickness(0);
		SecondaryWindowBorder.Visibility = Visibility.Collapsed;
		WindowTitleText.Text = "Rate-limit window";
		WindowStatusText.Text = "error";
		BankedStatusText.Text = "not loaded";
		TokenStatusText.Text = "Could not load usage.";
		WindowDetailText.Text = message;
		SetUsageBar(WindowProgressBar, null);
		SetUsageBar(WeeklyProgressBar, null);
	}

	private void ApplyCodexUsageSnapshot(CodexUsageSnapshot snapshot)
	{
		CodexUsageMetrics? metrics = snapshot.Metrics;
		CodexUsageWindow? primaryWindow = metrics?.PrimaryWindow;
		CodexUsageWindow? secondaryWindow = metrics?.SecondaryWindow;

		WindowTitleText.Text = BuildWindowTitle(primaryWindow, "Rate-limit window");
		ApplyUsageWindow(WindowStatusText, WindowDetailText, WindowProgressBar, primaryWindow, WindowTitleText.Text);

		if (secondaryWindow == null)
		{
			UsageWindowsGrid.Columns = 1;
			PrimaryWindowBorder.Margin = new Thickness(0);
			SecondaryWindowBorder.Visibility = Visibility.Collapsed;
		}
		else
		{
			UsageWindowsGrid.Columns = 2;
			PrimaryWindowBorder.Margin = new Thickness(0, 0, 6, 0);
			SecondaryWindowBorder.Visibility = Visibility.Visible;
			SecondaryWindowTitleText.Text = BuildWindowTitle(secondaryWindow, "Second rate-limit window");
			ApplyUsageWindow(WeeklyStatusText, WeeklyDetailText, WeeklyProgressBar, secondaryWindow, SecondaryWindowTitleText.Text);
		}

		if (metrics?.BankedResets == null)
		{
			BankedStatusText.Text = "not exposed";
			BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 76, 37));
		}
		else
		{
			BankedStatusText.Text = BuildBankedResetText(metrics);
			bool expirySoon = metrics.ResetExpiries.Any(expiry =>
				expiry.ExpiresAt != null &&
				IsExpirySoon(expiry.ExpiresAt.Value, DateTimeOffset.Now));
			BankedStatusBorder.BorderBrush = expirySoon
				? new SolidColorBrush(Color.FromRgb(220, 93, 93))
				: metrics.BankedResets.Value > 0
				? new SolidColorBrush(Color.FromRgb(107, 146, 72))
				: new SolidColorBrush(Color.FromRgb(92, 76, 37));
		}

		TokenStatusText.Text = BuildAccountStatusText(metrics);
	}

	private static string BuildBankedResetText(CodexUsageMetrics metrics)
	{
		if (metrics.BankedResets == null)
		{
			return "not exposed";
		}

		int available = metrics.BankedResets.Value;
		List<string> lines = new List<string>
		{
			"Available: " + available.ToString(CultureInfo.CurrentCulture)
		};
		if (available <= 0)
		{
			lines.Add("No reset credits available.");
			return string.Join(Environment.NewLine, lines);
		}

		List<CodexResetExpiry> expiries = metrics.ResetExpiries
			.Where(expiry =>
				expiry.GrantedAt != null ||
				expiry.ExpiresAt != null ||
				expiry.RedeemedAt != null ||
				!string.IsNullOrWhiteSpace(expiry.Title) ||
				!string.IsNullOrWhiteSpace(expiry.Status) ||
				!string.IsNullOrWhiteSpace(expiry.GrantedRawValue) ||
				!string.IsNullOrWhiteSpace(expiry.ExpiresRawValue) ||
				!string.IsNullOrWhiteSpace(expiry.RedeemedRawValue))
			.OrderByDescending(expiry => string.Equals(expiry.Status, "available", StringComparison.OrdinalIgnoreCase))
			.ThenBy(expiry => expiry.ExpiresAt ?? DateTimeOffset.MaxValue)
			.Take(Math.Max(available, 1))
			.ToList();
		if (expiries.Count == 0)
		{
			lines.Add("API expiry: not exposed");
			return string.Join(Environment.NewLine, lines);
		}

		foreach (CodexResetExpiry expiry in expiries)
		{
			lines.Add(FormatResetExpiry(expiry));
		}
		if (available > expiries.Count)
		{
			lines.Add("Additional reset credits not exposed.");
		}
		return string.Join(Environment.NewLine, lines);
	}

	private static string FormatResetExpiry(CodexResetExpiry expiry)
	{
		List<string> lines = new List<string>
		{
			expiry.Label + ":"
		};
		if (!string.IsNullOrWhiteSpace(expiry.Title))
		{
			lines.Add("Title: " + expiry.Title);
		}
		if (!string.IsNullOrWhiteSpace(expiry.Status))
		{
			lines.Add("Status: " + expiry.Status);
		}
		if (expiry.GrantedAt != null)
		{
			lines.Add("Granted: " + FormatShortDate(expiry.GrantedAt));
		}
		else if (!string.IsNullOrWhiteSpace(expiry.GrantedRawValue))
		{
			lines.Add("Granted: " + expiry.GrantedRawValue);
		}
		if (expiry.ExpiresAt != null)
		{
			lines.Add("Expires: " + FormatShortDate(expiry.ExpiresAt));
		}
		else if (!string.IsNullOrWhiteSpace(expiry.ExpiresRawValue))
		{
			lines.Add("Expires: " + expiry.ExpiresRawValue);
		}
		else
		{
			lines.Add("API expiry: not exposed");
			if (expiry.EstimatedExpiresAt != null)
			{
				lines.Add("Estimated expiry: " + FormatShortDate(expiry.EstimatedExpiresAt) + " (grant + 30 days; estimated, not confirmed)");
			}
		}
		if (expiry.RedeemedAt != null)
		{
			lines.Add("Redeemed: " + FormatShortDate(expiry.RedeemedAt));
		}
		else if (!string.IsNullOrWhiteSpace(expiry.RedeemedRawValue))
		{
			lines.Add("Redeemed: " + expiry.RedeemedRawValue);
		}
		return string.Join(Environment.NewLine, lines);
	}

	private void ApplyUsageWindow(TextBlock statusText, TextBlock detailText, ProgressBar progressBar, CodexUsageWindow? window, string label)
	{
		if (window == null)
		{
			statusText.Text = "not exposed";
			detailText.Text = label + " was not exposed by API.";
			SetUsageBar(progressBar, null);
			return;
		}

		statusText.Text = FormatPercent(window.UsedPercent) + " used";
		SetUsageBar(progressBar, window.UsedPercent);

		DateTimeOffset now = DateTimeOffset.Now;
		List<string> lines = new List<string>();
		if (window.WindowLength != null)
		{
			lines.Add("Window: " + FormatDuration(window.WindowLength.Value));
		}
		if (window.ResetAt != null && window.WindowLength != null)
		{
			DateTimeOffset start = window.ResetAt.Value - window.WindowLength.Value;
			lines.Add("Start: " + FormatShortDate(start));
			lines.Add("Reset: " + FormatShortDate(window.ResetAt) + " (" + FormatRelative(window.ResetAt.Value, now) + ")");
		}
		else if (window.ResetAt != null)
		{
			lines.Add("Reset: " + FormatShortDate(window.ResetAt) + " (" + FormatRelative(window.ResetAt.Value, now) + ")");
		}
		else if (window.ResetAfter != null)
		{
			lines.Add("Reset after: " + FormatDuration(window.ResetAfter.Value));
		}
		if (lines.Count == 0)
		{
			lines.Add("Reset time not exposed.");
		}
		detailText.Text = string.Join(Environment.NewLine, lines);
	}

	private static string BuildWindowTitle(CodexUsageWindow? window, string fallback)
	{
		if (window?.WindowLength == null || window.WindowLength.Value <= TimeSpan.Zero)
		{
			return fallback;
		}

		TimeSpan length = window.WindowLength.Value;
		double roundedDays = Math.Round(length.TotalDays);
		if (roundedDays >= 1 && Math.Abs(length.TotalDays - roundedDays) < 0.01)
		{
			return ((int)roundedDays).ToString(CultureInfo.CurrentCulture) + "-day window";
		}

		double roundedHours = Math.Round(length.TotalHours);
		if (roundedHours >= 1 && Math.Abs(length.TotalHours - roundedHours) < 0.01)
		{
			return ((int)roundedHours).ToString(CultureInfo.CurrentCulture) + "-hour window";
		}

		return FormatDuration(length) + " window";
	}

	private static string BuildAccountStatusText(CodexUsageMetrics? metrics)
	{
		if (metrics == null)
		{
			return "Usage metrics not exposed.";
		}

		List<string> lines = new List<string>();
		if (!string.IsNullOrWhiteSpace(metrics.PlanType))
		{
			lines.Add("Plan: " + metrics.PlanType);
		}
		if (metrics.Allowed != null)
		{
			lines.Add("Usage allowed: " + FormatBool(metrics.Allowed.Value));
		}
		if (metrics.LimitReached != null)
		{
			lines.Add("Rate limit reached: " + FormatBool(metrics.LimitReached.Value));
		}
		if (!string.IsNullOrWhiteSpace(metrics.LimitReachedType))
		{
			lines.Add("Limit type: " + metrics.LimitReachedType);
		}
		if (metrics.Credits != null)
		{
			if (metrics.Credits.HasCredits != null)
			{
				lines.Add("Paid credits: " + FormatBool(metrics.Credits.HasCredits.Value));
			}
			if (metrics.Credits.Unlimited != null)
			{
				lines.Add("Paid usage unlimited: " + FormatBool(metrics.Credits.Unlimited.Value));
			}
			if (!string.IsNullOrWhiteSpace(metrics.Credits.Balance))
			{
				lines.Add("Paid balance: " + metrics.Credits.Balance);
			}
		}
		if (metrics.SpendControl?.Reached != null)
		{
			lines.Add("Spend control reached: " + FormatBool(metrics.SpendControl.Reached.Value));
		}
		return lines.Count == 0 ? "Account fields not exposed." : string.Join(Environment.NewLine, lines);
	}

	private static void SetUsageBar(ProgressBar progressBar, double? percent)
	{
		double value = percent == null ? 0 : Math.Clamp(percent.Value, 0.0, 100.0);
		progressBar.Value = value;
		progressBar.Foreground = BuildUsageBrush(value);
	}

	private static Brush BuildUsageBrush(double percent)
	{
		if (percent >= 90)
		{
			return new SolidColorBrush(Color.FromRgb(230, 87, 87));
		}
		if (percent >= 70)
		{
			return new SolidColorBrush(Color.FromRgb(238, 174, 82));
		}
		return new SolidColorBrush(Color.FromRgb(106, 168, 255));
	}

	private static string FormatPercent(double? percent)
	{
		if (percent == null)
		{
			return "not exposed";
		}
		return Math.Clamp(percent.Value, 0.0, 100.0).ToString("0.#", CultureInfo.CurrentCulture) + "%";
	}

	private static string FormatBool(bool value)
	{
		return value ? "yes" : "no";
	}

	private static bool HasEstimatedExpiry(CodexUsageMetrics? metrics)
	{
		return metrics?.ResetExpiries.Any(expiry => expiry.EstimatedExpiresAt != null) == true;
	}

	private async void RestartExplorerButton_Click(object sender, RoutedEventArgs e)
	{
		if (!Confirm("Restart Windows Explorer?", "This closes and restarts explorer.exe. Your taskbar and File Explorer windows may disappear briefly."))
		{
			return;
		}
		await RunWindowsFixAsync("Restarting Explorer...", () =>
		{
			foreach (Process process in Process.GetProcessesByName("explorer"))
			{
				process.Kill();
			}
			Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
			return Task.FromResult("Explorer restarted.");
		});
	}

	private async void ClearTempButton_Click(object sender, RoutedEventArgs e)
	{
		if (!Confirm("Clear safe temp files?", "Quick Panel will delete old files from your user temp folder and skip locked or recent files."))
		{
			return;
		}
		await RunWindowsFixAsync("Clearing temp files...", () => Task.Run(ClearSafeTempFiles));
	}

	private void OpenStartupAppsButton_Click(object sender, RoutedEventArgs e)
	{
		RunOpenAction("Opened Startup Apps settings.", () => OpenTarget("ms-settings:startupapps"));
	}

	private void OpenBitdefenderButton_Click(object sender, RoutedEventArgs e)
	{
		RunOpenAction("Opened Bitdefender location or help page.", OpenBitdefenderLocation);
	}

	private void OpenPortableDataButton_Click(object sender, RoutedEventArgs e)
	{
		RunOpenAction("Opened Quick Panel portable data folder.", () =>
		{
			Directory.CreateDirectory(_settingsService.UserSettingsDirectory);
			OpenTarget(_settingsService.UserSettingsDirectory);
		});
	}

	private void ResetConfigButton_Click(object sender, RoutedEventArgs e)
	{
		if (!Confirm("Reset Quick Panel config?", "This backs up settings.json, resets panel preferences, and preserves tabs, history, zoom, and trips. Restart Quick Panel after this."))
		{
			return;
		}
		try
		{
			string backupPath = _settingsService.BackupAndResetPanelPreferences();
			WindowsFixStatusText.Text = "Config preferences reset. Backup saved: " + backupPath;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			WindowsFixStatusText.Text = "Could not reset config: " + ex.Message;
		}
	}

	private void OpenNetworkSharesButton_Click(object sender, RoutedEventArgs e)
	{
		RunOpenAction("Opened Network shares.", () => Process.Start(new ProcessStartInfo("explorer.exe", "shell:NetworkPlacesFolder") { UseShellExecute = true }));
	}

	private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
	{
		await RunWindowsFixAsync("Testing connection...", TestConnectionAsync);
	}

	private async Task RunWindowsFixAsync(string pendingMessage, Func<Task<string>> action)
	{
		WindowsFixStatusText.Text = pendingMessage;
		try
		{
			WindowsFixStatusText.Text = await action();
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is PingException)
		{
			WindowsFixStatusText.Text = "Action failed: " + ex.Message;
		}
	}

	private void RunOpenAction(string successMessage, Action action)
	{
		try
		{
			action();
			WindowsFixStatusText.Text = successMessage;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			WindowsFixStatusText.Text = "Could not open: " + ex.Message;
		}
	}

	private static string ClearSafeTempFiles()
	{
		string tempPath = Path.GetTempPath();
		DateTime cutoff = DateTime.Now.AddMinutes(-10);
		int deleted = 0;
		int skipped = 0;
		foreach (string entry in Directory.EnumerateFileSystemEntries(tempPath))
		{
			try
			{
				if (File.GetLastWriteTime(entry) > cutoff)
				{
					skipped++;
					continue;
				}
				if (Directory.Exists(entry))
				{
					Directory.Delete(entry, recursive: true);
				}
				else if (File.Exists(entry))
				{
					File.Delete(entry);
				}
				deleted++;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
			{
				skipped++;
			}
		}
		return "Temp cleanup complete. Deleted " + deleted.ToString(CultureInfo.InvariantCulture) +
			" item(s), skipped " + skipped.ToString(CultureInfo.InvariantCulture) + ".";
	}

	private static async Task<string> TestConnectionAsync()
	{
		string linkSpeed = GetBestLinkSpeedText();
		using Ping ping = new Ping();
		PingReply reply = await ping.SendPingAsync("1.1.1.1", 3000);
		string internet = reply.Status == IPStatus.Success
			? "Internet ping: " + reply.RoundtripTime.ToString(CultureInfo.InvariantCulture) + " ms"
			: "Internet ping failed: " + reply.Status;
		return internet + ". " + linkSpeed;
	}

	private static string GetBestLinkSpeedText()
	{
		long speed = NetworkInterface.GetAllNetworkInterfaces()
			.Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
				adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
				adapter.Speed > 0)
			.Select(adapter => adapter.Speed)
			.DefaultIfEmpty(0)
			.Max();
		return speed > 0
			? "Best LAN link: " + (speed / 1_000_000.0).ToString("0", CultureInfo.InvariantCulture) + " Mbps"
			: "No active LAN link speed found.";
	}

	private static void OpenBitdefenderLocation()
	{
		string[] folders =
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bitdefender"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Bitdefender")
		};
		string? folder = folders.FirstOrDefault(Directory.Exists);
		if (folder != null)
		{
			OpenTarget(folder);
			return;
		}
		OpenTarget("https://www.bitdefender.com/consumer/support/answer/13427/");
	}

	private static void OpenTarget(string target)
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = target,
			UseShellExecute = true
		});
	}

	private void TripSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loadingTrip)
		{
			return;
		}
		LoadSelectedTripIntoForm();
	}

	private void NewTripButton_Click(object sender, RoutedEventArgs e)
	{
		TripPlan trip = new TripPlan
		{
			Id = Guid.NewGuid().ToString("N"),
			Name = "New trip"
		};
		List<TripPlan> trips = _tripSettings.Trips.ToList();
		trips.Add(trip);
		_tripSettings = new TripModeSettings
		{
			SelectedTripId = trip.Id,
			Trips = trips
		};
		SaveTripSettings("New trip created.");
		RefreshTripSelector(trip.Id);
	}

	private void DeleteTripButton_Click(object sender, RoutedEventArgs e)
	{
		TripPlan? selected = GetSelectedTrip();
		if (selected == null)
		{
			return;
		}
		if (!Confirm("Delete trip?", "This removes the selected trip from Quick Panel settings."))
		{
			return;
		}
		List<TripPlan> trips = _tripSettings.Trips.Where(trip => !string.Equals(trip.Id, selected.Id, StringComparison.Ordinal)).ToList();
		_tripSettings = new TripModeSettings
		{
			SelectedTripId = trips.FirstOrDefault()?.Id,
			Trips = trips
		};
		if (_tripSettings.Trips.Count == 0)
		{
			LoadTripMode();
		}
		else
		{
			SaveTripSettings("Trip deleted.");
			RefreshTripSelector(_tripSettings.SelectedTripId);
		}
	}

	private void AddPackingItemButton_Click(object sender, RoutedEventArgs e)
	{
		string text = NewPackingItemTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		AddPackingCheckBox(text, isPacked: false);
		NewPackingItemTextBox.Text = string.Empty;
	}

	private void RemovePackedButton_Click(object sender, RoutedEventArgs e)
	{
		List<CheckBox> checkedItems = PackingItemsPanel.Children.OfType<CheckBox>()
			.Where(checkBox => checkBox.IsChecked == true)
			.ToList();
		foreach (CheckBox checkBox in checkedItems)
		{
			PackingItemsPanel.Children.Remove(checkBox);
		}
	}

	private void SaveTripButton_Click(object sender, RoutedEventArgs e)
	{
		TripPlan? selected = GetSelectedTrip();
		if (selected == null)
		{
			return;
		}
		TripPlan updated = BuildTripFromForm(selected.Id);
		List<TripPlan> trips = _tripSettings.Trips
			.Select(trip => string.Equals(trip.Id, selected.Id, StringComparison.Ordinal) ? updated : trip)
			.ToList();
		_tripSettings = new TripModeSettings
		{
			SelectedTripId = updated.Id,
			Trips = trips
		};
		SaveTripSettings("Trip saved.");
		RefreshTripSelector(updated.Id);
	}

	private void OpenMapLinksButton_Click(object sender, RoutedEventArgs e)
	{
		List<string> links = SavedMapsTextBox.Text
			.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.Trim())
			.Where(line => Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) &&
				(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
			.Take(5)
			.ToList();
		if (links.Count == 0)
		{
			TripStatusText.Text = "No valid map links to open.";
			return;
		}
		try
		{
			foreach (string link in links)
			{
				OpenTarget(link);
			}
			TripStatusText.Text = "Opened " + links.Count.ToString(CultureInfo.InvariantCulture) + " map link(s).";
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			TripStatusText.Text = "Could not open map links: " + ex.Message;
		}
	}

	private void SaveTripSettings(string message)
	{
		try
		{
			_settingsService.SaveTripModeSettings(_tripSettings);
			_tripSettings = _settingsService.LoadTripModeSettings();
			TripStatusText.Text = message;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			TripStatusText.Text = "Could not save trip data: " + ex.Message;
		}
	}

	private TripPlan BuildTripFromForm(string id)
	{
		return new TripPlan
		{
			Id = id,
			Name = string.IsNullOrWhiteSpace(TripNameTextBox.Text) ? "Trip" : TripNameTextBox.Text.Trim(),
			PackingItems = PackingItemsPanel.Children.OfType<CheckBox>()
				.Select(checkBox => new TripChecklistItem
				{
					Text = (checkBox.Content as string ?? string.Empty).Trim(),
					IsPacked = checkBox.IsChecked == true
				})
				.Where(item => !string.IsNullOrWhiteSpace(item.Text))
				.ToList(),
			SavedMapsLinks = SavedMapsTextBox.Text,
			CampingLinks = CampingLinksTextBox.Text,
			ParkingSpots = ParkingSpotsTextBox.Text,
			FuelEstimate = FuelEstimateTextBox.Text,
			DailyBudget = DailyBudgetTextBox.Text,
			EmergencyDocs = EmergencyDocsTextBox.Text,
			OfflineNotes = OfflineNotesTextBox.Text
		};
	}

	private TripPlan? GetSelectedTrip()
	{
		string? selectedId = TripSelector.SelectedValue as string;
		return _tripSettings.Trips.FirstOrDefault(trip => string.Equals(trip.Id, selectedId, StringComparison.Ordinal)) ??
			_tripSettings.Trips.FirstOrDefault();
	}

	private void AddPackingCheckBox(string text, bool isPacked)
	{
		CheckBox checkBox = new CheckBox
		{
			Content = text,
			IsChecked = isPacked,
			Margin = new Thickness(0, 0, 0, 5),
			Foreground = (Brush)FindResource("PanelTextBrush")
		};
		PackingItemsPanel.Children.Add(checkBox);
	}

	private bool Confirm(string title, string message)
	{
		return MessageBox.Show(
			Window.GetWindow(this),
			message,
			title,
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning) == MessageBoxResult.Yes;
	}

	private static string FormatShortDate(DateTimeOffset? value)
	{
		return value?.LocalDateTime.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture) ?? "not set";
	}

	private static string FormatRelative(DateTimeOffset value, DateTimeOffset now)
	{
		TimeSpan span = value - now;
		return span.TotalSeconds >= 0 ? "in " + FormatDuration(span) : FormatDuration(-span) + " ago";
	}

	private static string FormatDuration(TimeSpan span)
	{
		if (span.TotalDays >= 1)
		{
			return Math.Floor(span.TotalDays).ToString("0", CultureInfo.InvariantCulture) + "d " + span.Hours.ToString(CultureInfo.InvariantCulture) + "h";
		}
		if (span.TotalHours >= 1)
		{
			return Math.Floor(span.TotalHours).ToString("0", CultureInfo.InvariantCulture) + "h " + span.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
		}
		return Math.Max(0, span.Minutes).ToString(CultureInfo.InvariantCulture) + "m";
	}

	private static bool IsExpirySoon(DateTimeOffset? expiry, DateTimeOffset now)
	{
		return expiry != null && expiry.Value >= now && expiry.Value - now <= TimeSpan.FromDays(2);
	}
}
