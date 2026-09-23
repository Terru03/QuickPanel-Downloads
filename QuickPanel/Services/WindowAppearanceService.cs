using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickPanel.Services;

public static class WindowAppearanceService
{
	private enum DwmWindowCornerPreferenceValue
	{
		Default,
		DoNotRound,
		Round
	}

	private const int DwmWindowCornerPreference = 33;

	private const int DwmWindowBorderColor = 34;

	private const int DwmWindowColorNone = -2;

	public static void Apply(Window window)
	{
		nint handle = new WindowInteropHelper(window).Handle;
		if (handle != IntPtr.Zero)
		{
			int attributeValue = 2;
			DwmSetWindowAttribute(handle, 33, ref attributeValue, 4);
			int attributeValue2 = -2;
			DwmSetWindowAttribute(handle, 34, ref attributeValue2, 4);
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref int attributeValue, int attributeSize);
}
