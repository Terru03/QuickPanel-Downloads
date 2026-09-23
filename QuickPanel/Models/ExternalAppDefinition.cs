using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace QuickPanel.Models;

public enum ExternalAppLaunchKind
{
	Auto,
	Executable,
	AppUserModelId,
	Uri,
	Shell,
	StartApp
}

public enum ExternalAppDockingBehavior
{
	Reparent,
	Overlay,
	Detached
}

public sealed class ExternalAppMatchPriority
{
	public int ExactProcessName { get; init; } = 45;

	public int AlternativeProcessName { get; init; } = 38;

	public int ProcessPath { get; init; } = 24;

	public int AppUserModelId { get; init; } = 40;

	public int PackageIdentity { get; init; } = 34;

	public int ExactWindowTitle { get; init; } = 32;

	public int WindowTitleContains { get; init; } = 20;

	public int WindowTitleRegex { get; init; } = 35;

	public int WindowClass { get; init; } = 18;

	public int StartAppIdentity { get; init; } = 36;

	public int ActivatedProcess { get; init; } = 35;

	public int AppearedAfterLaunch { get; init; } = 18;

	public int RenderedSurface { get; init; } = 12;

	public int ForegroundWindow { get; init; } = 12;
}

public sealed class ExternalAppDefinition
{
	public required string Id { get; init; }

	public required string DisplayName { get; init; }

	public string? Icon { get; init; }

	public string? ExecutablePath { get; init; }

	public string? LaunchArguments { get; init; }

	public string? ProcessName { get; init; }

	public List<string> AlternativeProcessNames { get; init; } = new();

	public string? WindowTitleMatch { get; init; }

	public List<string> AlternativeWindowTitleMatches { get; init; } = new();

	public string? WindowTitleRegex { get; init; }

	public string? WindowClassName { get; init; }

	public List<string> AlternativeWindowClassNames { get; init; } = new();

	public string? AppUserModelId { get; init; }

	public List<string> AlternativeAppUserModelIds { get; init; } = new();

	public string? PackageIdentity { get; init; }

	public List<string> AlternativePackageIdentities { get; init; } = new();

	public string? UriLaunchCommand { get; init; }

	public string? ShellLaunchTarget { get; init; }

	public string? StartAppNameMatch { get; init; }

	public ExternalAppLaunchKind LaunchKind { get; init; } = ExternalAppLaunchKind.Auto;

	public int LaunchTimeoutMs { get; init; } = 20_000;

	public bool LaunchAutomatically { get; init; } = true;

	public bool TerminateOnPanelClose { get; init; }

	public ExternalAppDockingBehavior PreferredDockingBehavior { get; init; } = ExternalAppDockingBehavior.Reparent;

	public int StartupDelayMs { get; init; }

	public bool RestoreOriginalWindowOnUndock { get; init; } = true;

	public ExternalAppMatchPriority? CustomWindowMatchingPriority { get; init; }

	public int MinimumCandidateScore { get; init; } = 60;

	public bool RequireVisibleWindow { get; init; } = true;

	public bool AllowOwnedWindows { get; init; }

	public bool AllowToolWindows { get; init; }

	public int MinimumWindowWidth { get; init; } = 240;

	public int MinimumWindowHeight { get; init; } = 160;

	public bool RequireRenderedSurface { get; init; }

	public int ContentTopOffsetAt96Dpi { get; init; }

	public bool IsEnabled { get; init; } = true;

	public IReadOnlyList<string> Validate()
	{
		List<string> errors = new();
		if (string.IsNullOrWhiteSpace(Id))
		{
			errors.Add("External application ID is required.");
		}
		else if (Id.Length > 100 || Id.IndexOfAny(['\r', '\n', '\t']) >= 0)
		{
			errors.Add($"External application ID '{Id}' is invalid.");
		}
		if (string.IsNullOrWhiteSpace(DisplayName))
		{
			errors.Add($"External application '{Id}' needs a display name.");
		}
		if (LaunchTimeoutMs is < 500 or > 300_000)
		{
			errors.Add($"External application '{Id}' launch timeout must be from 500 to 300000 ms.");
		}
		if (StartupDelayMs is < 0 or > 300_000)
		{
			errors.Add($"External application '{Id}' startup delay must be from 0 to 300000 ms.");
		}
		if (MinimumCandidateScore is < 1 or > 1000)
		{
			errors.Add($"External application '{Id}' candidate score must be from 1 to 1000.");
		}
		if (ContentTopOffsetAt96Dpi is < 0 or > 500)
		{
			errors.Add($"External application '{Id}' content offset must be from 0 to 500 pixels.");
		}
		if (MinimumWindowWidth is < 1 or > 10000 || MinimumWindowHeight is < 1 or > 10000)
		{
			errors.Add($"External application '{Id}' minimum window dimensions must be from 1 to 10000 pixels.");
		}
		if (!string.IsNullOrWhiteSpace(WindowTitleRegex))
		{
			try
			{
				_ = new Regex(WindowTitleRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
			}
			catch (ArgumentException exception)
			{
				errors.Add($"External application '{Id}' has invalid title regex: {exception.Message}");
			}
		}
		return errors;
	}
}
