using System.Collections.Generic;

namespace QuickPanel.Models;

public sealed class ExternalAppWindowCandidate
{
	public nint Handle { get; init; }

	public uint ProcessId { get; init; }

	public string ProcessName { get; init; } = string.Empty;

	public string ProcessPath { get; init; } = string.Empty;

	public string PackageFullName { get; init; } = string.Empty;

	public string AppUserModelId { get; init; } = string.Empty;

	public string Title { get; init; } = string.Empty;

	public string ClassName { get; init; } = string.Empty;

	public bool IsVisible { get; init; }

	public bool IsForeground { get; init; }

	public bool IsToolWindow { get; init; }

	public bool IsCloaked { get; init; }

	public bool IsOwned { get; init; }

	public bool IsChild { get; init; }

	public int Width { get; init; }

	public int Height { get; init; }

	public bool AppearedAfterLaunch { get; init; }

	public bool ActivatedProcessMatch { get; init; }

	public bool StartAppIdentityMatch { get; init; }

	public bool HasRenderedSurface { get; init; }
}

public sealed class ExternalAppWindowMatch
{
	public required ExternalAppWindowCandidate Candidate { get; init; }

	public bool IsValid { get; init; }

	public bool HasIdentityMatch { get; init; }

	public int Score { get; init; }

	public string? RejectionReason { get; init; }

	public IReadOnlyList<string> ScoreReasons { get; init; } = [];
}
