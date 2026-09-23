using System.Collections.Generic;

namespace QuickPanel.Models;

public sealed class BrowserProfileDefinition
{
	public required string Id { get; init; }

	public required string Name { get; init; }

	public bool IsDefault { get; init; }
}

public sealed class BrowserProfileRegistryDocument
{
	public List<BrowserProfileDefinition> Profiles { get; set; } = new();

	public List<string> PendingDeletionProfileIds { get; set; } = new();
}
