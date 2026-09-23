using System;
using System.Collections.Generic;
using QuickPanel.Models;

namespace QuickPanel.Services;

public sealed class ClosedTabHistory
{
	private const int DefaultCapacity = 10;

	private readonly List<ClosedTabEntry> _entries = new List<ClosedTabEntry>();

	private readonly int _capacity;

	public ClosedTabHistory(int capacity = DefaultCapacity)
	{
		if (capacity < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");
		}
		_capacity = capacity;
	}

	public int Count => _entries.Count;

	public IReadOnlyList<ClosedTabEntry> Entries => _entries;

	public void Load(IEnumerable<ClosedTabEntry> entries)
	{
		_entries.Clear();
		foreach (ClosedTabEntry entry in entries)
		{
			Push(entry);
		}
	}

	public void Push(AiTab tab, int previousIndex)
	{
		if (!tab.IsCustom)
		{
			return;
		}
		Push(new ClosedTabEntry(CloneAsCustom(tab), Math.Max(0, previousIndex), DateTimeOffset.UtcNow));
	}

	public void Push(ClosedTabEntry entry)
	{
		_entries.Add(entry);
		if (_entries.Count > _capacity)
		{
			_entries.RemoveAt(0);
		}
	}

	public bool TryPop(out ClosedTabEntry? entry)
	{
		if (_entries.Count == 0)
		{
			entry = null;
			return false;
		}
		int lastIndex = _entries.Count - 1;
		entry = _entries[lastIndex];
		_entries.RemoveAt(lastIndex);
		return true;
	}

	private static AiTab CloneAsCustom(AiTab tab)
	{
		return new AiTab
		{
			Id = tab.Id,
			Name = tab.Name,
			Url = tab.Url,
			Icon = tab.Icon,
			BrowserProfileId = tab.BrowserProfileId,
			BrowserProfileLabel = tab.BrowserProfileLabel,
			IsPinned = tab.IsPinned,
			IsCustom = true
		};
	}
}

public sealed record ClosedTabEntry(AiTab Tab, int PreviousIndex, DateTimeOffset ClosedAt);
