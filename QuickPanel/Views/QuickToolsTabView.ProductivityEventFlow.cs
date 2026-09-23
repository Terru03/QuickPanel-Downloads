using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuickPanel.Views;

public partial class QuickToolsTabView
{
	private bool _productivityEventFlowAttached;

	[ModuleInitializer]
	internal static void RegisterProductivityEventFlowFixes()
	{
		EventManager.RegisterClassHandler(
			typeof(QuickToolsTabView),
			FrameworkElement.LoadedEvent,
			new RoutedEventHandler(ProductivityEventFlow_Loaded));
	}

	private static void ProductivityEventFlow_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is not QuickToolsTabView view || view._mode != QuickToolsPanelMode.TripPlanner)
		{
			return;
		}

		view.Dispatcher.BeginInvoke(
			DispatcherPriority.Loaded,
			new Action(view.StabiliseTripPlannerEventFlow));
	}

	private void StabiliseTripPlannerEventFlow()
	{
		if (_mode != QuickToolsPanelMode.TripPlanner ||
			!_productivityEnhancementsAttached ||
			_productivityEventFlowAttached)
		{
			return;
		}

		_productivityEventFlowAttached = true;
		TripSelector.SelectionChanged -= TripSelector_SelectionChanged_Productivity;
		TripSelector.SelectionChanged -= TripSelector_SelectionChanged_Productivity_Safe;
		TripSelector.SelectionChanged += TripSelector_SelectionChanged_Productivity_Safe;

		AttachDeferredTripRefresh("New trip");
		AttachDeferredTripRefresh("Delete");
		AttachDeferredPackingProgress("Add");
		AttachDeferredPackingProgress("Remove checked");
	}

	private void TripSelector_SelectionChanged_Productivity_Safe(object sender, SelectionChangedEventArgs e)
	{
		if (_loadingTrip || _loadingTripPlannerEnhancements)
		{
			return;
		}

		CaptureTripPlannerDraft();
		LoadTripPlannerEnhancements();
	}

	private void AttachDeferredTripRefresh(string buttonText)
	{
		Button? button = FindButtonByContent(TripPlannerSection, buttonText);
		if (button == null)
		{
			return;
		}

		button.Click += (_, _) => Dispatcher.BeginInvoke(
			DispatcherPriority.Background,
			new Action(LoadTripPlannerEnhancements));
	}

	private void AttachDeferredPackingProgress(string buttonText)
	{
		Button? button = FindButtonByContent(TripPlannerSection, buttonText);
		if (button == null)
		{
			return;
		}

		button.Click += (_, _) => Dispatcher.BeginInvoke(
			DispatcherPriority.Background,
			new Action(UpdatePackingProgress));
	}
}
