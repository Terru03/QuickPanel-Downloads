using System;
using System.Collections.Generic;

namespace QuickPanel.Services;

public static class NativePanelIconCatalog
{
	private static readonly IReadOnlyDictionary<string, string> Glyphs =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["native:codex-usage"] = "\uE9D2",
			["native:windows-helper"] = "\uE90F",
			["native:trip-planner"] = "\uE707",
			["native:task-manager"] = "\uF120"
		};

	public static string GetGlyph(string tabId)
	{
		return Glyphs.TryGetValue(tabId, out string? glyph)
			? glyph
			: throw new ArgumentOutOfRangeException(nameof(tabId), tabId, "Unknown native panel icon.");
	}
}
