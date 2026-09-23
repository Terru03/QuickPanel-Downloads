using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using QuickPanel.Models;
using QuickPanel.Services;

namespace QuickPanel.Views;

public sealed class ExternalAppHost : HwndHost
{
	private readonly ExternalAppDockService _dockService;

	private nint _hostHandle;

	public ExternalAppHost(ExternalAppDefinition definition, LogService? logService = null)
	{
		_dockService = new ExternalAppDockService(definition, logService: logService);
	}

	public event EventHandler<ExternalAppDockStateChangedEventArgs>? StateChanged
	{
		add => _dockService.StateChanged += value;
		remove => _dockService.StateChanged -= value;
	}

	public ExternalAppDockState State => _dockService.State;

	public string StateMessage => _dockService.StateMessage;

	public bool IsDocked => _dockService.IsDocked;

	public bool IsHostReady => _hostHandle != nint.Zero && ExternalAppNativeMethods.IsValidWindow(_hostHandle);

	public string? LastError => _dockService.LastError;

	public string LogPath => _dockService.LogPath;

	public int ContentTopOffsetAt96Dpi => _dockService.ContentTopOffsetAt96Dpi;

	public Task<bool> DockAsync(bool useForegroundWindow = false, CancellationToken cancellationToken = default)
	{
		return _dockService.DockAsync(useForegroundWindow, cancellationToken);
	}

	public Task<bool> DockWindowAsync(nint windowHandle, CancellationToken cancellationToken = default)
	{
		return _dockService.DockWindowAsync(windowHandle, cancellationToken);
	}

	public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
	{
		return _dockService.EnsureRunningAsync(cancellationToken);
	}

	public void Undock() => _dockService.Undock();

	public void RefreshDockedWindow() => _dockService.Refresh();

	public void SetVisible(bool visible) => _dockService.SetHostVisible(visible);

	public void SetContentTopOffset(int pixelsAt96Dpi) => _dockService.SetContentTopOffset(pixelsAt96Dpi);

	public bool FocusDockedWindow() => _dockService.FocusDockedWindow();

	public void Shutdown() => _dockService.Dispose();

	protected override HandleRef BuildWindowCore(HandleRef parentWindow)
	{
		_hostHandle = ExternalAppNativeMethods.CreateHostWindow(parentWindow.Handle);
		_dockService.AttachHost(_hostHandle);
		return new HandleRef(this, _hostHandle);
	}

	protected override void DestroyWindowCore(HandleRef window)
	{
		_dockService.DetachHost(window.Handle);
		ExternalAppNativeMethods.DestroyHostWindow(window.Handle);
		_hostHandle = nint.Zero;
	}

	protected override void OnWindowPositionChanged(Rect bounds)
	{
		base.OnWindowPositionChanged(bounds);
		_dockService.Resize();
	}

	protected override bool TabIntoCore(TraversalRequest request)
	{
		_ = request;
		return _dockService.FocusDockedWindow();
	}
}
