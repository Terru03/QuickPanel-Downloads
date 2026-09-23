using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QuickPanel.Models;

namespace QuickPanel.Services;

public static class ExternalAppWindowMatcher
{
	private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

	public static ExternalAppWindowMatch Evaluate(
		ExternalAppDefinition definition,
		ExternalAppWindowCandidate candidate,
		int ownProcessId,
		bool manualSelection = false)
	{
		List<string> reasons = new();
		int score = 0;
		if (candidate.ProcessId == ownProcessId)
		{
			return Reject(definition, candidate, score, reasons, "it belongs to Quick Panel");
		}
		if (candidate.IsChild)
		{
			return Reject(definition, candidate, score, reasons, "it is not a top-level window");
		}
		if (definition.RequireVisibleWindow && !candidate.IsVisible)
		{
			return Reject(definition, candidate, score, reasons, "it is hidden");
		}
		if (candidate.IsCloaked)
		{
			return Reject(definition, candidate, score, reasons, "it is a cloaked package window");
		}
		if (candidate.IsToolWindow && !definition.AllowToolWindows)
		{
			return Reject(definition, candidate, score, reasons, "it is a tool window");
		}
		if (!definition.AllowOwnedWindows && candidate.IsOwned)
		{
			return Reject(definition, candidate, score, reasons, "it is owned by another window");
		}
		if (candidate.Width < definition.MinimumWindowWidth || candidate.Height < definition.MinimumWindowHeight)
		{
			return Reject(definition, candidate, score, reasons, $"it is too small ({candidate.Width}x{candidate.Height})");
		}

		Add(5, "visible");
		Add(candidate.Width >= 480 && candidate.Height >= 320 ? 9 : 5, $"size={candidate.Width}x{candidate.Height}");
		ExternalAppMatchPriority priority = definition.CustomWindowMatchingPriority ?? new ExternalAppMatchPriority();
		if (candidate.IsForeground)
		{
			Add(priority.ForegroundWindow, "foreground");
		}
		if (manualSelection)
		{
			Add(100, "manual-foreground-selection");
			return Accept(candidate, score, reasons, hasIdentityMatch: true);
		}

		bool strongIdentity = false;
		bool weakIdentity = false;
		if (EqualsName(candidate.ProcessName, definition.ProcessName))
		{
			strongIdentity = true;
			Add(priority.ExactProcessName, "process-name");
		}
		else if (ContainsName(definition.AlternativeProcessNames, candidate.ProcessName))
		{
			strongIdentity = true;
			Add(priority.AlternativeProcessName, "alternative-process-name");
		}
		if (!string.IsNullOrWhiteSpace(definition.ExecutablePath) &&
			PathMatches(candidate.ProcessPath, definition.ExecutablePath))
		{
			strongIdentity = true;
			Add(priority.ProcessPath, "process-path");
		}
		if (TextMatches(candidate.AppUserModelId, definition.AppUserModelId))
		{
			strongIdentity = true;
			Add(priority.AppUserModelId, "aumid");
		}
		else if (TextMatchesAny(candidate.AppUserModelId, definition.AlternativeAppUserModelIds))
		{
			strongIdentity = true;
			Add(priority.AppUserModelId, "alternative-aumid");
		}
		if (TextMatches(candidate.PackageFullName, definition.PackageIdentity))
		{
			strongIdentity = true;
			Add(priority.PackageIdentity, "package");
		}
		else if (TextMatchesAny(candidate.PackageFullName, definition.AlternativePackageIdentities))
		{
			strongIdentity = true;
			Add(priority.PackageIdentity, "alternative-package");
		}
		if (!string.IsNullOrWhiteSpace(definition.WindowTitleMatch))
		{
			if (candidate.Title.Equals(definition.WindowTitleMatch, StringComparison.OrdinalIgnoreCase))
			{
				weakIdentity = true;
				Add(priority.ExactWindowTitle, "title-exact");
			}
			else if (candidate.Title.Contains(definition.WindowTitleMatch, StringComparison.OrdinalIgnoreCase))
			{
				weakIdentity = true;
				Add(priority.WindowTitleContains, "title-contains");
			}
		}
		if (TextMatchesAny(candidate.Title, definition.AlternativeWindowTitleMatches))
		{
			weakIdentity = true;
			Add(priority.WindowTitleContains, "alternative-window-title");
		}
		if (!string.IsNullOrWhiteSpace(definition.WindowTitleRegex) &&
			IsRegexMatch(candidate.Title, definition.WindowTitleRegex))
		{
			weakIdentity = true;
			Add(priority.WindowTitleRegex, "title-regex");
		}
		if (!string.IsNullOrWhiteSpace(definition.WindowClassName) &&
			candidate.ClassName.Equals(definition.WindowClassName, StringComparison.OrdinalIgnoreCase))
		{
			weakIdentity = true;
			Add(priority.WindowClass, "window-class");
		}
		else if (EqualsAny(candidate.ClassName, definition.AlternativeWindowClassNames))
		{
			weakIdentity = true;
			Add(priority.WindowClass, "alternative-window-class");
		}
		if (candidate.StartAppIdentityMatch)
		{
			strongIdentity = true;
			Add(priority.StartAppIdentity, "start-app-identity");
		}
		else if (!string.IsNullOrWhiteSpace(definition.StartAppNameMatch) &&
			(ExternalAppStartTargetMatcher.ContainsIdentityToken(candidate.AppUserModelId, definition.StartAppNameMatch) ||
			 ExternalAppStartTargetMatcher.ContainsIdentityToken(candidate.PackageFullName, definition.StartAppNameMatch)))
		{
			strongIdentity = true;
			Add(priority.StartAppIdentity, "configured-start-app-identity");
		}
		if (candidate.ActivatedProcessMatch)
		{
			strongIdentity = true;
			Add(priority.ActivatedProcess, "activated-process-id");
		}
		if (candidate.AppearedAfterLaunch)
		{
			Add(priority.AppearedAfterLaunch, "appeared-after-launch");
		}
		if (candidate.HasRenderedSurface)
		{
			Add(priority.RenderedSurface, "rendered-surface");
		}
		bool identity = strongIdentity || weakIdentity;
		if (!identity)
		{
			return Reject(definition, candidate, score, reasons, $"it has no {definition.DisplayName} identity signal");
		}
		if (HasConfiguredStrongIdentity(definition) && !strongIdentity)
		{
			return Reject(definition, candidate, score, reasons, $"it has no strong {definition.DisplayName} process or package identity", identity);
		}
		return score >= definition.MinimumCandidateScore
			? Accept(candidate, score, reasons, identity)
			: Reject(definition, candidate, score, reasons, $"its score {score} is below {definition.MinimumCandidateScore}", identity);

		void Add(int points, string reason)
		{
			score += points;
			reasons.Add($"{reason}:{points:+#;-#;0}");
		}
	}

