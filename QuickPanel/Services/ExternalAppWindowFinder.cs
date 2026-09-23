using System;
using System.Collections.Generic;
using System.Linq;
using QuickPanel.Models;

namespace QuickPanel.Services;

internal sealed record ExternalAppLaunchIdentity(string AppUserModelId, string PackageFamilyName, uint ActivatedProcessId)
{
	internal string PackageName => PackageFamilyName.Split('_', 2)[0];
}

public sealed class ExternalAppWindowFinder
{
	private readonly int _ownProcessId;

	public ExternalAppWindowFinder(int? ownProcessId = null)
	{
		_ownProcessId = ownProcessId ?? Environment.ProcessId;
	}

	internal IReadOnlyList<ExternalAppWindowMatch> EnumerateMatches(
		ExternalAppDefinition definition,
		ISet<nint>? baselineHandles = null,
		ExternalAppLaunchIdentity? launchIdentity = null)
	{
		return EnumerateCandidates(baselineHandles, launchIdentity)
			.Select(candidate => ExternalAppWindowMatcher.Evaluate(definition, candidate, _ownProcessId))
			.ToList();
	}

	internal IReadOnlyList<ExternalAppWindowCandidate> EnumerateCandidates(
		ISet<nint>? baselineHandles = null,
		ExternalAppLaunchIdentity? launchIdentity = null)
	{
		return ExternalAppNativeMethods.EnumerateTopLevelWindows()
			.Select(handle => BuildCandidate(handle, baselineHandles, launchIdentity))
			.ToList();
	}

	internal ExternalAppWindowMatch? FindBest(
		ExternalAppDefinition definition,
		ISet<nint>? baselineHandles = null,
		ExternalAppLaunchIdentity? launchIdentity = null)
	{
		return EnumerateMatches(definition, baselineHandles, launchIdentity)
			.Where(match => match.IsValid)
			.OrderByDescending(match => match.Score)
			.ThenByDescending(match => match.Candidate.IsForeground)
			.ThenByDescending(match => (long)match.Candidate.Width * match.Candidate.Height)
			.FirstOrDefault();
	}

	internal ExternalAppWindowMatch EvaluateManual(ExternalAppDefinition definition, nint handle)
	{
		return ExternalAppWindowMatcher.Evaluate(definition, BuildCandidate(handle, null, null), _ownProcessId, manualSelection: true);
	}

	internal HashSet<nint> CaptureTopLevelHandles()
	{
		return ExternalAppNativeMethods.EnumerateTopLevelWindows().ToHashSet();
	}

	internal static ExternalAppWindowCandidate BuildCandidate(
		nint handle,
		ISet<nint>? baselineHandles,
		ExternalAppLaunchIdentity? launchIdentity)
	{
		uint processId = ExternalAppNativeMethods.GetWindowProcessId(handle);
		string package = ExternalAppNativeMethods.GetPackageFullName(processId);
		string appUserModelId = ExternalAppNativeMethods.GetProcessAppUserModelId(processId);
		bool startIdentityMatch = launchIdentity != null &&
			(!string.IsNullOrWhiteSpace(appUserModelId) && appUserModelId.Equals(launchIdentity.AppUserModelId, StringComparison.OrdinalIgnoreCase) ||
			 !string.IsNullOrWhiteSpace(package) && !string.IsNullOrWhiteSpace(launchIdentity.PackageName) && package.Contains(launchIdentity.PackageName, StringComparison.OrdinalIgnoreCase));
		int width = 0;
		int height = 0;
		if (ExternalAppNativeMethods.TryGetWindowBounds(handle, out ExternalAppNativeMethods.NativeRect bounds))
		{
			width = bounds.Right - bounds.Left;
			height = bounds.Bottom - bounds.Top;
		}
		return new ExternalAppWindowCandidate
		{
			Handle = handle,
			ProcessId = processId,
			ProcessName = ExternalAppNativeMethods.GetProcessName(processId),
			ProcessPath = ExternalAppNativeMethods.GetProcessPath(processId),
			PackageFullName = package,
			AppUserModelId = appUserModelId,
			Title = ExternalAppNativeMethods.GetTitle(handle),
			ClassName = ExternalAppNativeMethods.GetClassName(handle),
			IsVisible = ExternalAppNativeMethods.IsVisible(handle),
			IsForeground = ExternalAppNativeMethods.GetForeground() == handle,
			IsToolWindow = ExternalAppNativeMethods.IsToolWindow(handle),
			IsCloaked = ExternalAppNativeMethods.IsCloaked(handle),
			IsOwned = ExternalAppNativeMethods.GetOwner(handle) != nint.Zero,
			IsChild = ExternalAppNativeMethods.IsChildStyle(handle),
			Width = width,
			Height = height,
			AppearedAfterLaunch = baselineHandles != null && !baselineHandles.Contains(handle),
			ActivatedProcessMatch = launchIdentity?.ActivatedProcessId is > 0 && processId == launchIdentity.ActivatedProcessId,
			StartAppIdentityMatch = startIdentityMatch,
			HasRenderedSurface = ExternalAppNativeMethods.HasRenderedSurface(handle)
		};
	}
}
