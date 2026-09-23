using System;

namespace QuickPanel.Services;

public static class BrowserViewportLayout
{
	public const double FallbackChromeHeight = 36.0;

	public static double ResolveChromeHeight(double measuredChromeHeight)
	{
		return double.IsFinite(measuredChromeHeight) && measuredChromeHeight > 0.0
			? measuredChromeHeight
			: FallbackChromeHeight;
	}

	public static BrowserViewportBounds Calculate(double hostWidth, double hostHeight, double measuredChromeHeight = FallbackChromeHeight)
	{
		double safeWidth = Math.Max(0.0, hostWidth);
		double safeChromeHeight = Math.Clamp(ResolveChromeHeight(measuredChromeHeight), 0.0, Math.Max(0.0, hostHeight));
		double safeHeight = Math.Max(0.0, hostHeight - safeChromeHeight);
		return new BrowserViewportBounds(0.0, safeChromeHeight, safeWidth, safeHeight);
	}
}

public readonly record struct BrowserViewportBounds(double X, double Y, double Width, double Height);
