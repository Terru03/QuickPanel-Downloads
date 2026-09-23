using System;

namespace QuickPanel.Services;

public static class PanelResizePolicy
{
	public const double DragScale = 0.18;

	public static double ApplyDelta(double currentSize, double pointerDelta, double minimumSize)
	{
		double adjusted = currentSize + pointerDelta * DragScale;
		return Math.Max(minimumSize, Math.Round(adjusted, 1, MidpointRounding.AwayFromZero));
	}
}
