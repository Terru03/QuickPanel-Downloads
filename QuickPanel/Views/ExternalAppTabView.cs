using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickPanel.Models;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class ExternalAppTabView : UserControl, IDisposable
{
	private readonly ExternalAppHost _host;

	private bool _dockInProgress;

	private bool _disposed;
	private nint _preferredWindowHandle;
	private bool _active;
	private bool _panelVisible = true;

	public ExternalAppTabView(ExternalAppDefinition definition, LogService? logService = null, nint preferredWindowHandle = default)
	{
		Definition = definition;
		_preferredWindowHandle = preferredWindowHandle;
		InitializeComponent();
		AppNameText.Text = definition.DisplayName;
		_host = new ExternalAppHost(definition, logService);
		_host.StateChanged += Host_StateChanged;
		HostContainer.Children.Add(_host);
		ApplyState(ExternalAppDockState.Idle, $"Ready to connect {definition.DisplayName}.");
	}

	public ExternalAppDefinition Definition { get; }

	public bool IsDocked => _host.IsDocked;

	public int ContentTopOffsetAt96Dpi => _host.ContentTopOffsetAt96Dpi;

	public void PreferWindow(nint windowHandle)
	{
		if (windowHandle != nint.Zero) Interlocked.Exchange(ref _preferredWindowHandle, windowHandle);
	}

	public bool FocusApplication() => _host.FocusDockedWindow();

	public void RefreshApplication() => _host.RefreshDockedWindow();

	public void SetContentTopOffset(int pixelsAt96Dpi) => _host.SetContentTopOffset(pixelsAt96Dpi);

	public void SetActive(bool active)
	{
		_active = active;
		Width = active ? double.NaN : 0.0;
		Height = active ? double.NaN : 0.0;
		HorizontalAlignment = active ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
		VerticalAlignment = active ? VerticalAlignment.Stretch : VerticalAlignment.Top;
		Opacity = active ? 1.0 : 0.0;
		IsHitTestVisible = active;
		Panel.SetZIndex(this, active ? 1 : 0);
		UpdateHostVisibility();
	}

	public void SetPanelVisible(bool visible)
	{
		_panelVisible = visible;
		UpdateHostVisibility();
	}

	public async Task WarmUpAsync()
	{
		if (_dockInProgress || _disposed) return;
		if (IsDocked)
		{
			_host.RefreshDockedWindow();
			return;
		}
		_dockInProgress = true;
		SetBusy(true, "Starting...");
		try
		{
			if (!await _host.EnsureRunningAsync())
			{
				ApplyState(ExternalAppDockState.UnableToEmbed, _host.LastError ?? $"{Definition.DisplayName} could not be started.");
			}
		}
		finally
		{
			_dockInProgress = false;
			SetBusy(false, "Retry");
		}
	}

	public async Task DockAsync(bool useForegroundWindow = false)
	{
		if (_dockInProgress || _disposed) return;
		if (IsDocked)
		{
			_host.RefreshDockedWindow();
			return;
		}
		_dockInProgress = true;
		SetBusy(true, "Connecting...");
		try
		{
			if (!await WaitForHostAsync())
			{
				ApplyState(ExternalAppDockState.UnableToEmbed, $"{Definition.DisplayName} tab is not ready yet. Click Retry.");
				return;
			}
			nint preferredWindowHandle = useForegroundWindow
				? nint.Zero
				: Interlocked.CompareExchange(ref _preferredWindowHandle, nint.Zero, nint.Zero);
			bool connected = preferredWindowHandle != nint.Zero
				? await _host.DockWindowAsync(preferredWindowHandle)
				: await _host.DockAsync(useForegroundWindow);
			if (connected)
			{
				DockOverlay.Visibility = Visibility.Collapsed;
				UpdateHostVisibility();
				await Dispatcher.InvokeAsync(() => _host.FocusDockedWindow(), DispatcherPriority.Input);
			}
			else
			{
				ApplyState(ExternalAppDockState.UnableToEmbed, _host.LastError ?? $"Unable to embed {Definition.DisplayName}.");
			}
		}
		finally
		{
			_dockInProgress = false;
			SetBusy(false, "Retry");
		}
	}

	private async Task<bool> WaitForHostAsync()
	{
		for (int attempt = 0; attempt < 8 && !_disposed; attempt++)
		{
			if (_host.IsHostReady) return true;
			await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			if (_host.IsHostReady) return true;
			await Task.Delay(25);
		}
		return _host.IsHostReady;
	}

	public void Undock()
	{
		_host.Undock();
		ApplyState(ExternalAppDockState.Disconnected, $"{Definition.DisplayName} is open as a separate desktop window.");
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_host.StateChanged -= Host_StateChanged;
		_host.Shutdown();
		GC.SuppressFinalize(this);
	}

	private async void DockButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		await DockAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
	}

	private void Host_StateChanged(object? sender, ExternalAppDockStateChangedEventArgs e)
	{
		_ = sender;
		if (_disposed) return;
		_ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ApplyState(e.State, e.Message)));
	}

	private void ApplyState(ExternalAppDockState state, string message)
	{
		if (_disposed) return;
		StateText.Text = state switch
		{
			ExternalAppDockState.Launching => "Launching...",
			ExternalAppDockState.Searching => "Searching for application window...",
			ExternalAppDockState.Connected => "Connected",
			ExternalAppDockState.ApplicationClosed => "Application closed",
			ExternalAppDockState.UnableToEmbed => "Unable to embed application",
			ExternalAppDockState.Disconnected => "Disconnected",
			ExternalAppDockState.ShuttingDown => "Shutting down",
			_ => "Ready"
		};
		DockMessage.Text = message;
		DockOverlay.Visibility = state == ExternalAppDockState.Connected ? Visibility.Collapsed : Visibility.Visible;
		DockButton.Content = state is ExternalAppDockState.Idle ? "Connect" : "Retry";
		UpdateHostVisibility();
	}

	private void UpdateHostVisibility()
	{
		_host.SetVisible(_active && _panelVisible && _host.IsDocked);
	}

	private void SetBusy(bool busy, string buttonText)
	{
		DockButton.IsEnabled = !busy;
		DockButton.Content = buttonText;
		if (busy)
		{
			DockOverlay.Visibility = Visibility.Visible;
		}
		else
		{
			ApplyState(_host.State, _host.StateMessage);
		}
	}
}
