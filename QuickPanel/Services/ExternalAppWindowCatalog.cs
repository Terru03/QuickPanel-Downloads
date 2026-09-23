using System;
using System.Collections.Generic;
using System.Linq;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed record DockableWindowEntry(
	ExternalAppWindowCandidate Candidate,
	WindowDockAssessment Assessment);

public sealed class ExternalAppWindowCatalog
{
	private readonly ExternalAppWindowFinder _windowFinder;
	private readonly int _ownProcessId;

	public ExternalAppWindowCatalog(ExternalAppWindowFinder? windowFinder = null, int? ownProcessId = null)
	{
		_ownProcessId = ownProcessId ?? Environment.ProcessId;
		_windowFinder = windowFinder ?? new ExternalAppWindowFinder(_ownProcessId);
	}

	public IReadOnlyList<DockableWindowEntry> GetDockableWindows()
	{
		return _windowFinder.EnumerateCandidates()
			.Select(candidate => new DockableWindowEntry(
				candidate,
				ExternalAppWindowPickerPolicy.Evaluate(candidate, _ownProcessId)))
			.Where(entry => entry.Assessment.IsDockable)
			.OrderByDescending(entry => entry.Candidate.IsForeground)
			.ThenBy(entry => entry.Assessment.Level == WindowDockCompatibilityLevel.Ready ? 0 : 1)
			.ThenBy(entry => entry.Candidate.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}
}
