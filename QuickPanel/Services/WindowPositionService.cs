using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickPanel.Services;

public static class WindowPositionService
{
	private enum MonitorDpiType
	{
		Effective
	}

	private struct Point
	{
		public int X;

		public int Y;
	}

	private struct Rect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	private struct MonitorInfo
	{
		public uint Size;

		public Rect Monitor;

		public Rect WorkArea;

		public uint Flags;
	}

	private static readonly nint HwndTopmost = new IntPtr(-1);

	private const uint MonitorDefaultToNearest = 2u;

	private const uint SwpNoActivate = 16u;

	private const uint SwpShowWindow = 64u;

	private const double DefaultDpi = 96.0;

	private const double MaximumWorkAreaHeight = 1.0;

	private const double CursorGapDip = 12.0;

	private const double TitleBarAnchorDip = 15.0;

	public static void MoveNearCursor(Window window)
	{
		nint handle = new WindowInteropHelper(window).Handle;
		if (handle == IntPtr.Zero || !GetCursorPos(out var point))
		{
			return;
		}
		nint monitorHandle = MonitorFromPoint(point, MonitorDefaultToNearest);
		MonitorInfo monitorInfo = new MonitorInfo
		{
			Size = (uint)Marshal.SizeOf<MonitorInfo>()
		};
		if (GetMonitorInfo(monitorHandle, ref monitorInfo))
		{
			double monitorScale = GetMonitorScale(monitorHandle);
			Rect workArea = monitorInfo.WorkArea;
			int max = workArea.Right - workArea.Left;
			int num = workArea.Bottom - workArea.Top;
			int value = (int)Math.Round(window.Width * monitorScale);
			int val = (int)Math.Round(window.Height * monitorScale);
			int val2 = (int)Math.Floor(num * MaximumWorkAreaHeight);
			int num2 = Math.Clamp(value, 1, max);
			int num3 = Math.Clamp(Math.Min(val, val2), 1, num);
			int num4 = (int)Math.Round(CursorGapDip * monitorScale);
			int num5 = (int)Math.Round(TitleBarAnchorDip * monitorScale);
			int num6 = point.X + num4;
			if (num6 + num2 > workArea.Right)
			{
				num6 = point.X - num2 - num4;
			}
			int value2 = point.Y - num5;
			num6 = Math.Clamp(num6, workArea.Left, Math.Max(workArea.Left, workArea.Right - num2));
			value2 = Math.Clamp(value2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - num3));
			SetWindowPos(handle, HwndTopmost, num6, value2, num2, num3, SwpNoActivate | SwpShowWindow);
		}
	}

	private static double GetMonitorScale(nint monitorHandle)
	{
		try
		{
			uint dpiX;
			uint dpiY;
			return (GetDpiForMonitor(monitorHandle, MonitorDpiType.Effective, out dpiX, out dpiY) == 0) ? ((double)dpiX / 96.0) : 1.0;
		}
		catch (DllNotFoundException)
		{
			return 1.0;
		}
		catch (EntryPointNotFoundException)
		{
			return 1.0;
		}
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(Point point, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

	[DllImport("Shcore.dll")]
	private static extern int GetDpiForMonitor(nint monitorHandle, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
