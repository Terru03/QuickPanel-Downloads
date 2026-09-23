using System;
using System.Globalization;
using QuickPanel.Models;

namespace QuickPanel.Services;

public static class ExternalAppDefinitionFactory
{
	private const string WindowIdPrefix = "window:";

	public static ExternalAppDefinition CreateForWindow(ExternalAppWindowCandidate candidate)
	{
		string title = candidate.Title.Trim();
		string processName = candidate.ProcessName.Trim();
		return new ExternalAppDefinition
		{
			Id = $"window:{candidate.ProcessId}:{candidate.Handle.ToInt64():X}",
			DisplayName = string.IsNullOrWhiteSpace(title) ? processName : title,
			Icon = "auto",
			ExecutablePath = EmptyToNull(candidate.ProcessPath),
			ProcessName = EmptyToNull(processName),
			WindowTitleMatch = EmptyToNull(title),
			WindowClassName = EmptyToNull(candidate.ClassName),
			AppUserModelId = EmptyToNull(candidate.AppUserModelId),
			PackageIdentity = GetStablePackageIdentity(candidate.PackageFullName),
			LaunchAutomatically = false,
			TerminateOnPanelClose = false,
			PreferredDockingBehavior = GetPreferredDockingBehavior(candidate),
			RestoreOriginalWindowOnUndock = true,
			MinimumCandidateScore = 40,
			RequireVisibleWindow = true,
			AllowOwnedWindows = candidate.IsOwned,
			AllowToolWindows = candidate.IsToolWindow,
			MinimumWindowWidth = Math.Clamp(candidate.Width, 1, 80),
			MinimumWindowHeight = Math.Clamp(candidate.Height, 1, 60),
			RequireRenderedSurface = false,
			ContentTopOffsetAt96Dpi = GetRecommendedTopCrop(candidate),
			IsEnabled = true
		};
	}

	public static ExternalAppDefinition CreateForInstalledApp(ExternalAppInstalledApp app)
	{
		return new ExternalAppDefinition
		{
			Id = "installed:" + (string.IsNullOrWhiteSpace(app.AppUserModelId) ? app.LaunchTarget : app.AppUserModelId),
			DisplayName = app.Name,
			Icon = "auto",
			ExecutablePath = GetExecutableLaunchTarget(app.LaunchTarget),
			AppUserModelId = EmptyToNull(app.AppUserModelId),
			PackageIdentity = GetStablePackageIdentity(app.PackageFamilyName),
			StartAppNameMatch = app.Name,
			LaunchKind = ExternalAppLaunchKind.StartApp,
			LaunchAutomatically = false,
			TerminateOnPanelClose = false,
			PreferredDockingBehavior = GetPreferredDockingBehavior(app),
			RestoreOriginalWindowOnUndock = true,
			MinimumCandidateScore = 30,
			RequireVisibleWindow = true,
			MinimumWindowWidth = 80,
			MinimumWindowHeight = 60,
			RequireRenderedSurface = false,
			ContentTopOffsetAt96Dpi = 0,
			IsEnabled = true
		};
	}

	public static bool TryReadWindowId(string? id, out uint processId, out nint windowHandle)
	{
		processId = 0;
		windowHandle = nint.Zero;
		if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(WindowIdPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] parts = id.Split(':', 3, StringSplitOptions.None);
		if (parts.Length != 3 ||
			!uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) ||
			!long.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handleValue) ||
			processId == 0 || handleValue == 0)
		{
			processId = 0;
			return false;
		}
		windowHandle = new nint(handleValue);
		return true;
	}

	private static string? GetStablePackageIdentity(string? packageFullName)
	{
		string? value = EmptyToNull(packageFullName);
		if (value == null) return null;
		int separator = value.IndexOf('_');
		return separator > 0 ? value[..separator] : value;
	}

	private static string? GetExecutableLaunchTarget(string? launchTarget)
	{
		string? value = EmptyToNull(launchTarget);
		return value != null && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
	}

	private static int GetRecommendedTopCrop(ExternalAppWindowCandidate candidate)
	{
		if (candidate.ClassName.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
		{
			// Windows 11 Explorer draws a second tab/title row inside its client area.
			// Quick Panel already supplies the outer tab, so hide only that duplicate row
			// while preserving Back/Forward, the address bar, search, and command bar.
			return 40;
		}
		return candidate.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase) ||
			candidate.ClassName.Equals("Notepad", StringComparison.OrdinalIgnoreCase) ||
			candidate.ClassName.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) ||
			candidate.ClassName.Equals("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase)
			? 32
			: 0;
	}

	private static ExternalAppDockingBehavior GetPreferredDockingBehavior(ExternalAppWindowCandidate candidate)
	{
		// Current packaged Notepad replaces its HWND after a child window is detached
		// and reparented a second time. Keep it as a border-preserving top-level overlay;
		// the dock service minimizes it while inactive instead of destroying its window.
		return candidate.ClassName.Equals("Notepad", StringComparison.OrdinalIgnoreCase) &&
			(IsWindowsNotepadIdentity(candidate.PackageFullName) || IsWindowsNotepadIdentity(candidate.AppUserModelId))
			? ExternalAppDockingBehavior.Overlay
			: ExternalAppDockingBehavior.Reparent;
	}

	private static ExternalAppDockingBehavior GetPreferredDockingBehavior(ExternalAppInstalledApp app)
	{
		return IsWindowsNotepadIdentity(app.PackageFamilyName) || IsWindowsNotepadIdentity(app.AppUserModelId)
			? ExternalAppDockingBehavior.Overlay
			: ExternalAppDockingBehavior.Reparent;
	}

	private static bool IsWindowsNotepadIdentity(string? value) =>
		!string.IsNullOrWhiteSpace(value) && value.Contains("WindowsNotepad", StringComparison.OrdinalIgnoreCase);

	private static string? EmptyToNull(string? value)
	{
		string? trimmed = value?.Trim();
		return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
	}
}
