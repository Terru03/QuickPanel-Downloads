using System;
using System.Collections.Generic;
using System.IO;
using QuickPanel.Models;

namespace QuickPanel.Services;

public enum WindowDockCompatibilityLevel
{
	Blocked,
	Ready,
	Try
}

public sealed record WindowDockAssessment(
	bool IsDockable,
	WindowDockCompatibilityLevel Level,
	string Label,
	string Reason);

public static class ExternalAppWindowPickerPolicy
{
	private const int MinimumWidth = 80;
	private const int MinimumHeight = 60;

	private static readonly HashSet<string> BlockedWindowClasses = new(StringComparer.OrdinalIgnoreCase)
	{
		"#32768",
		"CodexComputerUseCursorOverlay",
		"NarratorHelperWindow",
		"NotifyIconOverflowWindow",
		"Progman",
		"Shell_SecondaryTrayWnd",
		"Shell_TrayWnd",
		"TaskListThumbnailWnd",
		"tooltips_class32",
		"WorkerW"
	};

	private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"AIQuickPanel",
		"QuickPanel",
		"codex-computer-use",
		"consent",
		"CredentialUIBroker",
		"LockApp",
		"LogonUI",
		"SearchHost",
		"SecHealthUI",
		"SecurityHealthHost",
		"SecurityHealthSystray",
		"ShellExperienceHost",
		"StartMenuExperienceHost",
		"TextInputHost"
	};

	public static WindowDockAssessment Evaluate(ExternalAppWindowCandidate candidate, int ownProcessId)
	{
		if (candidate.Handle == nint.Zero || candidate.ProcessId == 0)
		{
			return Block("Window no longer exists.");
		}
		if (candidate.ProcessId == ownProcessId)
		{
			return Block("Quick Panel cannot dock itself.");
		}
		string processName = Path.GetFileNameWithoutExtension(candidate.ProcessName?.Trim() ?? string.Empty);
		if (BlockedProcesses.Contains(processName))
		{
			return Block("Quick Panel hides its own and protected Windows surfaces.");
		}
		if (BlockedWindowClasses.Contains(candidate.ClassName?.Trim() ?? string.Empty))
		{
			return Block("Windows shell surfaces cannot become tabs.");
		}
		if (!candidate.IsVisible)
		{
			return Block("Window is hidden.");
		}
		if (candidate.IsCloaked)
		{
			return Block("Window is a hidden packaged-app surface.");
		}
		if (candidate.IsChild)
		{
			return Block("Only top-level windows can be picked.");
		}
		List<string> cautions = [];
		if ((candidate.ClassName ?? string.Empty).Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
		{
			return new WindowDockAssessment(
				true,
				WindowDockCompatibilityLevel.Ready,
				"Shell view",
				"Opens the selected folder in a safe in-panel Explorer view; the original window stays unchanged.");
		}
		if (string.IsNullOrWhiteSpace(candidate.Title))
		{
			cautions.Add("untitled window");
		}
		if (candidate.IsOwned || candidate.IsToolWindow)
		{
			cautions.Add("utility window");
		}
		if ((candidate.ClassName ?? string.Empty).Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
		{
			cautions.Add("modern app compositor may render blank when child-hosted");
		}
		if (processName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase) ||
			(candidate.ClassName ?? string.Empty).Equals("TaskManagerWindow", StringComparison.OrdinalIgnoreCase))
		{
			cautions.Add("Windows Task Manager is often elevated; use Quick Panel's built-in Task Manager");
		}
		if (candidate.Width < MinimumWidth || candidate.Height < MinimumHeight)
		{
			cautions.Add($"small window {candidate.Width}x{candidate.Height}");
		}
		else if (candidate.Width < 240 || candidate.Height < 160)
		{
			cautions.Add("compact window");
		}
		return cautions.Count == 0
			? new WindowDockAssessment(true, WindowDockCompatibilityLevel.Ready, "Ready", "Standard visible window.")
			: new WindowDockAssessment(true, WindowDockCompatibilityLevel.Try, "Try", string.Join(", ", cautions) + ".");
	}

	private static WindowDockAssessment Block(string reason)
	{
		return new WindowDockAssessment(false, WindowDockCompatibilityLevel.Blocked, "Blocked", reason);
	}
}
