using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed record TabIconFallbackDescriptor(string Monogram, string BackgroundHex);

public static class TabIconResolver
{
	private static readonly string[] FallbackPalette =
	[
		"#365E9D", "#6A4C93", "#287271", "#9C4A5A", "#8A5A2B", "#3D5A80", "#7A5195", "#2F6F6D"
	];
	private static readonly IReadOnlyDictionary<string, string> BrandAliases =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["anthropic"] = "anthropic",
			["chatgpt"] = "openai",
			["claude"] = "anthropic",
			["codex"] = "openai",
			["gemini"] = "googlegemini",
			["google gemini"] = "googlegemini",
			["googlegemini"] = "googlegemini",
			["grok"] = "x",
			["openai"] = "openai",
			["perplexity"] = "perplexity",
			["reddit"] = "reddit",
			["x"] = "x",
			["xai"] = "x"
		};

	private static readonly IReadOnlyDictionary<string, string> HostBrands =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["anthropic.com"] = "anthropic",
			["chatgpt.com"] = "openai",
			["claude.ai"] = "anthropic",
			["gemini.google.com"] = "googlegemini",
			["grok.com"] = "x",
			["openai.com"] = "openai",
			["perplexity.ai"] = "perplexity",
			["reddit.com"] = "reddit",
			["x.ai"] = "x",
			["x.com"] = "x"
		};

	public static string? ResolveBrandKey(AiTab tab)
	{
		if (TryResolveBrandAlias(tab.Icon, out string? explicitBrand))
		{
			return explicitBrand;
		}
		if (Uri.TryCreate(tab.Url, UriKind.Absolute, out Uri? uri))
		{
			string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
				? uri.Host[4..]
				: uri.Host;
			foreach ((string domain, string brand) in HostBrands)
			{
				if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
					host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
				{
					return brand;
				}
			}
		}
		return TryResolveBrandAlias(tab.Name, out string? nameBrand) ? nameBrand : null;
	}

	public static bool HasManualOverride(AiTab tab)
	{
		ArgumentNullException.ThrowIfNull(tab);
		return !string.IsNullOrWhiteSpace(tab.Icon);
	}

	public static TabIconFallbackDescriptor CreateFallbackDescriptor(AiTab tab)
	{
		ArgumentNullException.ThrowIfNull(tab);
		string candidate = tab.Name;
		if (Uri.TryCreate(tab.Url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host))
		{
			candidate = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
		}
		char monogram = candidate.FirstOrDefault(char.IsLetterOrDigit);
		string text = monogram == default ? "?" : char.ToUpperInvariant(monogram).ToString();
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.ToLowerInvariant()));
		return new TabIconFallbackDescriptor(text, FallbackPalette[hash[0] % FallbackPalette.Length]);
	}

	public static string CreateSimpleIconsSlug(AiTab tab)
	{
		if (Uri.TryCreate(tab.Url, UriKind.Absolute, out Uri? uri))
		{
			string[] parts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
			string candidate = parts.FirstOrDefault(part =>
				!part.Equals("www", StringComparison.OrdinalIgnoreCase) &&
				!part.Equals("app", StringComparison.OrdinalIgnoreCase)) ?? tab.Name;
			return NormalizeSlug(candidate);
		}
		return NormalizeSlug(tab.Name);
	}

	public static bool TryResolveBrandAlias(string? value, out string? brand)
	{
		brand = null;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		return BrandAliases.TryGetValue(value.Trim(), out brand);
	}

	public static string NormalizeIconSlug(string value)
	{
		return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
	}

	private static string NormalizeSlug(string value)
	{
		return NormalizeIconSlug(value);
	}
}
