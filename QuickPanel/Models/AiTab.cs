using System.Text.Json.Serialization;

namespace QuickPanel.Models;

public sealed class AiTab
{
	public string? Id { get; init; }

	public required string Name { get; init; }

	public required string Url { get; init; }

	public string? Icon { get; init; }

	public string? BrowserProfileId { get; init; }

	public string? BrowserProfileLabel { get; init; }

	public bool IsPinned { get; init; }

	[JsonIgnore]
	public bool IsCustom { get; init; }
}
