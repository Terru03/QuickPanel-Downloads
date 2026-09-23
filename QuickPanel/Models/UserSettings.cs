using System.Collections.Generic;
using QuickPanel.Services;

namespace QuickPanel.Models;

public sealed class UserSettings
{
	public List<AiTab> CustomTabs { get; init; } = new List<AiTab>();

	public List<ClosedTabEntry> ClosedTabs { get; init; } = new List<ClosedTabEntry>();

	public string? LastSelectedTabId { get; init; }

	public List<string> TabOrder { get; init; } = new List<string>();

	public Dictionary<string, double> TabZoomFactors { get; init; } = new Dictionary<string, double>();

	public Dictionary<string, bool> TabPinStates { get; init; } = new Dictionary<string, bool>();

	public bool? StartWithWindows { get; init; }

	public bool? AlwaysOnTop { get; init; }

	public double? PanelWidth { get; init; }

	public double? PanelHeight { get; init; }

	public bool? LaunchCodexAutomatically { get; init; }

	public string? UpdateManifestUrl { get; init; }

	public bool? CheckForUpdatesOnStartup { get; init; }

	public CodexResetSettings? CodexReset { get; init; }

	public TripModeSettings? TripMode { get; init; }
}
