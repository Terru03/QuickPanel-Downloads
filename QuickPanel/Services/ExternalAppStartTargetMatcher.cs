using System;
using System.Collections.Generic;
using QuickPanel.Models;

namespace QuickPanel.Services;

public static class ExternalAppStartTargetMatcher
{
	public static bool HasStableIdentity(
		ExternalAppDefinition definition,
		string name,
		string appUserModelId,
		string packageFamily)
	{
		if (!string.IsNullOrWhiteSpace(definition.AppUserModelId) &&
			appUserModelId.Equals(definition.AppUserModelId, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (EqualsAny(appUserModelId, definition.AlternativeAppUserModelIds))
		{
			return true;
		}
		if (!string.IsNullOrWhiteSpace(definition.PackageIdentity) &&
			packageFamily.Contains(definition.PackageIdentity, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (ContainsAny(packageFamily, definition.AlternativePackageIdentities))
		{
			return true;
		}
		string search = definition.StartAppNameMatch ?? definition.DisplayName;
		if (name.Equals(search, StringComparison.OrdinalIgnoreCase) ||
			ContainsIdentityToken(appUserModelId, search) ||
			ContainsIdentityToken(packageFamily, search))
		{
			return true;
		}
		return !string.IsNullOrWhiteSpace(definition.ProcessName) &&
			(ContainsIdentityToken(appUserModelId, definition.ProcessName) ||
			 ContainsIdentityToken(packageFamily, definition.ProcessName));
	}

	public static int Score(
		ExternalAppDefinition definition,
		string name,
		string appUserModelId,
		string packageFamily)
	{
		int score = 0;
		if (!string.IsNullOrWhiteSpace(definition.AppUserModelId) && appUserModelId.Equals(definition.AppUserModelId, StringComparison.OrdinalIgnoreCase)) score += 200;
		else if (EqualsAny(appUserModelId, definition.AlternativeAppUserModelIds)) score += 180;
		if (!string.IsNullOrWhiteSpace(definition.PackageIdentity) && packageFamily.Contains(definition.PackageIdentity, StringComparison.OrdinalIgnoreCase)) score += 150;
		else if (ContainsAny(packageFamily, definition.AlternativePackageIdentities)) score += 140;
		string search = definition.StartAppNameMatch ?? definition.DisplayName;
		if (name.Equals(search, StringComparison.OrdinalIgnoreCase)) score += 100;
		else if (name.Contains(search, StringComparison.OrdinalIgnoreCase)) score += 60;
		if (ContainsIdentityToken(appUserModelId, search) || ContainsIdentityToken(packageFamily, search)) score += 80;
		if (!string.IsNullOrWhiteSpace(definition.ProcessName) &&
			(ContainsIdentityToken(appUserModelId, definition.ProcessName) || ContainsIdentityToken(packageFamily, definition.ProcessName))) score += 40;
		return score;
	}

	private static bool EqualsAny(string value, IEnumerable<string> expectedValues)
	{
		foreach (string expected in expectedValues)
		{
			if (!string.IsNullOrWhiteSpace(expected) && value.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	private static bool ContainsAny(string value, IEnumerable<string> expectedValues)
	{
		foreach (string expected in expectedValues)
		{
			if (!string.IsNullOrWhiteSpace(expected) && value.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	public static bool ContainsIdentityToken(string value, string token)
	{
		if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token)) return false;
		int startIndex = 0;
		while (startIndex < value.Length)
		{
			int index = value.IndexOf(token, startIndex, StringComparison.OrdinalIgnoreCase);
			if (index < 0) return false;
			int end = index + token.Length;
			bool leftBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
			bool rightBoundary = end == value.Length || !char.IsLetterOrDigit(value[end]);
			if (leftBoundary && rightBoundary) return true;
			startIndex = index + 1;
		}
		return false;
	}
}
