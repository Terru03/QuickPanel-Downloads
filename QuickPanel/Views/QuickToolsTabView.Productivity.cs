using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QuickPanel.Models;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class QuickToolsTabView
{
	private readonly Dictionary<string, TripPlannerDraft> _tripPlannerDrafts = new(StringComparer.Ordinal);

	private bool _productivityEnhancementsAttached;
	private bool _loadingTripPlannerEnhancements;
	private string? _enhancedTripId;

	private TextBlock? _windowsSnapshotText;
	private TextBlock? _packingProgressText;
	private TextBlock? _tripEstimateText;
	private TextBox? _tripOriginTextBox;
	private TextBox? _tripDestinationTextBox;
	private TextBox? _tripStartDateTextBox;
	private TextBox? _tripEndDateTextBox;
	private TextBox? _tripDistanceTextBox;
	private TextBox? _tripConsumptionTextBox;
	private TextBox? _tripFuelPriceTextBox;
	private TextBox? _tripDailyBudgetAmountTextBox;
	private TextBox? _tripRoadCostsTextBox;
	private TextBox? _tripAccommodationCostsTextBox;
	private TextBox? _tripOtherCostsTextBox;
	private TextBox? _tripCurrencyTextBox;

	[ModuleInitializer]
	internal static void RegisterProductivityEnhancements()
	{
		EventManager.RegisterClassHandler(
			typeof(QuickToolsTabView),
			FrameworkElement.LoadedEvent,
			new RoutedEventHandler(ProductivityEnhancements_Loaded));
		EventManager.RegisterClassHandler(
			typeof(QuickToolsTabView),
			FrameworkElement.UnloadedEvent,
			new RoutedEventHandler(ProductivityEnhancements_Unloaded));
	}

	private static void ProductivityEnhancements_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is QuickToolsTabView view)
		{
			view.AttachProductivityEnhancements();
		}
	}

	private static void ProductivityEnhancements_Unloaded(object sender, RoutedEventArgs e)
	{
		if (sender is QuickToolsTabView view && view._mode == QuickToolsPanelMode.TripPlanner)
		{
			view.CaptureTripPlannerDraft();
		}
	}

	private void AttachProductivityEnhancements()
	{
		if (_productivityEnhancementsAttached)
		{
			return;
		}

		_productivityEnhancementsAttached = true;
		if (_mode == QuickToolsPanelMode.WindowsHelper)
		{
			BuildWindowsHelperEnhancements();
		}
		else if (_mode == QuickToolsPanelMode.TripPlanner)
		{
			BuildTripPlannerEnhancements();
		}
	}

	private void BuildWindowsHelperEnhancements()
	{
		if (WindowsHelperSection.Child is not StackPanel host)
		{
			return;
		}

		PanelSubtitleText.Text = "Windows diagnostics, common fixes, and shortcuts to the places that usually matter.";

		Border snapshotCard = CreateInnerCard();
		StackPanel snapshotStack = new StackPanel();
		snapshotCard.Child = snapshotStack;
		snapshotStack.Children.Add(CreateSectionTitle("System snapshot"));
		_windowsSnapshotText = new TextBlock
		{
			Margin = new Thickness(0, 5, 0, 8),
			Foreground = (Brush)FindResource("PanelMutedTextBrush"),
			FontSize = 10.5,
			TextWrapping = TextWrapping.Wrap
		};
		snapshotStack.Children.Add(_windowsSnapshotText);

		WrapPanel snapshotActions = new WrapPanel();
		snapshotActions.Children.Add(CreateQuickButton("Refresh snapshot", (_, _) => RefreshWindowsSnapshot()));
		snapshotActions.Children.Add(CreateQuickButton("Copy snapshot", (_, _) => CopyWindowsSnapshot()));
		snapshotStack.Children.Add(snapshotActions);

		WrapPanel windowsShortcuts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
		windowsShortcuts.Children.Add(CreateQuickButton("Task Manager", (_, _) => OpenWindowsUtility("Task Manager", "taskmgr.exe")));
		windowsShortcuts.Children.Add(CreateQuickButton("Windows Update", (_, _) => OpenWindowsSettings("Windows Update", "ms-settings:windowsupdate")));
		windowsShortcuts.Children.Add(CreateQuickButton("Storage", (_, _) => OpenWindowsSettings("Storage settings", "ms-settings:storagesense")));
		windowsShortcuts.Children.Add(CreateQuickButton("Network settings", (_, _) => OpenWindowsSettings("Network settings", "ms-settings:network-status")));
		windowsShortcuts.Children.Add(CreateQuickButton("Device Manager", (_, _) => OpenWindowsUtility("Device Manager", "devmgmt.msc")));
		windowsShortcuts.Children.Add(CreateQuickButton("Reliability history", (_, _) => OpenWindowsUtility("Reliability Monitor", "perfmon.exe", "/rel")));
		snapshotStack.Children.Add(windowsShortcuts);

		host.Children.Insert(Math.Min(2, host.Children.Count), snapshotCard);
		RefreshWindowsSnapshot();
	}

	private void RefreshWindowsSnapshot()
	{
		if (_windowsSnapshotText == null)
		{
			return;
		}

		try
		{
			_windowsSnapshotText.Text = BuildWindowsSnapshot();
			WindowsFixStatusText.Text = "System snapshot refreshed.";
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NetworkInformationException || ex is InvalidOperationException)
		{
			_windowsSnapshotText.Text = "Snapshot unavailable: " + ex.Message;
			WindowsFixStatusText.Text = "Could not refresh system snapshot.";
		}
	}

	private string BuildWindowsSnapshot()
	{
		List<string> lines = new List<string>
		{
			RuntimeInformation.OSDescription.Trim() + " | " + RuntimeInformation.OSArchitecture,
			"PC: " + Environment.MachineName + " | Uptime: " + FormatCompactDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))
		};

		string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
		if (!string.IsNullOrWhiteSpace(systemRoot))
		{
			DriveInfo systemDrive = new DriveInfo(systemRoot);
			if (systemDrive.IsReady)
			{
				lines.Add("System drive: " + FormatGiB(systemDrive.AvailableFreeSpace) + " GiB free / " + FormatGiB(systemDrive.TotalSize) + " GiB");
			}
		}

		NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces()
			.Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
			.OrderByDescending(network => network.Speed)
			.FirstOrDefault();
		if (adapter != null)
		{
			IPInterfaceProperties properties = adapter.GetIPProperties();
			string ip = properties.UnicastAddresses
				.Select(address => address.Address)
				.Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
				.Select(address => address.ToString())
				.FirstOrDefault() ?? "no IPv4";
			double speedMbps = Math.Max(0, adapter.Speed) / 1_000_000.0;
			lines.Add("Network: " + adapter.Name + " | " + speedMbps.ToString("0", CultureInfo.CurrentCulture) + " Mbps | " + ip);
		}
		else
		{
			lines.Add("Network: no active adapter found");
		}

		lines.Add("Quick Panel data: " + _settingsService.UserSettingsDirectory);
		return string.Join(Environment.NewLine, lines);
	}

	private void CopyWindowsSnapshot()
	{
		if (_windowsSnapshotText == null || string.IsNullOrWhiteSpace(_windowsSnapshotText.Text))
		{
			RefreshWindowsSnapshot();
		}
		try
		{
			Clipboard.SetText(_windowsSnapshotText?.Text ?? string.Empty);
			WindowsFixStatusText.Text = "System snapshot copied to clipboard.";
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is InvalidOperationException)
		{
			WindowsFixStatusText.Text = "Could not copy snapshot: " + ex.Message;
		}
	}

	private void OpenWindowsSettings(string label, string target)
	{
		try
		{
			OpenTarget(target);
			WindowsFixStatusText.Text = "Opened " + label + ".";
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			WindowsFixStatusText.Text = "Could not open " + label + ": " + ex.Message;
		}
	}

	private void OpenWindowsUtility(string label, string executable, string? arguments = null)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = executable,
				Arguments = arguments ?? string.Empty,
				UseShellExecute = true
			});
			WindowsFixStatusText.Text = "Opened " + label + ".";
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			WindowsFixStatusText.Text = "Could not open " + label + ": " + ex.Message;
		}
	}

	private void BuildTripPlannerEnhancements()
	{
		if (TripPlannerSection.Child is not StackPanel host)
		{
			return;
		}

		PanelSubtitleText.Text = "Local route, packing, budget, fuel, maps, and offline trip notes.";
		RenameTripPlannerLegacyLabels(host);

		Border planningCard = CreateInnerCard();
		StackPanel planningStack = new StackPanel();
		planningCard.Child = planningStack;
		planningStack.Children.Add(CreateSectionTitle("Route & budget"));
		planningStack.Children.Add(new TextBlock
		{
			Margin = new Thickness(0, 3, 0, 8),
			Foreground = (Brush)FindResource("PanelMutedTextBrush"),
			FontSize = 10.5,
			TextWrapping = TextWrapping.Wrap,
			Text = "These fields are saved with the trip when you use the existing Save trip button. Dates use YYYY-MM-DD."
		});

		_tripOriginTextBox = CreateTripTextBox();
		_tripDestinationTextBox = CreateTripTextBox();
		_tripStartDateTextBox = CreateTripTextBox();
		_tripEndDateTextBox = CreateTripTextBox();
		_tripDistanceTextBox = CreateTripTextBox();
		_tripConsumptionTextBox = CreateTripTextBox();
		_tripFuelPriceTextBox = CreateTripTextBox();
		_tripDailyBudgetAmountTextBox = CreateTripTextBox();
		_tripRoadCostsTextBox = CreateTripTextBox();
		_tripAccommodationCostsTextBox = CreateTripTextBox();
		_tripOtherCostsTextBox = CreateTripTextBox();
		_tripCurrencyTextBox = CreateTripTextBox();

		UniformGrid fields = new UniformGrid { Columns = 2 };
		fields.Children.Add(CreateTripField("Origin", _tripOriginTextBox));
		fields.Children.Add(CreateTripField("Destination", _tripDestinationTextBox));
		fields.Children.Add(CreateTripField("Start date", _tripStartDateTextBox));
		fields.Children.Add(CreateTripField("End date", _tripEndDateTextBox));
		fields.Children.Add(CreateTripField("Total distance (km)", _tripDistanceTextBox));
		fields.Children.Add(CreateTripField("Consumption (L/100 km)", _tripConsumptionTextBox));
		fields.Children.Add(CreateTripField("Fuel price / litre", _tripFuelPriceTextBox));
		fields.Children.Add(CreateTripField("Daily budget / day", _tripDailyBudgetAmountTextBox));
		fields.Children.Add(CreateTripField("Road / toll costs", _tripRoadCostsTextBox));
		fields.Children.Add(CreateTripField("Accommodation", _tripAccommodationCostsTextBox));
		fields.Children.Add(CreateTripField("Other fixed costs", _tripOtherCostsTextBox));
		fields.Children.Add(CreateTripField("Currency", _tripCurrencyTextBox));
		planningStack.Children.Add(fields);

		Border estimateBorder = new Border
		{
			Margin = new Thickness(0, 4, 0, 8),
			Padding = new Thickness(10, 8, 10, 8),
			Background = new SolidColorBrush(Color.FromRgb(16, 26, 42)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(42, 72, 122)),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(7)
		};
		_tripEstimateText = new TextBlock
		{
			Foreground = (Brush)FindResource("PanelTextBrush"),
			FontSize = 10.5,
			TextWrapping = TextWrapping.Wrap
		};
		estimateBorder.Child = _tripEstimateText;
		planningStack.Children.Add(estimateBorder);

		WrapPanel actions = new WrapPanel();
		actions.Children.Add(CreateQuickButton("Copy trip summary", (_, _) => CopyTripSummary()));
		actions.Children.Add(CreateQuickButton("Add travel essentials", (_, _) => AddPackingTemplate(TravelEssentials, "travel essentials")));
		actions.Children.Add(CreateQuickButton("Add photo / hike kit", (_, _) => AddPackingTemplate(PhotoHikeEssentials, "photo / hike kit")));
		planningStack.Children.Add(actions);

		host.Children.Insert(Math.Min(3, host.Children.Count), planningCard);

		if (PackingItemsPanel.Parent is StackPanel packingHost)
		{
			_packingProgressText = new TextBlock
			{
				Margin = new Thickness(0, 0, 0, 7),
				Foreground = (Brush)FindResource("PanelMutedTextBrush"),
				FontSize = 10.5
			};
			packingHost.Children.Insert(0, _packingProgressText);
			PackingItemsPanel.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(PackingItemStateChanged), true);
			PackingItemsPanel.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(PackingItemStateChanged), true);
		}

		TripSelector.SelectionChanged += TripSelector_SelectionChanged_Productivity;
		Button? saveTripButton = FindButtonByContent(TripPlannerSection, "Save trip");
		if (saveTripButton != null)
		{
			saveTripButton.Click += SaveTripButton_Click_Productivity;
		}

		LoadTripPlannerEnhancements();
	}

	private void RenameTripPlannerLegacyLabels(StackPanel host)
	{
		foreach (TextBlock textBlock in EnumerateDescendants<TextBlock>(host))
		{
			if (string.Equals(textBlock.Text, "Daily budget", StringComparison.Ordinal))
			{
				textBlock.Text = "Budget notes";
			}
		}
	}

	private TextBox CreateTripTextBox()
	{
		TextBox textBox = new TextBox
		{
			Style = (Style)FindResource("QuickToolTextBoxStyle"),
			Margin = new Thickness(0, 0, 8, 0)
		};
		textBox.TextChanged += TripEnhancementInputChanged;
		return textBox;
	}

	private FrameworkElement CreateTripField(string label, TextBox textBox)
	{
		StackPanel field = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
		field.Children.Add(new TextBlock
		{
			Margin = new Thickness(0, 0, 0, 3),
			Foreground = (Brush)FindResource("PanelMutedTextBrush"),
			FontSize = 10,
			FontWeight = FontWeights.SemiBold,
			Text = label
		});
		field.Children.Add(textBox);
		return field;
	}

	private void TripSelector_SelectionChanged_Productivity(object sender, SelectionChangedEventArgs e)
	{
		CaptureTripPlannerDraft();
		LoadTripPlannerEnhancements();
	}

	private void TripEnhancementInputChanged(object sender, TextChangedEventArgs e)
	{
		if (_loadingTripPlannerEnhancements)
		{
			return;
		}
		CaptureTripPlannerDraft();
		UpdateTripEstimate();
	}

	private void PackingItemStateChanged(object sender, RoutedEventArgs e)
	{
		UpdatePackingProgress();
	}

	private void LoadTripPlannerEnhancements()
	{
		if (_tripOriginTextBox == null)
		{
			return;
		}

		TripPlan? trip = GetSelectedTrip();
		if (trip == null)
		{
			_enhancedTripId = null;
			return;
		}

		_enhancedTripId = trip.Id;
		if (!_tripPlannerDrafts.TryGetValue(trip.Id, out TripPlannerDraft? draft))
		{
			draft = TripPlannerDraft.FromTrip(trip);
			_tripPlannerDrafts[trip.Id] = draft;
		}

		_loadingTripPlannerEnhancements = true;
		try
		{
			_tripOriginTextBox.Text = draft.Origin;
			_tripDestinationTextBox!.Text = draft.Destination;
			_tripStartDateTextBox!.Text = draft.StartDate;
			_tripEndDateTextBox!.Text = draft.EndDate;
			_tripDistanceTextBox!.Text = draft.DistanceKm;
			_tripConsumptionTextBox!.Text = draft.Consumption;
			_tripFuelPriceTextBox!.Text = draft.FuelPrice;
			_tripDailyBudgetAmountTextBox!.Text = draft.DailyBudget;
			_tripRoadCostsTextBox!.Text = draft.RoadCosts;
			_tripAccommodationCostsTextBox!.Text = draft.AccommodationCosts;
			_tripOtherCostsTextBox!.Text = draft.OtherCosts;
			_tripCurrencyTextBox!.Text = draft.Currency;
		}
		finally
		{
			_loadingTripPlannerEnhancements = false;
		}

		UpdateTripEstimate();
		UpdatePackingProgress();
	}

	private void CaptureTripPlannerDraft()
	{
		if (_loadingTripPlannerEnhancements || string.IsNullOrWhiteSpace(_enhancedTripId) || _tripOriginTextBox == null)
		{
			return;
		}
		if (!_tripPlannerDrafts.TryGetValue(_enhancedTripId, out TripPlannerDraft? draft))
		{
			TripPlan? baseline = _tripSettings.Trips.FirstOrDefault(trip => string.Equals(trip.Id, _enhancedTripId, StringComparison.Ordinal));
			if (baseline == null)
			{
				return;
			}
			draft = TripPlannerDraft.FromTrip(baseline);
			_tripPlannerDrafts[_enhancedTripId] = draft;
		}

		draft.Origin = _tripOriginTextBox.Text;
		draft.Destination = _tripDestinationTextBox?.Text ?? string.Empty;
		draft.StartDate = _tripStartDateTextBox?.Text ?? string.Empty;
		draft.EndDate = _tripEndDateTextBox?.Text ?? string.Empty;
		draft.DistanceKm = _tripDistanceTextBox?.Text ?? string.Empty;
		draft.Consumption = _tripConsumptionTextBox?.Text ?? string.Empty;
		draft.FuelPrice = _tripFuelPriceTextBox?.Text ?? string.Empty;
		draft.DailyBudget = _tripDailyBudgetAmountTextBox?.Text ?? string.Empty;
		draft.RoadCosts = _tripRoadCostsTextBox?.Text ?? string.Empty;
		draft.AccommodationCosts = _tripAccommodationCostsTextBox?.Text ?? string.Empty;
		draft.OtherCosts = _tripOtherCostsTextBox?.Text ?? string.Empty;
		draft.Currency = _tripCurrencyTextBox?.Text ?? string.Empty;
	}

	private void SaveTripButton_Click_Productivity(object sender, RoutedEventArgs e)
	{
		CaptureTripPlannerDraft();
		TripPlan? current = GetSelectedTrip();
		if (current == null || !_tripPlannerDrafts.TryGetValue(current.Id, out TripPlannerDraft? draft))
		{
			return;
		}

		bool valid = TryBuildEnhancedTrip(current, draft, out TripPlan updated, out string? error);
		if (!valid)
		{
			updated = CopyEnhancedFields(current, draft.Baseline);
		}

		_tripSettings = new TripModeSettings
		{
			SelectedTripId = current.Id,
			Trips = _tripSettings.Trips
				.Select(trip => string.Equals(trip.Id, current.Id, StringComparison.Ordinal) ? updated : trip)
				.ToList()
		};

		try
		{
			_settingsService.SaveTripModeSettings(_tripSettings);
			_tripSettings = _settingsService.LoadTripModeSettings();
			draft.Baseline = updated;
			TripStatusText.Text = valid
				? "Trip saved. Route and budget details included."
				: "Trip saved, but route/budget edits were not saved: " + error;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
		{
			TripStatusText.Text = "Trip saved, but route/budget details could not be persisted: " + ex.Message;
		}
	}

	private bool TryBuildEnhancedTrip(TripPlan source, TripPlannerDraft draft, out TripPlan updated, out string? error)
	{
		updated = source;
		error = null;
		if (!TryParseOptionalDate(draft.StartDate, out DateTime? startDate) || !TryParseOptionalDate(draft.EndDate, out DateTime? endDate))
		{
			error = "use YYYY-MM-DD for dates.";
			return false;
		}
		if (!TryParseOptionalNumber(draft.DistanceKm, out double? distance) ||
			!TryParseOptionalNumber(draft.Consumption, out double? consumption) ||
			!TryParseOptionalNumber(draft.FuelPrice, out double? fuelPrice) ||
			!TryParseOptionalNumber(draft.DailyBudget, out double? dailyBudget) ||
			!TryParseOptionalNumber(draft.RoadCosts, out double? roadCosts) ||
			!TryParseOptionalNumber(draft.AccommodationCosts, out double? accommodationCosts) ||
			!TryParseOptionalNumber(draft.OtherCosts, out double? otherCosts))
		{
			error = "cost, distance, fuel price, and consumption fields must be non-negative numbers.";
			return false;
		}

		TripEstimate estimate = TripPlannerCalculator.Calculate(
			startDate,
			endDate,
			distance,
			consumption,
			fuelPrice,
			dailyBudget,
			roadCosts,
			accommodationCosts,
			otherCosts);
		if (!estimate.DateRangeValid)
		{
			error = "end date cannot be before start date.";
			return false;
		}

		updated = new TripPlan
		{
			Id = source.Id,
			Name = source.Name,
			Origin = NullIfWhiteSpace(draft.Origin),
			Destination = NullIfWhiteSpace(draft.Destination),
			StartDate = startDate,
			EndDate = endDate,
			DistanceKm = distance,
			FuelConsumptionLitresPer100Km = consumption,
			FuelPricePerLitre = fuelPrice,
			RoadCosts = roadCosts,
			AccommodationCosts = accommodationCosts,
			OtherCosts = otherCosts,
			DailyBudgetAmount = dailyBudget,
			BudgetCurrency = string.IsNullOrWhiteSpace(draft.Currency) ? "EUR" : draft.Currency.Trim().ToUpperInvariant(),
			PackingItems = source.PackingItems.ToList(),
			SavedMapsLinks = source.SavedMapsLinks,
			CampingLinks = source.CampingLinks,
			ParkingSpots = source.ParkingSpots,
			FuelEstimate = source.FuelEstimate,
			DailyBudget = source.DailyBudget,
			EmergencyDocs = source.EmergencyDocs,
			OfflineNotes = source.OfflineNotes
		};
		return true;
	}

	private static TripPlan CopyEnhancedFields(TripPlan source, TripPlan enhancedSource)
	{
		return new TripPlan
		{
			Id = source.Id,
			Name = source.Name,
			Origin = enhancedSource.Origin,
			Destination = enhancedSource.Destination,
			StartDate = enhancedSource.StartDate,
			EndDate = enhancedSource.EndDate,
			DistanceKm = enhancedSource.DistanceKm,
			FuelConsumptionLitresPer100Km = enhancedSource.FuelConsumptionLitresPer100Km,
			FuelPricePerLitre = enhancedSource.FuelPricePerLitre,
			RoadCosts = enhancedSource.RoadCosts,
			AccommodationCosts = enhancedSource.AccommodationCosts,
			OtherCosts = enhancedSource.OtherCosts,
			DailyBudgetAmount = enhancedSource.DailyBudgetAmount,
			BudgetCurrency = enhancedSource.BudgetCurrency,
			PackingItems = source.PackingItems.ToList(),
			SavedMapsLinks = source.SavedMapsLinks,
			CampingLinks = source.CampingLinks,
			ParkingSpots = source.ParkingSpots,
			FuelEstimate = source.FuelEstimate,
			DailyBudget = source.DailyBudget,
			EmergencyDocs = source.EmergencyDocs,
			OfflineNotes = source.OfflineNotes
		};
	}

	private void UpdateTripEstimate()
	{
		if (_tripEstimateText == null || string.IsNullOrWhiteSpace(_enhancedTripId))
		{
			return;
		}
		CaptureTripPlannerDraft();
		if (!_tripPlannerDrafts.TryGetValue(_enhancedTripId, out TripPlannerDraft? draft))
		{
			return;
		}

		if (!TryParseOptionalDate(draft.StartDate, out DateTime? startDate) ||
			!TryParseOptionalDate(draft.EndDate, out DateTime? endDate) ||
			!TryParseOptionalNumber(draft.DistanceKm, out double? distance) ||
			!TryParseOptionalNumber(draft.Consumption, out double? consumption) ||
			!TryParseOptionalNumber(draft.FuelPrice, out double? fuelPrice) ||
			!TryParseOptionalNumber(draft.DailyBudget, out double? dailyBudget) ||
			!TryParseOptionalNumber(draft.RoadCosts, out double? roadCosts) ||
			!TryParseOptionalNumber(draft.AccommodationCosts, out double? accommodationCosts) ||
			!TryParseOptionalNumber(draft.OtherCosts, out double? otherCosts))
		{
			_tripEstimateText.Text = "Estimate: enter valid non-negative numbers and dates in YYYY-MM-DD format.";
			return;
		}

		TripEstimate estimate = TripPlannerCalculator.Calculate(
			startDate,
			endDate,
			distance,
			consumption,
			fuelPrice,
			dailyBudget,
			roadCosts,
			accommodationCosts,
			otherCosts);
		if (!estimate.DateRangeValid)
		{
			_tripEstimateText.Text = "Estimate: end date cannot be before start date.";
			return;
		}

		string currency = string.IsNullOrWhiteSpace(draft.Currency) ? "EUR" : draft.Currency.Trim().ToUpperInvariant();
		_tripEstimateText.Text =
			estimate.Days.ToString(CultureInfo.CurrentCulture) + " day(s) | " +
			FormatOptional(distance, " km") + " | " + estimate.FuelLitres.ToString("0.#", CultureInfo.CurrentCulture) + " L fuel" + Environment.NewLine +
			"Fuel: " + FormatMoney(estimate.FuelCost, currency) + " | Daily budget: " + FormatMoney(estimate.DailyBudgetCost, currency) + Environment.NewLine +
			"Estimated total: " + FormatMoney(estimate.TotalCost, currency);
	}

	private void UpdatePackingProgress()
	{
		if (_packingProgressText == null)
		{
			return;
		}
		List<CheckBox> items = PackingItemsPanel.Children.OfType<CheckBox>().ToList();
		int packed = items.Count(item => item.IsChecked == true);
		int total = items.Count;
		int percent = total == 0 ? 0 : (int)Math.Round(packed * 100.0 / total);
		_packingProgressText.Text = total == 0
			? "No packing items yet."
			: packed.ToString(CultureInfo.CurrentCulture) + "/" + total.ToString(CultureInfo.CurrentCulture) + " packed (" + percent.ToString(CultureInfo.CurrentCulture) + "%)";
	}

	private void AddPackingTemplate(IEnumerable<string> items, string templateName)
	{
		HashSet<string> existing = PackingItemsPanel.Children.OfType<CheckBox>()
			.Select(checkBox => (checkBox.Content as string ?? string.Empty).Trim())
			.Where(text => !string.IsNullOrWhiteSpace(text))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		int added = 0;
		foreach (string item in items)
		{
			if (existing.Add(item))
			{
				AddPackingCheckBox(item, isPacked: false);
				added++;
			}
		}
		UpdatePackingProgress();
		TripStatusText.Text = added == 0
			? "All " + templateName + " items are already on the checklist."
			: "Added " + added.ToString(CultureInfo.CurrentCulture) + " " + templateName + " item(s). Save trip to keep them.";
	}

	private void CopyTripSummary()
	{
		CaptureTripPlannerDraft();
		TripPlan? trip = GetSelectedTrip();
		if (trip == null || string.IsNullOrWhiteSpace(_enhancedTripId) || !_tripPlannerDrafts.TryGetValue(_enhancedTripId, out TripPlannerDraft? draft))
		{
			return;
		}

		StringBuilder summary = new StringBuilder();
		summary.AppendLine(trip.Name);
		if (!string.IsNullOrWhiteSpace(draft.Origin) || !string.IsNullOrWhiteSpace(draft.Destination))
		{
			summary.AppendLine("Route: " + (string.IsNullOrWhiteSpace(draft.Origin) ? "?" : draft.Origin.Trim()) + " -> " + (string.IsNullOrWhiteSpace(draft.Destination) ? "?" : draft.Destination.Trim()));
		}
		if (!string.IsNullOrWhiteSpace(draft.StartDate) || !string.IsNullOrWhiteSpace(draft.EndDate))
		{
			summary.AppendLine("Dates: " + (string.IsNullOrWhiteSpace(draft.StartDate) ? "?" : draft.StartDate.Trim()) + " to " + (string.IsNullOrWhiteSpace(draft.EndDate) ? "?" : draft.EndDate.Trim()));
		}
		if (_tripEstimateText != null && !string.IsNullOrWhiteSpace(_tripEstimateText.Text))
		{
			summary.AppendLine(_tripEstimateText.Text);
		}
		List<CheckBox> packing = PackingItemsPanel.Children.OfType<CheckBox>().ToList();
		summary.AppendLine("Packing: " + packing.Count(item => item.IsChecked == true).ToString(CultureInfo.CurrentCulture) + "/" + packing.Count.ToString(CultureInfo.CurrentCulture));
		if (!string.IsNullOrWhiteSpace(SavedMapsTextBox.Text))
		{
			summary.AppendLine("Maps:");
			summary.AppendLine(SavedMapsTextBox.Text.Trim());
		}
		if (!string.IsNullOrWhiteSpace(OfflineNotesTextBox.Text))
		{
			summary.AppendLine("Notes:");
			summary.AppendLine(OfflineNotesTextBox.Text.Trim());
		}

		try
		{
			Clipboard.SetText(summary.ToString().Trim());
			TripStatusText.Text = "Trip summary copied to clipboard.";
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is InvalidOperationException)
		{
			TripStatusText.Text = "Could not copy trip summary: " + ex.Message;
		}
	}

	private Border CreateInnerCard()
	{
		return new Border
		{
			Margin = new Thickness(0, 8, 0, 8),
			Padding = new Thickness(11, 10, 11, 10),
			Background = new SolidColorBrush(Color.FromRgb(13, 17, 26)),
			BorderBrush = (Brush)FindResource("PanelBorderSubtleBrush"),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(7)
		};
	}

	private TextBlock CreateSectionTitle(string text)
	{
		return new TextBlock
		{
			FontSize = 11.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = (Brush)FindResource("PanelTextBrush"),
			Text = text
		};
	}

	private Button CreateQuickButton(string text, RoutedEventHandler handler)
	{
		Button button = new Button
		{
			Style = (Style)FindResource("QuickToolButtonStyle"),
			Content = text
		};
		button.Click += handler;
		return button;
	}

	private static Button? FindButtonByContent(DependencyObject root, string content)
	{
		if (root is Button button && string.Equals(button.Content as string, content, StringComparison.Ordinal))
		{
			return button;
		}
		int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
		for (int index = 0; index < childCount; index++)
		{
			Button? found = FindButtonByContent(System.Windows.Media.VisualTreeHelper.GetChild(root, index), content);
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}

	private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root) where T : DependencyObject
	{
		int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
		for (int index = 0; index < childCount; index++)
		{
			DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
			if (child is T typed)
			{
				yield return typed;
			}
			foreach (T nested in EnumerateDescendants<T>(child))
			{
				yield return nested;
			}
		}
	}

	private static bool TryParseOptionalNumber(string text, out double? value)
	{
		string trimmed = text.Trim();
		if (trimmed.Length == 0)
		{
			value = null;
			return true;
		}
		string normalized = trimmed.Replace(',', '.');
		if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) && parsed >= 0)
		{
			value = parsed;
			return true;
		}
		value = null;
		return false;
	}

	private static bool TryParseOptionalDate(string text, out DateTime? value)
	{
		string trimmed = text.Trim();
		if (trimmed.Length == 0)
		{
			value = null;
			return true;
		}
		if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
		{
			value = parsed.Date;
			return true;
		}
		value = null;
		return false;
	}

	private static string FormatOptional(double? value, string suffix)
	{
		return value == null ? "distance not set" : value.Value.ToString("0.#", CultureInfo.CurrentCulture) + suffix;
	}

	private static string FormatMoney(double value, string currency)
	{
		return currency + " " + value.ToString("0.##", CultureInfo.CurrentCulture);
	}

	private static string FormatGiB(long bytes)
	{
		return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture);
	}

	private static string FormatCompactDuration(TimeSpan span)
	{
		if (span.TotalDays >= 1)
		{
			return ((int)span.TotalDays).ToString(CultureInfo.CurrentCulture) + "d " + span.Hours.ToString(CultureInfo.CurrentCulture) + "h";
		}
		return ((int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + "h " + span.Minutes.ToString(CultureInfo.CurrentCulture) + "m";
	}

	private static string? NullIfWhiteSpace(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private static readonly string[] TravelEssentials =
	{
		"ID / passport",
		"Wallet / cards",
		"Phone charger",
		"Power bank",
		"Medication",
		"Water bottle",
		"Offline maps",
		"Travel insurance details"
	};

	private static readonly string[] PhotoHikeEssentials =
	{
		"Hiking shoes",
		"Rain jacket",
		"Headlamp",
		"Camera",
		"Spare camera battery",
		"Memory cards",
		"Lens cloth",
		"Small first-aid kit"
	};

	private sealed class TripPlannerDraft
	{
		public TripPlan Baseline { get; set; } = new TripPlan();
		public string Origin { get; set; } = string.Empty;
		public string Destination { get; set; } = string.Empty;
		public string StartDate { get; set; } = string.Empty;
		public string EndDate { get; set; } = string.Empty;
		public string DistanceKm { get; set; } = string.Empty;
		public string Consumption { get; set; } = string.Empty;
		public string FuelPrice { get; set; } = string.Empty;
		public string DailyBudget { get; set; } = string.Empty;
		public string RoadCosts { get; set; } = string.Empty;
		public string AccommodationCosts { get; set; } = string.Empty;
		public string OtherCosts { get; set; } = string.Empty;
		public string Currency { get; set; } = "EUR";

		public static TripPlannerDraft FromTrip(TripPlan trip)
		{
			return new TripPlannerDraft
			{
				Baseline = trip,
				Origin = trip.Origin ?? string.Empty,
				Destination = trip.Destination ?? string.Empty,
				StartDate = trip.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
				EndDate = trip.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
				DistanceKm = FormatNumber(trip.DistanceKm),
				Consumption = FormatNumber(trip.FuelConsumptionLitresPer100Km),
				FuelPrice = FormatNumber(trip.FuelPricePerLitre),
				DailyBudget = FormatNumber(trip.DailyBudgetAmount),
				RoadCosts = FormatNumber(trip.RoadCosts),
				AccommodationCosts = FormatNumber(trip.AccommodationCosts),
				OtherCosts = FormatNumber(trip.OtherCosts),
				Currency = string.IsNullOrWhiteSpace(trip.BudgetCurrency) ? "EUR" : trip.BudgetCurrency!
			};
		}

		private static string FormatNumber(double? value)
		{
			return value?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
		}
	}
}
