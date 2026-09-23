using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace QuickPanel.Services;

public sealed record TaskManagerResponsiveLayout(
	bool ShowProcessId,
	bool ShowStatus,
	bool ShowWindowTitle,
	int SummaryColumns);

public static class TaskManagerPresentationPolicy
{
	private const double MediumWidth = 600.0;
	private const double WideWidth = 780.0;

	public static TaskManagerResponsiveLayout ForWidth(double width)
	{
		if (width >= WideWidth)
		{
			return new TaskManagerResponsiveLayout(true, true, true, 3);
		}
		if (width >= MediumWidth)
		{
			return new TaskManagerResponsiveLayout(true, false, false, 2);
		}
		return new TaskManagerResponsiveLayout(false, false, false, 2);
	}

	public static string FormatTemperatureSummary(IEnumerable<double> readings)
	{
		double[] valid = readings.Where(value => double.IsFinite(value) && value > 1.0).ToArray();
		return valid.Length == 0
			? "Unavailable"
			: valid.Max().ToString("0", CultureInfo.CurrentCulture) + " °C hottest";
	}

	public static string FormatFanSummary(IEnumerable<double> readings)
	{
		double[] valid = readings.Where(value => double.IsFinite(value) && value > 0.0).ToArray();
		return valid.Length == 0
			? "Unavailable"
			: valid.Length.ToString(CultureInfo.CurrentCulture) + " sensor" + (valid.Length == 1 ? string.Empty : "s") + " · " +
			  valid.Max().ToString("N0", CultureInfo.CurrentCulture) + " RPM max";
	}
}
