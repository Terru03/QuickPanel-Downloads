using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickPanel.Services;

internal static class ExternalAppNativeMethods
{
	internal const int GwlStyle = -16;
	internal const int GwlExtendedStyle = -20;
	internal const int GwlHwndParent = -8;
	internal const int SwHide = 0;
	internal const int SwShowNoActivate = 4;
	internal const int SwShow = 5;
	internal const int SwMinimize = 6;
	internal const int SwRestore = 9;
	internal const long WsChild = 0x40000000L;
	internal const long WsPopup = 0x80000000L;
	internal const long WsCaption = 0x00C00000L;
	internal const long WsThickFrame = 0x00040000L;
	internal const long WsSystemMenu = 0x00080000L;
	internal const long WsMinimizeBox = 0x00020000L;
	internal const long WsMaximizeBox = 0x00010000L;
	internal const long WsMinimize = 0x20000000L;
	internal const long WsMaximize = 0x01000000L;
	internal const long WsVisible = 0x10000000L;
	internal const long WsClipChildren = 0x02000000L;
	internal const long WsClipSiblings = 0x04000000L;
	internal const long WsExtendedAppWindow = 0x00040000L;
	internal const long WsExtendedToolWindow = 0x00000080L;
	internal const long WsExtendedTopmost = 0x00000008L;
	internal static readonly nint HwndTop = nint.Zero;
	internal static readonly nint HwndTopmost = new(-1);
	internal static readonly nint HwndNoTopmost = new(-2);
	internal const uint SwpNoSize = 0x0001;
	internal const uint SwpNoMove = 0x0002;
	internal const uint SwpNoZOrder = 0x0004;
	internal const uint SwpNoActivate = 0x0010;
	internal const uint SwpFrameChanged = 0x0020;
	internal const uint SwpShowWindow = 0x0040;

	private const uint GaRoot = 2;
	private const uint GwOwner = 4;
	private const uint WmSize = 5;
	private const uint WmNull = 0;
	private const uint WmGetIcon = 0x007F;
	private const int IconSmall = 0;
	private const int IconBig = 1;
	private const int IconSmall2 = 2;
	private const int GclpIcon = -14;
	private const int GclpIconSmall = -34;
	private const uint DwmwaCloaked = 14;
	private const uint SmtoAbortIfHung = 2;
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const int ErrorInsufficientBuffer = 122;
	private const uint RdwInvalidate = 0x0001;
	private const uint RdwErase = 0x0004;
	private const uint RdwAllChildren = 0x0080;
	private const uint RdwUpdateNow = 0x0100;
	private const uint RdwFrame = 0x0400;
	private const uint ShgfiIcon = 0x000000100;
	private const uint ShgfiLargeIcon = 0x000000000;
	private const uint ShgfiPidl = 0x000000008;
	private const int DpiHostingBehaviorInvalid = -1;
	private const int DpiHostingBehaviorMixed = 1;
	private const uint SsBlackRect = 0x00000004;

	internal const uint EventSystemForeground = 3;
	internal const uint WineventOutOfContext = 0;
	internal const uint WineventSkipOwnProcess = 2;

	internal delegate void WinEventDelegate(nint eventHook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime);

	private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

	[Flags]
	private enum ActivateOptions
	{
		NoErrorUi = 2,
		NoSplashScreen = 4
	}

