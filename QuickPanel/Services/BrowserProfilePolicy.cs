using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed record BrowserProfileIdentity(string? Id, string Label)
{
	public bool IsDefault => string.IsNullOrWhiteSpace(Id);
}

public static class BrowserProfilePolicy
{
	public const string DefaultProfileLabel = "Default profile";

	private const int MaximumProfileIdLength = 64;

	private const int MaximumProfileLabelLength = 40;

	public static string? NormalizeProfileId(string? profileId)
	{
		string? value = profileId?.Trim();
		if (string.IsNullOrWhiteSpace(value) ||
			value.Length <= "qp-".Length ||
			value.Length > MaximumProfileIdLength ||
			!value.StartsWith("qp-", StringComparison.OrdinalIgnoreCase) ||
			value.EndsWith(".", StringComparison.Ordinal))
		{
			return null;
		}

		return value.All(IsAllowedProfileCharacter) ? value : null;
	}

	public static string? NormalizeProfileLabel(string? label, string? normalizedProfileId)
	{
		if (normalizedProfileId is null)
		{
			return null;
		}
		string value = label?.Trim() ?? string.Empty;
		if (value.Length > MaximumProfileLabelLength)
		{
			value = value[..MaximumProfileLabelLength].TrimEnd();
		}
		return string.IsNullOrWhiteSpace(value) ? "Separate profile" : value;
	}

	public static string GetDisplayLabel(AiTab tab)
	{
		string? id = NormalizeProfileId(tab.BrowserProfileId);
		return id is null
			? DefaultProfileLabel
			: NormalizeProfileLabel(tab.BrowserProfileLabel, id) ?? "Separate profile";
	}

	public static IReadOnlyList<BrowserProfileIdentity> GetAvailableProfiles(IEnumerable<AiTab> tabs)
	{
		var profiles = new List<BrowserProfileIdentity>
		{
			new(null, DefaultProfileLabel)
		};
		var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AiTab tab in tabs)
		{
			string? id = NormalizeProfileId(tab.BrowserProfileId);
			if (id is null || !used.Add(id))
			{
				continue;
			}
			profiles.Add(new BrowserProfileIdentity(id, GetDisplayLabel(tab)));
		}
		return profiles;
	}

	public static BrowserProfileIdentity CreateNew(IEnumerable<AiTab> tabs, Func<string>? idFactory = null)
	{
		ArgumentNullException.ThrowIfNull(tabs);
		AiTab[] existingTabs = tabs.ToArray();
		HashSet<string> usedIds = existingTabs
			.Select(tab => NormalizeProfileId(tab.BrowserProfileId))
			.Where(id => id is not null)
			.Select(id => id!)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		string profileId;
		int attempt = 0;
		do
		{
			string seed = attempt < 10 && idFactory is not null
				? idFactory()
				: Guid.NewGuid().ToString("N");
			profileId = NormalizeProfileId("qp-" + seed) ?? "qp-" + Guid.NewGuid().ToString("N");
			attempt++;
		}
		while (usedIds.Contains(profileId));

		HashSet<int> usedLabelNumbers = existingTabs
			.Select(tab => TryGetProfileNumber(GetDisplayLabel(tab)))
			.Where(number => number.HasValue)
			.Select(number => number!.Value)
			.ToHashSet();
		int profileNumber = 2;
		while (usedLabelNumbers.Contains(profileNumber))
		{
			profileNumber++;
		}

		return new BrowserProfileIdentity(
			profileId,
			"Profile " + profileNumber.ToString(CultureInfo.InvariantCulture));
	}

	private static int? TryGetProfileNumber(string label)
	{
		const string prefix = "Profile ";
		return label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
			int.TryParse(label[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
			number >= 2
			? number
			: null;
	}

	private static bool IsAllowedProfileCharacter(char value)
	{
		return value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or
			'#' or '@' or '$' or '(' or ')' or '+' or '-' or '_' or '~' or '.' or ' ';
	}
}
