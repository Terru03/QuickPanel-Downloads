using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class TaskManagerTabView : UserControl
{
	private sealed class ProcessItem : INotifyPropertyChanged
	{
		private readonly DateTime? _startTimeUtc;
		private double _cpuPercent;
		private long _workingSetBytes;
		private string _status;
		private string _windowTitle;
		private bool _canTerminate;

		public ProcessItem(TaskManagerProcessSnapshot snapshot)
		{
			ProcessId = snapshot.ProcessId;
			Name = snapshot.Name;
			_startTimeUtc = snapshot.StartTimeUtc;
			_cpuPercent = snapshot.CpuPercent;
			_workingSetBytes = snapshot.WorkingSetBytes;
			_status = snapshot.Status;
			_windowTitle = snapshot.WindowTitle;
			_canTerminate = snapshot.CanTerminate;
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		public int ProcessId { get; }

		public string Name { get; }

		public double CpuPercent => _cpuPercent;

		public double MemoryMegabytes => _workingSetBytes / 1024.0 / 1024.0;

		public string Status => _status;

		public string WindowTitle => _windowTitle;

		public bool CanTerminate => _canTerminate;

		public TaskManagerProcessSnapshot ToSnapshot() => new(
			ProcessId,
			Name,
			_startTimeUtc,
			_cpuPercent,
			_workingSetBytes,
			_status,
			_windowTitle,
			_canTerminate);

		public void Update(TaskManagerProcessSnapshot snapshot)
		{
			_cpuPercent = snapshot.CpuPercent;
			_workingSetBytes = snapshot.WorkingSetBytes;
			_status = snapshot.Status;
			_windowTitle = snapshot.WindowTitle;
			_canTerminate = snapshot.CanTerminate;
			OnPropertyChanged(nameof(CpuPercent));
			OnPropertyChanged(nameof(MemoryMegabytes));
			OnPropertyChanged(nameof(Status));
			OnPropertyChanged(nameof(WindowTitle));
			OnPropertyChanged(nameof(CanTerminate));
		}

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}

	private readonly TaskManagerProcessService _processService = new();
	private readonly HardwareTelemetryService _hardwareService = new();
	private readonly ObservableCollection<ProcessItem> _processes = [];
	private readonly ICollectionView _processView;
	private readonly DispatcherTimer _refreshTimer;
	private readonly TelemetryHistorySeries _cpuHistory = new(30, 100.0);
	private readonly TelemetryHistorySeries _memoryHistory = new(30, 100.0);
	private readonly TelemetryHistorySeries _gpuHistory = new(30, 100.0);
	private readonly TelemetryHistorySeries _storageHistory = new(30, 100.0);
	private readonly TelemetryHistorySeries _temperatureHistory = new(30, 110.0);
	private readonly TelemetryHistorySeries _fanHistory = new(30, 5000.0);
	private bool _refreshInProgress;
	private bool _hardwareRefreshInProgress;
	private bool _isPerformanceMode;

	public TaskManagerTabView()
	{
		InitializeComponent();
		_processView = CollectionViewSource.GetDefaultView(_processes);
		_processView.Filter = FilterProcess;
		_processView.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.CpuPercent), ListSortDirection.Descending));
		_processView.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.MemoryMegabytes), ListSortDirection.Descending));
		ProcessGrid.ItemsSource = _processView;
		_refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
		_refreshTimer.Tick += RefreshTimer_Tick;
		Loaded += TaskManagerTabView_Loaded;
		Unloaded += TaskManagerTabView_Unloaded;
		SizeChanged += TaskManagerTabView_SizeChanged;
	}

	private async void TaskManagerTabView_Loaded(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		ApplyResponsiveLayout(ActualWidth);
		_refreshTimer.Start();
		await RefreshActiveViewAsync();
	}

	private void TaskManagerTabView_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		_ = sender;
		ApplyResponsiveLayout(e.NewSize.Width);
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)RedrawTelemetryCharts);
	}

	private void ApplyResponsiveLayout(double width)
	{
		TaskManagerResponsiveLayout layout = TaskManagerPresentationPolicy.ForWidth(width);
		ProcessIdColumn.Visibility = layout.ShowProcessId ? Visibility.Visible : Visibility.Collapsed;
		ProcessStatusColumn.Visibility = layout.ShowStatus ? Visibility.Visible : Visibility.Collapsed;
		ProcessWindowColumn.Visibility = layout.ShowWindowTitle ? Visibility.Visible : Visibility.Collapsed;
		SummaryGrid.Columns = layout.SummaryColumns;
	}

	private void TaskManagerTabView_Unloaded(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		_refreshTimer.Stop();
		if (!_hardwareRefreshInProgress) _hardwareService.Close();
	}

	private async void RefreshTimer_Tick(object? sender, EventArgs e)
	{
		_ = sender;
		_ = e;
		await RefreshActiveViewAsync();
	}

	private async void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		await RefreshActiveViewAsync();
	}

	private async void ProcessesModeButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		SetPerformanceMode(false);
		await RefreshProcessesAsync();
	}

	private async void PerformanceModeButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		SetPerformanceMode(true);
		await RefreshHardwareAsync();
	}

	private void SetPerformanceMode(bool isPerformanceMode)
	{
		_isPerformanceMode = isPerformanceMode;
		ProcessesView.Visibility = isPerformanceMode ? Visibility.Collapsed : Visibility.Visible;
		PerformanceView.Visibility = isPerformanceMode ? Visibility.Visible : Visibility.Collapsed;
		ProcessesModeButton.SetResourceReference(BackgroundProperty, isPerformanceMode ? "TaskManagerModeIdleBrush" : "PanelAccentTintBrush");
		PerformanceModeButton.SetResourceReference(BackgroundProperty, isPerformanceMode ? "PanelAccentTintBrush" : "TaskManagerModeIdleBrush");
		AutomationProperties.SetName(RefreshButton, isPerformanceMode ? "Refresh hardware sensors" : "Refresh processes");
	}

	private Task RefreshActiveViewAsync() => _isPerformanceMode ? RefreshHardwareAsync() : RefreshProcessesAsync();

	private async Task RefreshProcessesAsync()
	{
		if (_refreshInProgress) return;
		_refreshInProgress = true;
		RefreshButton.IsEnabled = false;
		StatusText.Text = "Refreshing…";
		try
		{
			IReadOnlyList<TaskManagerProcessSnapshot> snapshots = await Task.Run(_processService.Capture);
			if (!IsLoaded) return;
			UpdateProcessItems(snapshots);
			long totalWorkingSet = snapshots.Sum(snapshot => snapshot.WorkingSetBytes);
			StatusText.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
			SummaryText.Text = snapshots.Count.ToString(CultureInfo.CurrentCulture) + " processes · " +
				(totalWorkingSet / 1024.0 / 1024.0 / 1024.0).ToString("N1", CultureInfo.CurrentCulture) + " GB working set";
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
			StatusText.Text = "Could not refresh processes: " + exception.Message;
		}
		finally
		{
			_refreshInProgress = false;
			RefreshButton.IsEnabled = true;
		}
	}

	private async Task RefreshHardwareAsync()
	{
		if (_hardwareRefreshInProgress) return;
		_hardwareRefreshInProgress = true;
		RefreshButton.IsEnabled = false;
		HardwareStatusText.Text = "Reading hardware sensors…";
		try
		{
			HardwarePerformanceSnapshot snapshot = await Task.Run(_hardwareService.Capture);
			if (!IsLoaded || !_isPerformanceMode) return;
			HardwareList.ItemsSource = snapshot.Items;
			CpuSummaryText.Text = snapshot.CpuSummary;
			MemorySummaryText.Text = snapshot.MemorySummary;
			GpuSummaryText.Text = snapshot.GpuSummary;
			StorageSummaryText.Text = snapshot.StorageSummary;
			TemperatureSummaryText.Text = snapshot.TemperatureSummary;
			FanSummaryText.Text = snapshot.FanSummary;
			UpdateTelemetryHistory(snapshot.Sample);
			HardwareStatusText.Text = snapshot.SensorAccessNote;
			HardwareUpdatedText.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.IO.IOException or System.Management.ManagementException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or DllNotFoundException or TypeInitializationException or TypeLoadException)
		{
			HardwareStatusText.Text = "Could not read hardware sensors: " + exception.Message;
		}
		finally
		{
			_hardwareRefreshInProgress = false;
			RefreshButton.IsEnabled = true;
			if (!IsLoaded) _hardwareService.Close();
		}
	}

	private void UpdateTelemetryHistory(HardwareTelemetrySample sample)
	{
		_cpuHistory.Add(sample.CpuPercent);
		_memoryHistory.Add(sample.MemoryPercent);
		_gpuHistory.Add(sample.GpuPercent);
		_storageHistory.Add(sample.StoragePercent);
		_temperatureHistory.Add(sample.TemperatureCelsius);
		_fanHistory.Add(sample.FanRpm);
		RedrawTelemetryCharts();
	}

	private void RedrawTelemetryCharts()
	{
		DrawTelemetryChart(CpuChartCanvas, CpuChartLine, _cpuHistory);
		DrawTelemetryChart(MemoryChartCanvas, MemoryChartLine, _memoryHistory);
		DrawTelemetryChart(GpuChartCanvas, GpuChartLine, _gpuHistory);
		DrawTelemetryChart(StorageChartCanvas, StorageChartLine, _storageHistory);
		DrawTelemetryChart(TemperatureChartCanvas, TemperatureChartLine, _temperatureHistory);
		DrawTelemetryChart(FanChartCanvas, FanChartLine, _fanHistory);
	}

	private static void DrawTelemetryChart(Canvas canvas, Polyline line, TelemetryHistorySeries history)
	{
		IReadOnlyList<double> values = history.NormalizedValues;
		double width = canvas.ActualWidth;
		double height = canvas.ActualHeight;
		if (values.Count == 0 || width <= 0.0 || height <= 0.0)
		{
			line.Points = [];
			return;
		}

		PointCollection points = [];
		if (values.Count == 1)
		{
			double y = 1.0 + (1.0 - values[0]) * Math.Max(0.0, height - 2.0);
			points.Add(new Point(0.0, y));
			points.Add(new Point(width, y));
		}
		else
		{
			for (int index = 0; index < values.Count; index++)
			{
				double x = width * index / (values.Count - 1.0);
				double y = 1.0 + (1.0 - values[index]) * Math.Max(0.0, height - 2.0);
				points.Add(new Point(x, y));
			}
		}
		line.Points = points;
	}

	private void UpdateProcessItems(IReadOnlyList<TaskManagerProcessSnapshot> snapshots)
	{
		TaskManagerProcessSnapshot? selectedSnapshot = (ProcessGrid.SelectedItem as ProcessItem)?.ToSnapshot();
		Dictionary<int, ProcessItem> existing = [];
		foreach (ProcessItem item in _processes.ToList())
		{
			if (!existing.TryAdd(item.ProcessId, item))
			{
				_processes.Remove(item);
			}
		}
		HashSet<int> active = snapshots.Select(snapshot => snapshot.ProcessId).ToHashSet();
		foreach (ProcessItem stale in _processes.Where(item => !active.Contains(item.ProcessId)).ToList())
		{
			_processes.Remove(stale);
		}
		foreach (TaskManagerProcessSnapshot snapshot in snapshots)
		{
			existing.TryGetValue(snapshot.ProcessId, out ProcessItem? item);
			TaskManagerProcessMergeAction action = TaskManagerProcessListPolicy.GetMergeAction(item?.ToSnapshot(), snapshot);
			if (action == TaskManagerProcessMergeAction.Update && item != null)
			{
				item.Update(snapshot);
			}
			else if (action == TaskManagerProcessMergeAction.Replace && item != null)
			{
				int index = _processes.IndexOf(item);
				ProcessItem replacement = new(snapshot);
				if (index >= 0) _processes[index] = replacement;
				else _processes.Add(replacement);
				existing[snapshot.ProcessId] = replacement;
			}
			else
			{
				ProcessItem added = new(snapshot);
				_processes.Add(added);
				existing[snapshot.ProcessId] = added;
			}
		}
		_processView.Refresh();
		if (selectedSnapshot != null)
		{
			ProcessGrid.SelectedItem = _processes.FirstOrDefault(item =>
				TaskManagerProcessListPolicy.GetMergeAction(selectedSnapshot, item.ToSnapshot()) == TaskManagerProcessMergeAction.Update);
		}
		UpdateSelectionActions();
	}

	private bool FilterProcess(object item)
	{
		if (item is not ProcessItem process) return false;
		string query = SearchTextBox.Text.Trim();
		return string.IsNullOrWhiteSpace(query) ||
			process.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
			process.WindowTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
			process.ProcessId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal);
	}

	private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
			? Visibility.Visible
			: Visibility.Collapsed;
		_processView?.Refresh();
	}

	private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		UpdateSelectionActions();
	}

	private void UpdateSelectionActions()
	{
		ProcessItem? selected = ProcessGrid.SelectedItem as ProcessItem;
		OpenLocationButton.IsEnabled = selected != null;
		EndTaskButton.IsEnabled = selected?.CanTerminate == true;
		if (selected == null)
		{
			SelectionTitleText.Text = "Select a process";
			SelectionDetailText.Text = "Choose a row to see its PID, status, window, and actions.";
			return;
		}
		SelectionTitleText.Text = selected.Name;
		string window = string.IsNullOrWhiteSpace(selected.WindowTitle) ? "No visible window" : selected.WindowTitle;
		SelectionDetailText.Text = "PID " + selected.ProcessId.ToString(CultureInfo.CurrentCulture) + " · " + selected.Status + " · " + window;
	}

	private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		if (ProcessGrid.SelectedItem is not ProcessItem selected) return;
		if (!_processService.TryGetExecutablePath(selected.ProcessId, out string path, out string error))
		{
			StatusText.Text = error;
			return;
		}
		try
		{
			ProcessStartInfo startInfo = new("explorer.exe") { UseShellExecute = false };
			startInfo.ArgumentList.Add("/select," + path);
			Process.Start(startInfo);
			StatusText.Text = "Opened " + selected.Name + " in File Explorer.";
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			StatusText.Text = "Could not open the file location: " + exception.Message;
		}
	}

	private async void EndTaskButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		if (ProcessGrid.SelectedItem is not ProcessItem selected || !selected.CanTerminate) return;
		EndTaskConfirmationWindow confirmation = new(selected.Name, selected.ProcessId)
		{
			Owner = Window.GetWindow(this)
		};
		if (confirmation.ShowDialog() != true) return;
		if (_processService.TryTerminate(selected.ToSnapshot(), out string error))
		{
			StatusText.Text = "End task requested for " + selected.Name + ".";
			await Task.Delay(250);
			await RefreshProcessesAsync();
		}
		else
		{
			StatusText.Text = error;
		}
	}
}
