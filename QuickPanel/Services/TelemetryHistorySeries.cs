using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickPanel.Services;

public sealed class TelemetryHistorySeries
{
	private readonly int _maxSamples;
	private readonly double _ceiling;
	private readonly Queue<double> _normalizedValues = [];

	public TelemetryHistorySeries(int maxSamples, double ceiling)
	{
		if (maxSamples < 2) throw new ArgumentOutOfRangeException(nameof(maxSamples));
		if (!double.IsFinite(ceiling) || ceiling <= 0.0) throw new ArgumentOutOfRangeException(nameof(ceiling));
		_maxSamples = maxSamples;
		_ceiling = ceiling;
	}

	public IReadOnlyList<double> NormalizedValues => _normalizedValues.ToArray();

	public void Add(double? value)
	{
		if (!value.HasValue || !double.IsFinite(value.Value) || value.Value < 0.0) return;
		_normalizedValues.Enqueue(Math.Clamp(value.Value / _ceiling, 0.0, 1.0));
		while (_normalizedValues.Count > _maxSamples)
		{
			_normalizedValues.Dequeue();
		}
	}
}
