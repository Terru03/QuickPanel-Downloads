using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuickPanel.Views;

public partial class QuickToolsTabView
{
	private bool _tripMapActionsAttached;

	[ModuleInitializer]
	internal static void RegisterTripMapActions()
	{
		EventManager.RegisterClassHandler(
			typeof(QuickToolsTabView),
			FrameworkElement.LoadedEvent,
			new RoutedEventHandler(TripMapActions_Loaded));
	}

	private static void TripMapActions_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is not QuickToolsTabView view || view._mode != QuickToolsPanelMode.TripPlanner)
		{
			return;
		}

		view.Dispatcher.BeginInvoke(
			DispatcherPriority.Loaded,
			new Action(view.AttachTripMapActions));
	}

	private void AttachTripMapActions()
	{
		if (_tripMapActionsAttached || !_productivityEnhancementsAttached)
		{
			return;
		}

		Button? summaryButton = FindButtonByContent(TripPlannerSection, "Copy trip summary");
		if (summaryButton?.Parent is not WrapPanel actions)
		{
			return;
		}

		_tripMapActionsAttached = true;
		Button routeButton = CreateQuickButton("Open route in Maps", OpenTripRouteInMaps_Click);
		actions.Children.Insert(0, routeButton);
	}

	private void OpenTripRouteInMaps_Click(object sender, RoutedEventArgs e)
	{
		CaptureTripPlannerDraft();
		string origin = _tripOriginTextBox?.Text.Trim() ?? string.Empty;
		string destination = _tripDestinationTextBox?.Text.Trim() ?? string.Empty;
		if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination))
		{
			TripStatusText.Text = "Enter both origin and destination before opening the route.";
			return;
		}

		string url = "https://www.google.com/maps/dir/?api=1&origin=" +
			Uri.EscapeDataString(origin) +
			"&destination=" + Uri.EscapeDataString(destination) +
			"&travelmode=driving";
		try
		{
			OpenTarget(url);
			TripStatusText.Text = "Opened the planned route in Google Maps.";
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			TripStatusText.Text = "Could not open the route: " + ex.Message;
		}
	}
}
