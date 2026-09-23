using System;
using System.Collections.Generic;

namespace QuickPanel.Models;

public sealed class CodexResetSettings
{
	public DateTimeOffset? WindowStartedAt { get; init; }

	public DateTimeOffset? WindowEndedAt { get; init; }

	public DateTimeOffset? WeeklyResetAt { get; init; }

	public DateTimeOffset? ResetExpiresAt { get; init; }

	public int BankedResets { get; init; }

	public int TokensUsed { get; init; }

	public int TokenLimit { get; init; }

	public string? Notes { get; init; }
}

public sealed class TripModeSettings
{
	public string? SelectedTripId { get; init; }

	public List<TripPlan> Trips { get; init; } = new List<TripPlan>();
}

public sealed class TripPlan
{
	public string Id { get; init; } = Guid.NewGuid().ToString("N");

	public string Name { get; init; } = "Trip";

	public string? Origin { get; init; }

	public string? Destination { get; init; }

	public DateTime? StartDate { get; init; }

	public DateTime? EndDate { get; init; }

	public double? DistanceKm { get; init; }

	public double? FuelConsumptionLitresPer100Km { get; init; }

	public double? FuelPricePerLitre { get; init; }

	public double? RoadCosts { get; init; }

	public double? AccommodationCosts { get; init; }

	public double? OtherCosts { get; init; }

	public double? DailyBudgetAmount { get; init; }

	public string? BudgetCurrency { get; init; }

	public List<TripChecklistItem> PackingItems { get; init; } = new List<TripChecklistItem>();

	public string? SavedMapsLinks { get; init; }

	public string? CampingLinks { get; init; }

	public string? ParkingSpots { get; init; }

	public string? FuelEstimate { get; init; }

	public string? DailyBudget { get; init; }

	public string? EmergencyDocs { get; init; }

	public string? OfflineNotes { get; init; }
}

public sealed class TripChecklistItem
{
	public string Text { get; init; } = string.Empty;

	public bool IsPacked { get; init; }
}