	[ComImport]
	[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IApplicationActivationManager
	{
		[PreserveSig]
		int ActivateApplication(
			[MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
			[MarshalAs(UnmanagedType.LPWStr)] string? arguments,
			ActivateOptions options,
			out uint processId);

		void ActivateForFile();

		void ActivateForProtocol();
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct NativePoint
	{
		internal int X;
		internal int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct NativeRect
	{
		internal int Left;
		internal int Top;
		internal int Right;
		internal int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct WindowPlacement
	{
		internal int Length;
		internal int Flags;
		internal int ShowCommand;
		internal NativePoint MinimumPosition;
		internal NativePoint MaximumPosition;
		internal NativeRect NormalPosition;
	}

	internal sealed class WindowSnapshot
	{
		internal nint Parent { get; init; }
		internal nint Style { get; init; }
		internal nint ExtendedStyle { get; init; }
		internal WindowPlacement Placement { get; init; }
		internal bool HasPlacement { get; init; }
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ShellFileInfo
	{
		internal nint IconHandle;
		internal int IconIndex;
		internal uint Attributes;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		internal string? DisplayName;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
		internal string? TypeName;
	}

	internal static IReadOnlyList<nint> EnumerateTopLevelWindows()
	{
		List<nint> handles = [];
		EnumWindows((handle, _) =>
		{
			handles.Add(handle);
			return true;
		}, nint.Zero);
		return handles;
	}

	internal static IReadOnlyList<nint> EnumerateChildWindows(nint parent)
	{
		List<nint> handles = [];
		EnumChildWindows(parent, (handle, _) =>
		{
			handles.Add(handle);
			return true;
		}, nint.Zero);
		return handles;
	}

	internal static nint CreateHostWindow(nint parent)
	{
		int previousHostingBehavior = DpiHostingBehaviorInvalid;
		try
		{
			previousHostingBehavior = SetThreadDpiHostingBehavior(DpiHostingBehaviorMixed);
		}
		catch (EntryPointNotFoundException)
		{
		}
		try
		{
			// Client-drawn windows can keep translucent chrome after
			// reparenting. Paint an opaque native backing surface so that transparency cannot
			// reveal the previously selected WPF tab or another desktop window underneath.
			// Clip child bounds when this hidden host becomes visible again; otherwise the
			// STATIC control can repaint over an already-parented application window.
			uint style = unchecked((uint)(WsChild | WsVisible | WsClipChildren | WsClipSiblings | SsBlackRect));
			nint handle = CreateWindowEx(0, "static", null, style, 0, 0, 0, 0, parent, nint.Zero, GetModuleHandle(null), nint.Zero);
			if (handle == nint.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			return handle;
		}
		finally
		{
			if (previousHostingBehavior != DpiHostingBehaviorInvalid)
			{
				_ = SetThreadDpiHostingBehavior(previousHostingBehavior);
			}
		}
	}

	internal static void DestroyHostWindow(nint handle)
	{
		if (handle != nint.Zero)
		{
			DestroyWindow(handle);
		}
	}

	internal static bool IsValidWindow(nint handle) => handle != nint.Zero && IsWindow(handle);

	internal static Icon? TryGetShellIcon(string target)
	{
		if (string.IsNullOrWhiteSpace(target)) return null;
		string expanded = Environment.ExpandEnvironmentVariables(target.Trim());
		if (File.Exists(expanded) && Path.GetExtension(expanded).Equals(".ico", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				return new Icon(expanded);
			}
			catch (Exception exception) when (exception is ArgumentException or IOException)
			{
			}
		}
		nint itemIdList = nint.Zero;
		try
		{
			if (SHParseDisplayName(expanded, nint.Zero, out itemIdList, 0, out _) < 0 || itemIdList == nint.Zero) return null;
			ShellFileInfo info = new();
			if (SHGetFileInfo(itemIdList, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiPidl | ShgfiIcon | ShgfiLargeIcon) == nint.Zero ||
				info.IconHandle == nint.Zero)
			{
				return null;
			}
			try
			{
				using Icon shellIcon = Icon.FromHandle(info.IconHandle);
				return (Icon)shellIcon.Clone();
			}
			finally
			{
				DestroyIcon(info.IconHandle);
			}
		}
		finally
		{
			if (itemIdList != nint.Zero) Marshal.FreeCoTaskMem(itemIdList);
		}
	}

	internal static Icon? TryGetWindowIcon(nint handle)
	{
		if (!IsValidWindow(handle)) return null;
		foreach (int iconType in new[] { IconBig, IconSmall2, IconSmall })
		{
			if (SendMessageTimeout(handle, WmGetIcon, new nint(iconType), nint.Zero, SmtoAbortIfHung, 80, out nint iconHandle) != nint.Zero &&
				iconHandle != nint.Zero)
			{
				Icon? icon = CloneIcon(iconHandle);
				if (icon != null) return icon;
			}
		}
		foreach (int index in new[] { GclpIcon, GclpIconSmall })
		{
			nint iconHandle = GetClassLongPtr(handle, index);
			if (iconHandle == nint.Zero) continue;
			Icon? icon = CloneIcon(iconHandle);
			if (icon != null) return icon;
		}
		return null;
	}

	private static Icon? CloneIcon(nint iconHandle)
	{
		try
		{
			using Icon icon = Icon.FromHandle(iconHandle);
			return (Icon)icon.Clone();
		}
		catch (Exception exception) when (exception is ArgumentException or ExternalException)
		{
			return null;
		}
	}

	internal static bool IsVisible(nint handle) => IsWindowVisible(handle);

	internal static bool IsMinimized(nint handle) => IsIconic(handle);

	internal static bool IsMaximized(nint handle) => IsZoomed(handle);

	internal static nint GetForeground() => GetForegroundWindow();

	internal static nint GetRoot(nint handle) => GetAncestor(handle, GaRoot);

	internal static nint GetOwner(nint handle) => GetWindow(handle, GwOwner);

	internal static nint GetWindowParent(nint handle) => GetParent(handle);

	internal static uint GetWindowProcessId(nint handle)
	{
		GetWindowThreadProcessId(handle, out uint processId);
		return processId;
	}

	internal static uint GetWindowThreadId(nint handle)
	{
		return GetWindowThreadProcessId(handle, out _);
	}

	internal static string GetTitle(nint handle)
	{
		StringBuilder text = new(512);
		GetWindowText(handle, text, text.Capacity);
		return text.ToString();
	}

	internal static string GetClassName(nint handle)
	{
		StringBuilder text = new(256);
		GetClassNameNative(handle, text, text.Capacity);
		return text.ToString();
	}

	internal static nint GetStyle(nint handle, int index) => GetWindowLongPtr(handle, index);

	internal static void SetStyle(nint handle, int index, nint value)
	{
		SetLastError(0);
		nint result = IntPtr.Size == 8
			? SetWindowLongPtr64(handle, index, value)
			: new nint(SetWindowLong32(handle, index, value.ToInt32()));
		if (result == nint.Zero && Marshal.GetLastWin32Error() != 0)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	internal static void SetWindowOwner(nint handle, nint owner) => SetStyle(handle, GwlHwndParent, owner);

	internal static bool IsChildStyle(nint handle) => (GetStyle(handle, GwlStyle).ToInt64() & WsChild) != 0;

	internal static bool IsToolWindow(nint handle) => (GetStyle(handle, GwlExtendedStyle).ToInt64() & WsExtendedToolWindow) != 0;

	internal static bool IsCloaked(nint handle)
	{
		return DwmGetWindowAttribute(handle, DwmwaCloaked, out int value, sizeof(int)) == 0 && value != 0;
	}

	internal static bool TryGetWindowBounds(nint handle, out NativeRect bounds) => GetWindowRect(handle, out bounds);

	internal static bool TryGetClientBounds(nint handle, out NativeRect bounds) => GetClientRect(handle, out bounds);

	internal static WindowSnapshot CaptureWindow(nint handle)
	{
		WindowPlacement placement = new() { Length = Marshal.SizeOf<WindowPlacement>() };
		bool hasPlacement = GetWindowPlacement(handle, ref placement);
		return new WindowSnapshot
		{
			Parent = GetParent(handle),
			Style = GetStyle(handle, GwlStyle),
			ExtendedStyle = GetStyle(handle, GwlExtendedStyle),
			Placement = placement,
			HasPlacement = hasPlacement
		};
	}

	internal static void SetWindowParent(nint child, nint parent)
	{
		SetLastError(0);
		if (SetParent(child, parent) == nint.Zero && Marshal.GetLastWin32Error() != 0)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	internal static void SetWindowPosition(nint handle, int x, int y, int width, int height, uint flags)
	{
		SetWindowPosition(handle, HwndTop, x, y, width, height, flags);
	}

	internal static void SetWindowPosition(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags)
	{
		if (!SetWindowPos(handle, insertAfter, x, y, width, height, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	internal static void Show(nint handle, int command) => ShowWindow(handle, command);

	internal static void RestorePlacement(nint handle, WindowPlacement placement)
	{
		SetWindowPlacement(handle, ref placement);
	}

	internal static uint GetDpi(nint handle)
	{
		uint dpi = GetDpiForWindow(handle);
		return dpi == 0 ? 96u : dpi;
	}

	internal static bool FocusWindow(nint hostHandle, nint childHandle)
	{
		nint root = GetRoot(hostHandle);
		if (root != nint.Zero)
		{
			SetForegroundWindow(root);
			SetActiveWindow(root);
		}
		nint previous = SetFocus(childHandle);
		return previous != nint.Zero || GetFocus() == childHandle;
	}

	internal static bool AttachInput(uint firstThreadId, uint secondThreadId, bool attach)
	{
		return AttachThreadInput(firstThreadId, secondThreadId, attach);
	}

	internal static bool IsResponsive(nint handle)
	{
		return SendMessageTimeout(handle, WmNull, nint.Zero, nint.Zero, SmtoAbortIfHung, 200, out _) != nint.Zero;
	}

	internal static bool TryGetRenderedSurface(nint handle, bool requireVisible, out nint rendererHandle, out int width, out int height)
	{
		rendererHandle = nint.Zero;
		width = 0;
		height = 0;
		foreach (nint child in EnumerateChildWindows(handle))
		{
			if (!GetClassName(child).Equals("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) ||
				(requireVisible && !IsVisible(child)) ||
				!TryGetWindowBounds(child, out NativeRect bounds) ||
				bounds.Right - bounds.Left <= 100 || bounds.Bottom - bounds.Top <= 100)
			{
				continue;
			}
			rendererHandle = child;
			width = bounds.Right - bounds.Left;
			height = bounds.Bottom - bounds.Top;
			return true;
		}
		return false;
	}

	internal static bool HasRenderedSurface(nint handle, bool requireVisible = true)
	{
		return TryGetRenderedSurface(handle, requireVisible, out _, out _, out _);
	}

	internal static void ForceRedraw(nint handle)
	{
		if (!TryGetClientBounds(handle, out NativeRect bounds))
		{
			return;
		}
		int width = Math.Max(1, bounds.Right - bounds.Left);
		int height = Math.Max(1, bounds.Bottom - bounds.Top);
		SendMessage(handle, WmSize, nint.Zero, new nint((height << 16) | (width & 0xFFFF)));
		RedrawWindow(handle, nint.Zero, nint.Zero, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow | RdwFrame);
		UpdateWindow(handle);
		foreach (nint child in EnumerateChildWindows(handle))
		{
			RedrawWindow(child, nint.Zero, nint.Zero, RdwInvalidate | RdwUpdateNow | RdwAllChildren);
			UpdateWindow(child);
		}
		DwmFlush();
	}

	internal static void RequestRedraw(nint handle)
	{
		RedrawWindow(handle, nint.Zero, nint.Zero, RdwInvalidate | RdwAllChildren | RdwFrame);
	}

	internal static void FlushDwm() => DwmFlush();

	internal static nint InstallForegroundHook(WinEventDelegate callback)
	{
		return SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero, callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
	}

	internal static void RemoveEventHook(nint hook)
	{
		if (hook != nint.Zero)
		{
			UnhookWinEvent(hook);
		}
	}

	internal static string GetProcessName(uint processId)
	{
		try
		{
			using Process process = Process.GetProcessById((int)processId);
			return process.ProcessName;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
		{
			return string.Empty;
		}
	}

	internal static string GetProcessPath(uint processId)
	{
		nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (process == nint.Zero)
		{
			return string.Empty;
		}
		try
		{
			int size = 1024;
			StringBuilder path = new(size);
			return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : string.Empty;
		}
		finally
		{
			CloseHandle(process);
		}
	}

	internal static string GetPackageFullName(uint processId)
	{
		nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (process == nint.Zero)
		{
			return string.Empty;
		}
		try
		{
			uint length = 0;
			int result = GetPackageFullNameNative(process, ref length, null);
			if (result != 0 && result != ErrorInsufficientBuffer || length == 0)
			{
				return string.Empty;
			}
			StringBuilder value = new((int)length);
			return GetPackageFullNameNative(process, ref length, value) == 0 ? value.ToString() : string.Empty;
		}
		finally
		{
			CloseHandle(process);
		}
	}

	internal static string GetProcessAppUserModelId(uint processId)
	{
		nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (process == nint.Zero)
		{
			return string.Empty;
		}
		try
		{
			uint length = 0;
			if (GetApplicationUserModelId(process, ref length, null) != ErrorInsufficientBuffer || length == 0)
			{
				return string.Empty;
			}
			StringBuilder value = new((int)length);
			return GetApplicationUserModelId(process, ref length, value) == 0 ? value.ToString() : string.Empty;
		}
		finally
		{
			CloseHandle(process);
		}
	}

	internal static bool TryActivateApplication(string appUserModelId, string? arguments, out uint processId, out string error)
	{
		processId = 0;
		error = string.Empty;
		IApplicationActivationManager? manager = null;
		try
		{
			Type type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
			manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
			int result = manager.ActivateApplication(appUserModelId, arguments, ActivateOptions.NoErrorUi | ActivateOptions.NoSplashScreen, out processId);
			if (result >= 0)
			{
				return true;
			}
			error = Marshal.GetExceptionForHR(result)?.Message ?? $"Windows activation failed with error 0x{result:X8}.";
			return false;
		}
		catch (Exception exception) when (exception is COMException or TypeLoadException)
		{
			error = exception.Message;
			return false;
		}
		finally
		{
			if (manager != null && Marshal.IsComObject(manager))
			{
				Marshal.FinalReleaseComObject(manager);
			}
		}
	}

	private static nint GetWindowLongPtr(nint handle, int index)
	{
		return IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new nint(GetWindowLong32(handle, index));
	}

	private static nint GetClassLongPtr(nint handle, int index)
	{
		return IntPtr.Size == 8 ? GetClassLongPtr64(handle, index) : new nint(GetClassLong32(handle, index));
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumChildWindows(nint parentWindow, EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateWindowEx(uint extendedStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parentWindow, nint menu, nint instance, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyWindow(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(nint iconHandle);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHParseDisplayName(string name, nint bindingContext, out nint itemIdList, uint attributesIn, out uint attributesOut);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern nint SHGetFileInfo(nint itemIdList, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern nint GetModuleHandle(string? moduleName);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(nint windowHandle, StringBuilder windowText, int maximumCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
	private static extern int GetClassNameNative(nint windowHandle, StringBuilder className, int maximumCount);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindow(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsZoomed(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(nint windowHandle, out NativeRect bounds);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetClientRect(nint windowHandle, out NativeRect bounds);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint windowHandle, uint flags);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint windowHandle, uint command);

	[DllImport("user32.dll")]
	private static extern nint GetParent(nint windowHandle);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetParent(nint childWindow, nint newParent);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong32(nint windowHandle, int index);

	[DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
	private static extern nint GetClassLongPtr64(nint windowHandle, int index);

	[DllImport("user32.dll", EntryPoint = "GetClassLongW")]
	private static extern uint GetClassLong32(nint windowHandle, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint newValue);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
	private static extern int SetWindowLong32(nint windowHandle, int index, int newValue);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(nint windowHandle, int command);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

	[DllImport("user32.dll")]
	private static extern nint SetFocus(nint windowHandle);

	[DllImport("user32.dll")]
	private static extern nint GetFocus();

	[DllImport("user32.dll")]
	private static extern nint SetActiveWindow(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint windowHandle);

	[DllImport("user32.dll")]
	private static extern int SetThreadDpiHostingBehavior(int value);

	[DllImport("user32.dll")]
	private static extern nint SendMessage(nint windowHandle, uint message, nint wordParameter, nint longParameter);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SendMessageTimeout(nint windowHandle, uint message, nint wordParameter, nint longParameter, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RedrawWindow(nint windowHandle, nint updateRectangle, nint updateRegion, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UpdateWindow(nint windowHandle);

	[DllImport("user32.dll")]
	private static extern nint SetWinEventHook(uint eventMinimum, uint eventMaximum, nint eventHookModule, WinEventDelegate callback, uint processId, uint threadId, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWinEvent(nint eventHook);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint windowHandle, uint attribute, out int value, int valueSize);

	[DllImport("dwmapi.dll")]
	private static extern int DwmFlush();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, StringBuilder executableName, ref int size);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPackageFullName")]
	private static extern int GetPackageFullNameNative(nint processHandle, ref uint packageFullNameLength, StringBuilder? packageFullName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetApplicationUserModelId(nint processHandle, ref uint applicationUserModelIdLength, StringBuilder? applicationUserModelId);

	[DllImport("kernel32.dll")]
	private static extern void SetLastError(int errorCode);
}
