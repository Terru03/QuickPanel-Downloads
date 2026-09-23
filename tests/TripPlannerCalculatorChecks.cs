using System;
using System.Runtime.CompilerServices;
using QuickPanel.Services;

internal static class TripPlannerCalculatorChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        TripEstimate estimate = TripPlannerCalculator.Calculate(
            new DateTime(2026, 8, 17),
            new DateTime(2026, 8, 19),
            distanceKm: 1000,
            consumptionLitresPer100Km: 6,
            fuelPricePerLitre: 1.5,
            dailyBudget: 50,
            roadCosts: 20,
            accommodationCosts: 200,
            otherCosts: 40);

        Need(estimate.DateRangeValid, "Valid trip dates were rejected.");
        Need(estimate.Days == 3, "Trip day count must be inclusive.");
        Need(Math.Abs(estimate.FuelLitres - 60) < 0.001, "Fuel litres estimate is incorrect.");
        Need(Math.Abs(estimate.FuelCost - 90) < 0.001, "Fuel cost estimate is incorrect.");
        Need(Math.Abs(estimate.DailyBudgetCost - 150) < 0.001, "Daily budget estimate is incorrect.");
        Need(Math.Abs(estimate.TotalCost - 500) < 0.001, "Total trip estimate is incorrect.");

        TripEstimate invalidDates = TripPlannerCalculator.Calculate(
            new DateTime(2026, 8, 20),
            new DateTime(2026, 8, 19),
            distanceKm: 0,
            consumptionLitresPer100Km: 0,
            fuelPricePerLitre: 0,
            dailyBudget: 0,
            roadCosts: 0,
            accommodationCosts: 0,
            otherCosts: 0);

        Need(!invalidDates.DateRangeValid && invalidDates.Days == 0,
            "End date before start date must be rejected.");
    }

    private static void Need(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}