	private static ExternalAppWindowMatch Accept(ExternalAppWindowCandidate candidate, int score, IReadOnlyList<string> reasons, bool hasIdentityMatch)
	{
		return new ExternalAppWindowMatch
		{
			Candidate = candidate,
			IsValid = true,
			HasIdentityMatch = hasIdentityMatch,
			Score = score,
			ScoreReasons = reasons
		};
	}

	private static ExternalAppWindowMatch Reject(
		ExternalAppDefinition definition,
		ExternalAppWindowCandidate candidate,
		int score,
		IReadOnlyList<string> reasons,
		string reason,
		bool hasIdentityMatch = false)
	{
		_ = definition;
		return new ExternalAppWindowMatch
		{
			Candidate = candidate,
			IsValid = false,
			HasIdentityMatch = hasIdentityMatch,
			Score = score,
			RejectionReason = reason,
			ScoreReasons = reasons
		};
	}

	private static bool EqualsName(string value, string? expected)
	{
		return !string.IsNullOrWhiteSpace(expected) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
	}

	private static bool ContainsName(IEnumerable<string> names, string value)
	{
		foreach (string name in names)
		{
			if (!string.IsNullOrWhiteSpace(name) && value.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool TextMatches(string value, string? expected)
	{
		return !string.IsNullOrWhiteSpace(expected) &&
			(value.Equals(expected, StringComparison.OrdinalIgnoreCase) || value.Contains(expected, StringComparison.OrdinalIgnoreCase));
	}

	private static bool TextMatchesAny(string value, IEnumerable<string> expectedValues)
	{
		foreach (string expected in expectedValues)
		{
			if (TextMatches(value, expected)) return true;
		}
		return false;
	}

	private static bool EqualsAny(string value, IEnumerable<string> expectedValues)
	{
		foreach (string expected in expectedValues)
		{
			if (!string.IsNullOrWhiteSpace(expected) && value.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	private static bool HasConfiguredStrongIdentity(ExternalAppDefinition definition)
	{
		return !string.IsNullOrWhiteSpace(definition.ProcessName) ||
			definition.AlternativeProcessNames.Exists(name => !string.IsNullOrWhiteSpace(name)) ||
			!string.IsNullOrWhiteSpace(definition.ExecutablePath) ||
			!string.IsNullOrWhiteSpace(definition.AppUserModelId) ||
			definition.AlternativeAppUserModelIds.Exists(value => !string.IsNullOrWhiteSpace(value)) ||
			!string.IsNullOrWhiteSpace(definition.PackageIdentity) ||
			definition.AlternativePackageIdentities.Exists(value => !string.IsNullOrWhiteSpace(value)) ||
			!string.IsNullOrWhiteSpace(definition.StartAppNameMatch);
	}

	private static bool PathMatches(string candidatePath, string expectedPath)
	{
		string expanded = Environment.ExpandEnvironmentVariables(expectedPath);
		if (candidatePath.Equals(expanded, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return Path.GetFileNameWithoutExtension(candidatePath)
			.Equals(Path.GetFileNameWithoutExtension(expanded), StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsRegexMatch(string value, string pattern)
	{
		try
		{
			return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
		}
		catch (RegexMatchTimeoutException)
		{
			return false;
		}
	}
}
