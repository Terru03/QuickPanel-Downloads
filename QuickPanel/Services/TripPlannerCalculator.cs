using System;

namespace QuickPanel.Services;

public sealed class TripEstimate
{
	public int Days { get; init; }

	public bool DateRangeValid { get; init; }

	public double FuelLitres { get; init; }

	public double FuelCost { get; init; }

	public double DailyBudgetCost { get; init; }

	public double TotalCost { get; init; }
}

public static class TripPlannerCalculator
{
	public static TripEstimate Calculate(
		DateTime? startDate,
		DateTime? endDate,
		double? distanceKm,
		double? consumptionLitresPer100Km,
		double? fuelPricePerLitre,
		double? dailyBudget,
		double? roadCosts,
		double? accommodationCosts,
		double? otherCosts)
	{
		bool dateRangeValid = true;
		int days = 1;
		if (startDate != null && endDate != null)
		{
			if (endDate.Value.Date < startDate.Value.Date)
			{
				dateRangeValid = false;
				days = 0;
			}
			else
			{
				days = (endDate.Value.Date - startDate.Value.Date).Days + 1;
			}
		}

		double distance = PositiveOrZero(distanceKm);
		double consumption = PositiveOrZero(consumptionLitresPer100Km);
		double fuelPrice = PositiveOrZero(fuelPricePerLitre);
		double fuelLitres = distance * consumption / 100.0;
		double fuelCost = fuelLitres * fuelPrice;
		double dailyBudgetCost = PositiveOrZero(dailyBudget) * days;
		double totalCost = fuelCost +
			dailyBudgetCost +
			PositiveOrZero(roadCosts) +
			PositiveOrZero(accommodationCosts) +
			PositiveOrZero(otherCosts);

		return new TripEstimate
		{
			Days = days,
			DateRangeValid = dateRangeValid,
			FuelLitres = fuelLitres,
			FuelCost = fuelCost,
			DailyBudgetCost = dailyBudgetCost,
			TotalCost = totalCost
		};
	}

	private static double PositiveOrZero(double? value)
	{
		return value is > 0 ? value.Value : 0.0;
	}
}
