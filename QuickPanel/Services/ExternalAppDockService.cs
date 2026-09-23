using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed class ExternalAppDockService : IDisposable
{
	private static readonly TimeSpan HealthInterval = TimeSpan.FromMilliseconds(2500);

	private readonly ExternalAppDefinition _definition;
	private readonly ExternalAppWindowFinder _windowFinder;
	private readonly ExternalAppLauncher _launcher;
	private readonly LogService _logService;
	private readonly SemaphoreSlim _dockGate = new(1, 1);
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly object _operationLock = new();
	private readonly object _stateLock = new();
	private readonly ExternalAppNativeMethods.WinEventDelegate _foregroundEventDelegate;

	private CancellationTokenSource? _activeOperation;
	private Timer? _healthTimer;
	private nint _hostHandle;
	private nint _dockedHandle;
	private nint _lastExternalForegroundWindow;
	private nint _foregroundHook;
	private ExternalAppNativeMethods.WindowSnapshot? _snapshot;
	private uint _hostThreadId;
	private uint _dockedThreadId;
	private bool _inputThreadsAttached;
	private bool _hostVisible;
	private bool _reparentSuspended;
	private bool _disposed;
	private int _dockVersion;
	private uint _ownedProcessId;
	private DateTime? _ownedProcessStartTimeUtc;
	private bool _overlayDocked;
	private int _lastDockedWidth = -1;
	private int _lastDockedHeight = -1;
	private int _lastDockedTopOffset = -1;
	private int _contentTopOffsetAt96Dpi;
	private ExternalAppDockState _state = ExternalAppDockState.Idle;
	private string _stateMessage = "Ready.";

	private bool UsesMinimizedOverlayVisibility =>
		_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay &&
		(string.Equals(_definition.WindowClassName, "Notepad", StringComparison.OrdinalIgnoreCase) ||
			ContainsWindowsNotepadIdentity(_definition.PackageIdentity) ||
			ContainsWindowsNotepadIdentity(_definition.AppUserModelId));

	public ExternalAppDockService(
		ExternalAppDefinition definition,
		ExternalAppWindowFinder? windowFinder = null,
		ExternalAppLauncher? launcher = null,
		LogService? logService = null)
	{
		_definition = definition;
		_windowFinder = windowFinder ?? new ExternalAppWindowFinder();
		_logService = logService ?? new LogService();
		_launcher = launcher ?? new ExternalAppLauncher(Log);
		_foregroundEventDelegate = ForegroundWindowChanged;
		_contentTopOffsetAt96Dpi = definition.ContentTopOffsetAt96Dpi;
		_foregroundHook = ExternalAppNativeMethods.InstallForegroundHook(_foregroundEventDelegate);
		RememberExternalForegroundWindow(ExternalAppNativeMethods.GetForeground());
		Log($"service-start ownPid={Environment.ProcessId} foregroundHook={FormatHandle(_foregroundHook)}");
	}

	public event EventHandler<ExternalAppDockStateChangedEventArgs>? StateChanged;

	public ExternalAppDefinition Definition => _definition;

	public ExternalAppDockState State
	{
		get
		{
			lock (_stateLock)
			{
				return _state;
			}
		}
	}

	public string StateMessage
	{
		get
		{
			lock (_stateLock)
			{
				return _stateMessage;
			}
		}
	}

	public bool IsDocked =>
		_dockedHandle != nint.Zero &&
		ExternalAppNativeMethods.IsValidWindow(_dockedHandle) &&
		(_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay
			? _overlayDocked
			: (ExternalAppNativeMethods.GetWindowParent(_dockedHandle) == _hostHandle ||
				(_reparentSuspended && _snapshot != null && ExternalAppNativeMethods.GetWindowParent(_dockedHandle) == _snapshot.Parent)));

	public string? LastError { get; private set; }

	public string LogPath => _logService.CurrentLogPath;

	public int ContentTopOffsetAt96Dpi => _contentTopOffsetAt96Dpi;

	public void SetContentTopOffset(int pixelsAt96Dpi)
	{
		_contentTopOffsetAt96Dpi = Math.Clamp(pixelsAt96Dpi, 0, 96);
		_lastDockedTopOffset = -1;
		if (IsDocked) ResizeDockedWindow(nudgeCompositor: true);
		Log("content-top-crop pixels96=" + _contentTopOffsetAt96Dpi);
	}

	public void AttachHost(nint hostHandle)
	{
		_hostHandle = hostHandle;
		ExternalAppNativeMethods.Show(_hostHandle, _hostVisible ? ExternalAppNativeMethods.SwShow : ExternalAppNativeMethods.SwHide);
		Log("host-attached hwnd=" + FormatHandle(hostHandle));
	}

	public void DetachHost(nint hostHandle)
	{
		if (_hostHandle != hostHandle) return;
		_hostVisible = false;
		Interlocked.Increment(ref _dockVersion);
		CancelActiveOperation();
		StopHealthMonitor();
		if (_dockedHandle != nint.Zero)
		{
			TryRestoreWindow();
			ClearDockState();
		}
		_hostHandle = nint.Zero;
		if (!_disposed)
		{
			SetState(ExternalAppDockState.Disconnected, $"{_definition.DisplayName} host was rebuilt. Click Retry to reconnect.");
		}
		Log("host-detached hwnd=" + FormatHandle(hostHandle));
	}

	public void SetHostVisible(bool visible)
	{
		_hostVisible = visible;
		if (!visible)
		{
			if (IsDocked)
			{
				DetachInputThreads();
				if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent)
				{
					SuspendReparentedWindow();
					if (_hostHandle != nint.Zero) ExternalAppNativeMethods.Show(_hostHandle, ExternalAppNativeMethods.SwHide);
					Log("host-visibility minimized-top-level");
					return;
				}
				else
				{
					ExternalAppNativeMethods.Show(
						_dockedHandle,
						UsesMinimizedOverlayVisibility ? ExternalAppNativeMethods.SwMinimize : ExternalAppNativeMethods.SwHide);
					if (UsesMinimizedOverlayVisibility) Log("host-visibility minimized-overlay");
				}
			}
			if (_hostHandle != nint.Zero) ExternalAppNativeMethods.Show(_hostHandle, ExternalAppNativeMethods.SwHide);
			Log("host-visibility hidden");
			return;
		}
		if (_hostHandle == nint.Zero) return;
		if (IsDocked && _definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent)
		{
			ExternalAppNativeMethods.Show(_hostHandle, ExternalAppNativeMethods.SwShow);
			ResumeReparentedWindow();
		}
		else
		{
			ExternalAppNativeMethods.SetWindowPosition(
				_hostHandle,
				0,
				0,
				0,
				0,
				ExternalAppNativeMethods.SwpNoActivate |
				ExternalAppNativeMethods.SwpNoMove |
				ExternalAppNativeMethods.SwpNoSize |
				ExternalAppNativeMethods.SwpShowWindow);
		}
		if (!IsDocked)
		{
			Log("host-visibility shown");
			return;
		}
		if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay)
		{
			ExternalAppNativeMethods.Show(
				_dockedHandle,
				UsesMinimizedOverlayVisibility ? ExternalAppNativeMethods.SwShowNoActivate : ExternalAppNativeMethods.SwShow);
		}
		// A client-drawn reparented window can miss its first composition pass after it
		// returns to the native host. Nudge its width after resuming it so the app paints
		// immediately instead of waiting for a manual panel resize.
		Refresh(nudgeCompositor: _definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent);
		Log("host-visibility shown");
	}

	public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource operation = BeginOperation(cancellationToken);
		try
		{
			await _dockGate.WaitAsync(operation.Token);
			try
			{
				if (_disposed) return Fail("Dock service is shutting down.");
				LastError = null;
				if (IsDocked)
				{
					Refresh();
					return true;
				}
				ExternalAppWindowMatch? match = await FindOrLaunchWindowAsync(operation.Token);
				if (match == null) return false;
				LogCandidate("warm-up-ready", match);
				SetState(ExternalAppDockState.Disconnected, $"{_definition.DisplayName} is open and ready to connect.");
				return true;
			}
			finally
			{
				_dockGate.Release();
			}
		}
		catch (OperationCanceledException)
		{
			return Cancelled();
		}
		finally
		{
			EndOperation(operation);
		}
	}

	public Task<bool> DockAsync(bool useForegroundWindow = false, CancellationToken cancellationToken = default)
	{
		return DockAsyncCore(useForegroundWindow, nint.Zero, cancellationToken);
	}

	public Task<bool> DockWindowAsync(nint windowHandle, CancellationToken cancellationToken = default)
	{
		return DockAsyncCore(useForegroundWindow: false, windowHandle, cancellationToken);
	}

	private async Task<bool> DockAsyncCore(bool useForegroundWindow, nint windowHandle, CancellationToken cancellationToken)
	{
		using CancellationTokenSource operation = BeginOperation(cancellationToken);
		try
		{
			await _dockGate.WaitAsync(operation.Token);
			try
			{
				if (_disposed) return Fail("Dock service is shutting down.");
				if (_definition.PreferredDockingBehavior is not (ExternalAppDockingBehavior.Reparent or ExternalAppDockingBehavior.Overlay))
				{
					return Fail("This docking mode is not available yet.");
				}
				if (_hostHandle == nint.Zero || !ExternalAppNativeMethods.IsValidWindow(_hostHandle))
				{
					return Fail("External application host is not ready.");
				}
				int operationVersion = Volatile.Read(ref _dockVersion);
				if (IsDocked)
				{
					Refresh();
					return true;
				}
				LastError = null;
				if (windowHandle != nint.Zero)
				{
					SetState(ExternalAppDockState.Searching, "Checking selected application window...");
				}
				else if (useForegroundWindow)
				{
					SetState(ExternalAppDockState.Searching, "Checking focused application window...");
				}
				ExternalAppWindowMatch? selected = windowHandle != nint.Zero
					? GetSelectedWindowCandidate(windowHandle)
					: useForegroundWindow
						? GetManualForegroundCandidate()
						: await FindOrLaunchWindowAsync(operation.Token);
				if (selected == null) return false;
				operation.Token.ThrowIfCancellationRequested();
				if (operationVersion != Volatile.Read(ref _dockVersion)) return Cancelled();
				LogCandidate("selected", selected);
				try
				{
					DockWindow(selected.Candidate);
					if (!await StabilizeDockedWindowAsync(operationVersion, operation.Token))
					{
						TryRestoreWindow();
						ClearDockState();
						return Fail($"{_definition.DisplayName} opened, but its window did not render correctly inside the panel.");
					}
					StartHealthMonitor();
					SetState(ExternalAppDockState.Connected, "Connected");
					Log($"dock-success hwnd={FormatHandle(selected.Candidate.Handle)} pid={selected.Candidate.ProcessId} manual={useForegroundWindow} picked={windowHandle != nint.Zero}");
					return true;
				}
				catch (Win32Exception exception)
				{
					TryRestoreWindow();
					ClearDockState();
					string detail = exception.NativeErrorCode == 5
						? "Windows denied access. The application may run elevated while Quick Panel does not."
						: exception.Message;
					return Fail("Windows could not dock the selected window: " + detail);
				}
			}
			finally
			{
				_dockGate.Release();
			}
		}
		catch (OperationCanceledException)
		{
			return Cancelled();
		}
		finally
		{
			EndOperation(operation);
		}
	}

	private ExternalAppWindowMatch? GetSelectedWindowCandidate(nint windowHandle)
	{
		nint handle = ExternalAppNativeMethods.GetRoot(windowHandle);
		if (handle == nint.Zero || !ExternalAppNativeMethods.IsValidWindow(handle))
		{
			return FailMatch("The selected window is no longer open. Refresh the window picker and try again.");
		}
		ExternalAppWindowMatch match = _windowFinder.EvaluateManual(_definition, handle);
		if (ExternalAppDefinitionFactory.TryReadWindowId(_definition.Id, out uint expectedProcessId, out nint expectedHandle) &&
			(match.Candidate.ProcessId != expectedProcessId || handle != expectedHandle))
		{
			return FailMatch("The selected window closed or changed. Open the window picker and choose it again.");
		}
		WindowDockAssessment assessment = ExternalAppWindowPickerPolicy.Evaluate(match.Candidate, Environment.ProcessId);
		if (!assessment.IsDockable)
		{
			return FailMatch("The selected window cannot be docked: " + assessment.Reason);
		}
		LogCandidate("picked", match);
		return match.IsValid ? match : FailMatch("The selected window cannot be docked: " + match.RejectionReason);
	}

	public void Resize()
	{
		if (!IsDocked || _reparentSuspended) return;
		try
		{
			ResizeDockedWindow();
		}
		catch (Win32Exception exception)
		{
			Log("resize-failed error=" + exception.Message);
		}
	}

	public void Refresh(bool nudgeCompositor = false)
	{
		if (!IsDocked || _reparentSuspended) return;
		try
		{
			ResizeDockedWindow(nudgeCompositor);
			ExternalAppNativeMethods.RequestRedraw(_dockedHandle);
		}
		catch (Win32Exception exception)
		{
			Log("refresh-failed error=" + exception.Message);
		}
	}

	private void SuspendReparentedWindow()
	{
		if (_reparentSuspended || _dockedHandle == nint.Zero || _snapshot == null) return;
		ExternalAppNativeMethods.SetWindowParent(_dockedHandle, _snapshot.Parent);
		ExternalAppNativeMethods.SetStyle(_dockedHandle, ExternalAppNativeMethods.GwlStyle, _snapshot.Style);
		ExternalAppNativeMethods.SetStyle(_dockedHandle, ExternalAppNativeMethods.GwlExtendedStyle, _snapshot.ExtendedStyle);
		ExternalAppNativeMethods.SetWindowPosition(
			_dockedHandle,
			0,
			0,
			0,
			0,
			ExternalAppNativeMethods.SwpNoActivate |
			ExternalAppNativeMethods.SwpNoMove |
			ExternalAppNativeMethods.SwpNoSize |
			ExternalAppNativeMethods.SwpNoZOrder |
			ExternalAppNativeMethods.SwpFrameChanged);
		if (_snapshot.HasPlacement) ExternalAppNativeMethods.RestorePlacement(_dockedHandle, _snapshot.Placement);
		_reparentSuspended = true;
		ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwMinimize);
	}

	private void ResumeReparentedWindow()
	{
		if (!_reparentSuspended || _hostHandle == nint.Zero || _dockedHandle == nint.Zero || _snapshot == null) return;
		// Modern packaged apps can tear down their HWND when a minimized child window is
		// restored. Restore it while it still has its original top-level parent and style,
		// then immediately put the live window back into the native host.
		ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwRestore);
		ApplyReparentedWindowStyle();
		ExternalAppNativeMethods.SetWindowParent(_dockedHandle, _hostHandle);
		_reparentSuspended = false;
		_lastDockedWidth = -1;
		_lastDockedHeight = -1;
		_lastDockedTopOffset = -1;
		ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwShow);
	}

	public bool FocusDockedWindow()
	{
		if (!IsDocked || _reparentSuspended) return false;
		AttachInputThreads();
		try
		{
			return ExternalAppNativeMethods.FocusWindow(_hostHandle, _dockedHandle);
		}
		finally
		{
			DetachInputThreads();
		}
	}

	public void Undock()
	{
		Interlocked.Increment(ref _dockVersion);
		CancelActiveOperation();
		StopHealthMonitor();
		nint handle = _dockedHandle;
		if (handle != nint.Zero)
		{
			TryRestoreWindow();
			ClearDockState();
			Log("undock-complete hwnd=" + FormatHandle(handle));
		}
		if (!_disposed)
		{
			SetState(ExternalAppDockState.Disconnected, $"{_definition.DisplayName} is open as a separate desktop window.");
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		SetState(ExternalAppDockState.ShuttingDown, "Shutting down.");
		_lifetimeCancellation.Cancel();
		Undock();
		TryTerminateOwnedProcess();
		ExternalAppNativeMethods.RemoveEventHook(_foregroundHook);
		_foregroundHook = nint.Zero;
		_lifetimeCancellation.Dispose();
		Log("service-stop");
	}

	private async Task<ExternalAppWindowMatch?> FindOrLaunchWindowAsync(CancellationToken cancellationToken)
	{
		SetState(ExternalAppDockState.Searching, $"Searching for {_definition.DisplayName} window...");
		ExternalAppWindowMatch? existing = _windowFinder.FindBest(_definition);
		if (existing != null)
		{
			LogCandidate("existing", existing);
			return existing;
		}
		if (!_definition.LaunchAutomatically)
		{
			return FailMatch($"{_definition.DisplayName} is not running. Open it, then retry.");
		}
		HashSet<nint> baseline = _windowFinder.CaptureTopLevelHandles();
		SetState(ExternalAppDockState.Launching, $"Launching {_definition.DisplayName}...");
		ExternalAppLaunchResult launch = await _launcher.LaunchAsync(_definition, cancellationToken);
		if (!launch.Success)
		{
			return FailMatch($"{_definition.DisplayName} could not be launched: {launch.Error}");
		}
		TrackOwnedProcess(launch.OwnedProcessId);
		Log($"launch-request method=\"{launch.Description}\" activatedPid={launch.Identity?.ActivatedProcessId ?? 0} baselineCount={baseline.Count}");
		SetState(ExternalAppDockState.Searching, $"Searching for {_definition.DisplayName} window...");

		long deadline = Environment.TickCount64 + _definition.LaunchTimeoutMs;
		int delayMs = 200;
		nint lastHandle = nint.Zero;
		int stableScans = 0;
		while (Environment.TickCount64 < deadline)
		{
			await Task.Delay(delayMs, cancellationToken);
			ExternalAppWindowMatch? match = _windowFinder.FindBest(_definition, baseline, launch.Identity);
			if (match == null)
			{
				lastHandle = nint.Zero;
				stableScans = 0;
			}
			else
			{
				bool ready = !_definition.RequireRenderedSurface ||
					(match.Candidate.HasRenderedSurface && ExternalAppNativeMethods.IsResponsive(match.Candidate.Handle));
				if (ready && match.Candidate.Handle == lastHandle) stableScans++;
				else if (ready)
				{
					lastHandle = match.Candidate.Handle;
					stableScans = 1;
				}
				else
				{
					lastHandle = match.Candidate.Handle;
					stableScans = 0;
				}
				if (stableScans >= (_definition.RequireRenderedSurface ? 4 : 2))
				{
					LogCandidate("post-launch", match);
					return match;
				}
			}
			delayMs = Math.Min(750, delayMs + 75);
		}
		IReadOnlyList<ExternalAppWindowMatch> matches = _windowFinder.EnumerateMatches(_definition, baseline, launch.Identity);
		LogCandidateSet("launch-timeout", matches);
		return FailMatch($"{_definition.DisplayName} started, but Quick Panel could not identify a usable window.");
	}

	private ExternalAppWindowMatch? GetManualForegroundCandidate()
	{
		nint handle = ExternalAppNativeMethods.GetForeground();
		if (ExternalAppNativeMethods.GetWindowProcessId(handle) == Environment.ProcessId)
		{
			handle = _lastExternalForegroundWindow;
		}
		handle = ExternalAppNativeMethods.GetRoot(handle);
		if (handle == nint.Zero)
		{
			return FailMatch("No external foreground window is available. Focus the application, return here, then Shift+click Retry.");
		}
		ExternalAppWindowMatch match = _windowFinder.EvaluateManual(_definition, handle);
		WindowDockAssessment assessment = ExternalAppWindowPickerPolicy.Evaluate(match.Candidate, Environment.ProcessId);
		if (!assessment.IsDockable)
		{
			return FailMatch("The focused window cannot be docked: " + assessment.Reason);
		}
		LogCandidate("manual", match);
		return match.IsValid ? match : FailMatch("The focused window cannot be docked: " + match.RejectionReason);
	}

	private void DockWindow(ExternalAppWindowCandidate candidate)
	{
		_dockedHandle = candidate.Handle;
		_hostThreadId = ExternalAppNativeMethods.GetWindowThreadId(_hostHandle);
		_dockedThreadId = ExternalAppNativeMethods.GetWindowThreadId(_dockedHandle);
		_snapshot = ExternalAppNativeMethods.CaptureWindow(_dockedHandle);
		if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay)
		{
			DockOverlayWindow();
			return;
		}
		ApplyReparentedWindowStyle();
		ExternalAppNativeMethods.SetWindowParent(_dockedHandle, _hostHandle);
		_reparentSuspended = false;
		ResizeDockedWindow(nudgeCompositor: true);
		ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwShow);
		if (_hostVisible)
		{
			Refresh();
			FocusDockedWindow();
		}
	}

	private void ApplyReparentedWindowStyle()
	{
		if (_snapshot == null || _dockedHandle == nint.Zero) return;
		long style = _snapshot.Style.ToInt64();
		style &= ~(ExternalAppNativeMethods.WsPopup |
			ExternalAppNativeMethods.WsCaption |
			ExternalAppNativeMethods.WsThickFrame |
			ExternalAppNativeMethods.WsMinimize |
			ExternalAppNativeMethods.WsMaximize);
		style |= ExternalAppNativeMethods.WsChild |
			ExternalAppNativeMethods.WsVisible |
			ExternalAppNativeMethods.WsClipChildren |
			ExternalAppNativeMethods.WsClipSiblings;
		long extendedStyle = _snapshot.ExtendedStyle.ToInt64();
		extendedStyle &= ~ExternalAppNativeMethods.WsExtendedAppWindow;
		ExternalAppNativeMethods.SetStyle(_dockedHandle, ExternalAppNativeMethods.GwlStyle, new nint(style));
		ExternalAppNativeMethods.SetStyle(_dockedHandle, ExternalAppNativeMethods.GwlExtendedStyle, new nint(extendedStyle));
	}

	private void DockOverlayWindow()
	{
		if (_snapshot == null) return;
		nint owner = ExternalAppNativeMethods.GetRoot(_hostHandle);
		if (owner != nint.Zero)
		{
			// Keep the non-activating overlay above Quick Panel without making it a child
			// HWND. The original owner is restored when the tab is undocked.
			ExternalAppNativeMethods.SetWindowOwner(_dockedHandle, owner);
		}
		if (!UsesMinimizedOverlayVisibility)
		{
			ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwHide);
		}
		_overlayDocked = true;
		ResizeOverlayWindow();
		ExternalAppNativeMethods.Show(
			_dockedHandle,
			_hostVisible
				? UsesMinimizedOverlayVisibility ? ExternalAppNativeMethods.SwShowNoActivate : ExternalAppNativeMethods.SwShow
				: UsesMinimizedOverlayVisibility ? ExternalAppNativeMethods.SwMinimize : ExternalAppNativeMethods.SwHide);
		if (_hostVisible)
		{
			Refresh();
			FocusDockedWindow();
		}
	}

	private async Task<bool> StabilizeDockedWindowAsync(int operationVersion, CancellationToken cancellationToken)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			if (!IsDocked || operationVersion != Volatile.Read(ref _dockVersion)) return false;
			ResizeDockedWindow(nudgeCompositor: attempt == 0);
			await Task.Delay(60, cancellationToken);
		}
		Refresh();
		return IsDocked &&
			operationVersion == Volatile.Read(ref _dockVersion) &&
			(!_definition.RequireRenderedSurface || ExternalAppNativeMethods.HasRenderedSurface(_dockedHandle, requireVisible: false));
	}

	private void ResizeDockedWindow(bool nudgeCompositor = false)
	{
		if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay)
		{
			ResizeOverlayWindow(nudgeCompositor);
			return;
		}
		if (!IsDocked || !ExternalAppNativeMethods.TryGetClientBounds(_hostHandle, out ExternalAppNativeMethods.NativeRect bounds)) return;
		int width = Math.Max(1, bounds.Right - bounds.Left);
		int hostHeight = Math.Max(1, bounds.Bottom - bounds.Top);
		int topOffset = (int)Math.Round(_contentTopOffsetAt96Dpi * ExternalAppNativeMethods.GetDpi(_hostHandle) / 96.0);
		int height = hostHeight + topOffset;
		if (!nudgeCompositor && width == _lastDockedWidth && height == _lastDockedHeight && topOffset == _lastDockedTopOffset)
		{
			return;
		}
		RestoreDockedWindowState();
		uint flags = ExternalAppNativeMethods.SwpNoActivate | ExternalAppNativeMethods.SwpNoZOrder | ExternalAppNativeMethods.SwpFrameChanged;
		if (_hostVisible) flags |= ExternalAppNativeMethods.SwpShowWindow;
		if (nudgeCompositor && width > 2)
		{
			ExternalAppNativeMethods.SetWindowPosition(_dockedHandle, 0, -topOffset, width - 1, height, flags);
		}
		ExternalAppNativeMethods.SetWindowPosition(_dockedHandle, 0, -topOffset, width, height, flags);
		_lastDockedWidth = width;
		_lastDockedHeight = height;
		_lastDockedTopOffset = topOffset;
	}

	private void ResizeOverlayWindow(bool nudgeCompositor = false)
	{
		if (!IsDocked || !ExternalAppNativeMethods.TryGetWindowBounds(_hostHandle, out ExternalAppNativeMethods.NativeRect bounds)) return;
		if (!_hostVisible && UsesMinimizedOverlayVisibility)
		{
			if (!ExternalAppNativeMethods.IsMinimized(_dockedHandle))
			{
				ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwMinimize);
			}
			return;
		}
		RestoreDockedWindowState();
		int width = Math.Max(1, bounds.Right - bounds.Left);
		int height = Math.Max(1, bounds.Bottom - bounds.Top);
		uint flags = ExternalAppNativeMethods.SwpNoActivate | ExternalAppNativeMethods.SwpFrameChanged;
		if (_hostVisible) flags |= ExternalAppNativeMethods.SwpShowWindow;
		nint owner = ExternalAppNativeMethods.GetRoot(_hostHandle);
		bool ownerIsTopmost = owner != nint.Zero &&
			(ExternalAppNativeMethods.GetStyle(owner, ExternalAppNativeMethods.GwlExtendedStyle).ToInt64() & ExternalAppNativeMethods.WsExtendedTopmost) != 0;
		nint insertAfter = ownerIsTopmost ? ExternalAppNativeMethods.HwndTopmost : ExternalAppNativeMethods.HwndTop;
		_ = nudgeCompositor;
		ExternalAppNativeMethods.SetWindowPosition(_dockedHandle, insertAfter, bounds.Left, bounds.Top, width, height, flags);
		if (!_hostVisible) ExternalAppNativeMethods.Show(_dockedHandle, ExternalAppNativeMethods.SwHide);
	}

	private void RestoreDockedWindowState()
	{
		if (!IsDocked) return;
		if (ExternalAppNativeMethods.IsMinimized(_dockedHandle) || ExternalAppNativeMethods.IsMaximized(_dockedHandle))
		{
			ExternalAppNativeMethods.Show(
				_dockedHandle,
				UsesMinimizedOverlayVisibility ? ExternalAppNativeMethods.SwShowNoActivate : ExternalAppNativeMethods.SwRestore);
		}
		if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay) return;
		long style = ExternalAppNativeMethods.GetStyle(_dockedHandle, ExternalAppNativeMethods.GwlStyle).ToInt64();
		long expected = style & ~(ExternalAppNativeMethods.WsMinimize |
			ExternalAppNativeMethods.WsMaximize |
			ExternalAppNativeMethods.WsCaption |
			ExternalAppNativeMethods.WsThickFrame);
		expected &= ~ExternalAppNativeMethods.WsPopup;
		expected |= ExternalAppNativeMethods.WsChild |
			ExternalAppNativeMethods.WsVisible |
			ExternalAppNativeMethods.WsClipChildren |
			ExternalAppNativeMethods.WsClipSiblings;
		if (style != expected)
		{
			ExternalAppNativeMethods.SetStyle(_dockedHandle, ExternalAppNativeMethods.GwlStyle, new nint(expected));
		}
	}

	private void TryRestoreWindow()
	{
		nint handle = _dockedHandle;
		ExternalAppNativeMethods.WindowSnapshot? snapshot = _snapshot;
		if (!ExternalAppNativeMethods.IsValidWindow(handle) || snapshot == null) return;
		try
		{
			DetachInputThreads();
			if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay)
			{
				if (!UsesMinimizedOverlayVisibility)
				{
					ExternalAppNativeMethods.Show(handle, ExternalAppNativeMethods.SwHide);
				}
				RestoreOverlayOwner(handle, snapshot.Parent);
			}
			else
			{
				ExternalAppNativeMethods.SetWindowParent(handle, snapshot.Parent);
				_reparentSuspended = false;
			}
			if (_definition.RestoreOriginalWindowOnUndock)
			{
				ExternalAppNativeMethods.SetStyle(handle, ExternalAppNativeMethods.GwlStyle, snapshot.Style);
				ExternalAppNativeMethods.SetStyle(handle, ExternalAppNativeMethods.GwlExtendedStyle, snapshot.ExtendedStyle);
				nint restoreInsertAfter = (snapshot.ExtendedStyle.ToInt64() & ExternalAppNativeMethods.WsExtendedTopmost) != 0
					? ExternalAppNativeMethods.HwndTopmost
					: ExternalAppNativeMethods.HwndNoTopmost;
				ExternalAppNativeMethods.SetWindowPosition(
					handle,
					restoreInsertAfter,
					0,
					0,
					0,
					0,
					ExternalAppNativeMethods.SwpNoActivate |
					ExternalAppNativeMethods.SwpNoMove |
					ExternalAppNativeMethods.SwpNoSize |
					ExternalAppNativeMethods.SwpFrameChanged);
				if (snapshot.HasPlacement) ExternalAppNativeMethods.RestorePlacement(handle, snapshot.Placement);
			}
			ExternalAppNativeMethods.Show(handle, ExternalAppNativeMethods.SwShow);
		}
		catch (Win32Exception exception)
		{
			Log($"restore-failed hwnd={FormatHandle(handle)} error={exception.Message}");
		}
	}

	private void RestoreOverlayOwner(nint handle, nint owner)
	{
		try
		{
			ExternalAppNativeMethods.SetWindowOwner(handle, owner);
		}
		catch (Win32Exception exception)
		{
			// During late teardown Windows can clear a destroyed owner before
			// SetWindowLongPtr returns. Continue only when the live window already
			// has the exact owner we intended; placement and show-state restoration
			// must not be skipped after that confirmed postcondition.
			if (!ExternalAppNativeMethods.IsValidWindow(handle) || ExternalAppNativeMethods.GetOwner(handle) != owner)
			{
				throw;
			}
			Log($"restore-owner-confirmed-after-error hwnd={FormatHandle(handle)} error={exception.Message}");
		}
	}

	private void AttachInputThreads()
	{
		if (!_inputThreadsAttached && _hostThreadId != 0 && _dockedThreadId != 0 && _hostThreadId != _dockedThreadId)
		{
			_inputThreadsAttached = ExternalAppNativeMethods.AttachInput(_hostThreadId, _dockedThreadId, attach: true);
		}
	}

	private void DetachInputThreads()
	{
		if (_inputThreadsAttached)
		{
			ExternalAppNativeMethods.AttachInput(_hostThreadId, _dockedThreadId, attach: false);
			_inputThreadsAttached = false;
		}
	}

	private void ClearDockState()
	{
		DetachInputThreads();
		_overlayDocked = false;
		_reparentSuspended = false;
		_lastDockedWidth = -1;
		_lastDockedHeight = -1;
		_lastDockedTopOffset = -1;
		_dockedHandle = nint.Zero;
		_snapshot = null;
		_hostThreadId = 0;
		_dockedThreadId = 0;
	}

	private void StartHealthMonitor()
	{
		StopHealthMonitor();
		_healthTimer = new Timer(CheckDockHealth, null, HealthInterval, HealthInterval);
	}

	private void StopHealthMonitor()
	{
		Interlocked.Exchange(ref _healthTimer, null)?.Dispose();
	}

	private void CheckDockHealth(object? state)
	{
		_ = state;
		if (_disposed || _dockedHandle == nint.Zero) return;
		if (!ExternalAppNativeMethods.IsValidWindow(_dockedHandle))
		{
			StopHealthMonitor();
			ClearDockState();
			SetState(ExternalAppDockState.ApplicationClosed, "Application closed. Click Retry to reconnect or relaunch.");
			return;
		}
		if (_definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent)
		{
			nint parent = ExternalAppNativeMethods.GetWindowParent(_dockedHandle);
			bool connectedToHost = parent == _hostHandle;
			bool connectedToSuspendedParent = _reparentSuspended && _snapshot != null && parent == _snapshot.Parent;
			if (!connectedToHost && !connectedToSuspendedParent)
			{
				StopHealthMonitor();
				ClearDockState();
				SetState(ExternalAppDockState.Disconnected, "Application disconnected. Click Retry.");
			}
		}
	}

	private void ForegroundWindowChanged(nint eventHook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime)
	{
		_ = eventHook;
		_ = eventType;
		_ = objectId;
		_ = childId;
		_ = eventThread;
		_ = eventTime;
		RememberExternalForegroundWindow(windowHandle);
	}

	private void RememberExternalForegroundWindow(nint handle)
	{
		if (handle == nint.Zero) return;
		handle = ExternalAppNativeMethods.GetRoot(handle);
		if (handle != nint.Zero && ExternalAppNativeMethods.GetWindowProcessId(handle) != Environment.ProcessId)
		{
			_lastExternalForegroundWindow = handle;
		}
	}

	private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
	{
		CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
		lock (_operationLock)
		{
			_activeOperation?.Cancel();
			_activeOperation = operation;
		}
		return operation;
	}

	private void EndOperation(CancellationTokenSource operation)
	{
		lock (_operationLock)
		{
			if (ReferenceEquals(_activeOperation, operation)) _activeOperation = null;
		}
	}

	private void CancelActiveOperation()
	{
		lock (_operationLock)
		{
			_activeOperation?.Cancel();
		}
	}

	private void TrackOwnedProcess(uint processId)
	{
		if (processId == 0) return;
		try
		{
			using Process process = Process.GetProcessById((int)processId);
			_ownedProcessId = processId;
			_ownedProcessStartTimeUtc = process.StartTime.ToUniversalTime();
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
		{
			_ownedProcessId = 0;
			_ownedProcessStartTimeUtc = null;
		}
	}

	private void TryTerminateOwnedProcess()
	{
		if (!_definition.TerminateOnPanelClose || _ownedProcessId == 0 || _ownedProcessStartTimeUtc == null) return;
		try
		{
			using Process process = Process.GetProcessById((int)_ownedProcessId);
			if (process.StartTime.ToUniversalTime() != _ownedProcessStartTimeUtc.Value) return;
			if (!process.CloseMainWindow()) process.Kill(entireProcessTree: false);
			Log("owned-process-close-request pid=" + _ownedProcessId);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
		{
			Log("owned-process-close-failed error=" + exception.Message);
		}
	}

	private bool Cancelled()
	{
		if (!_disposed)
		{
			LastError = "Docking was cancelled.";
			SetState(ExternalAppDockState.Disconnected, LastError);
		}
		return false;
	}

	private bool Fail(string message)
	{
		LastError = message + " Log: " + LogPath;
		SetState(ExternalAppDockState.UnableToEmbed, message);
		Log("dock-failed message=\"" + message + "\"");
		return false;
	}

	private ExternalAppWindowMatch? FailMatch(string message)
	{
		Fail(message);
		return null;
	}

	private void SetState(ExternalAppDockState next, string message)
	{
		lock (_stateLock)
		{
			if (!ExternalAppLifecycle.CanTransition(_state, next)) return;
			_state = next;
			_stateMessage = message;
		}
		Log($"state={next} message=\"{message}\"");
		StateChanged?.Invoke(this, new ExternalAppDockStateChangedEventArgs { State = next, Message = message });
	}

	private void LogCandidateSet(string phase, IReadOnlyList<ExternalAppWindowMatch> matches)
	{
		foreach (ExternalAppWindowMatch match in matches
			.Where(item => item.HasIdentityMatch || item.Score >= 25)
			.OrderByDescending(item => item.Score)
			.Take(8))
		{
			LogCandidate(phase, match);
		}
	}

	private void LogCandidate(string phase, ExternalAppWindowMatch match)
	{
		ExternalAppWindowCandidate candidate = match.Candidate;
		Log($"candidate phase={phase} hwnd={FormatHandle(candidate.Handle)} pid={candidate.ProcessId} process=\"{candidate.ProcessName}\" titleLength={candidate.Title.Length} class=\"{candidate.ClassName}\" path=\"{candidate.ProcessPath}\" package=\"{candidate.PackageFullName}\" aumid=\"{candidate.AppUserModelId}\" visible={candidate.IsVisible} tool={candidate.IsToolWindow} cloaked={candidate.IsCloaked} owned={candidate.IsOwned} foreground={candidate.IsForeground} size={candidate.Width}x{candidate.Height} appeared={candidate.AppearedAfterLaunch} activatedPidMatch={candidate.ActivatedProcessMatch} renderer={candidate.HasRenderedSurface} valid={match.IsValid} score={match.Score} reasons=\"{string.Join(", ", match.ScoreReasons)}\" rejected=\"{match.RejectionReason}\"");
	}

	private void Log(string message)
	{
		_logService.Info($"ExternalApp[{_definition.Id}] {message}");
	}

	private static string FormatHandle(nint handle) => $"0x{handle.ToInt64():X}";

	private static bool ContainsWindowsNotepadIdentity(string? value) =>
		!string.IsNullOrWhiteSpace(value) && value.Contains("WindowsNotepad", StringComparison.OrdinalIgnoreCase);
}
