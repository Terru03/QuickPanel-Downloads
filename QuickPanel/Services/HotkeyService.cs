using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickPanel.Services;

public sealed class HotkeyService : IDisposable
{
	private const int HotkeyId = 20801;

	private const int WmHotkey = 786;

	private const uint ModAlt = 1u;

	private const uint ModControl = 2u;

	private const uint VirtualKeyG = 71u;

	private HwndSource? _source;

	private nint _windowHandle;

	private bool _isRegistered;

	public event Action? Pressed;

	public bool Register(Window window)
	{
		if (_isRegistered)
		{
			return true;
		}
		_windowHandle = new WindowInteropHelper(window).Handle;
		_source = HwndSource.FromHwnd(_windowHandle);
		_source?.AddHook(WindowMessageHook);
		_isRegistered = RegisterHotKey(_windowHandle, 20801, 3u, 71u);
		if (!_isRegistered)
		{
			_source?.RemoveHook(WindowMessageHook);
			_source = null;
		}
		return _isRegistered;
	}

	public void Dispose()
	{
		if (_isRegistered)
		{
			UnregisterHotKey(_windowHandle, 20801);
			_isRegistered = false;
		}
		_source?.RemoveHook(WindowMessageHook);
		_source = null;
		GC.SuppressFinalize(this);
	}

	private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
	{
		if (message == 786 && ((IntPtr)wParam).ToInt32() == 20801)
		{
			this.Pressed?.Invoke();
			handled = true;
		}
		return IntPtr.Zero;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
