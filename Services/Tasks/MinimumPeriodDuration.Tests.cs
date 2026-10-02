using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// <see cref="MethodTask.MinimumPeriodDuration"/>: claim-type tasks only track the seconds of claiming,
/// so each period counts for at least the time the reward took to earn.
/// </summary>
public class MinimumPeriodDurationTests
{
    private static Period MakePeriod(string location, long profit, TimeSpan duration, Dictionary<string, int> items)
    {
        var start = new DateTime(2025, 7, 24, 12, 0, 0);
        return new Period
        {
            PlayerUuid = "test", Server = "m1", Location = location, Profit = profit,
            StartTime = start, EndTime = start + duration, ItemsCollected = items
        };
    }

    private static TaskParams MakeParams(params Period[] periods) => new()
    {
        TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        MaxAvailableCoins = 1_000_000_000,
        LocationProfit = periods.GroupBy(l => l.Location).ToDictionary(l => l.Key, l => l.ToArray()),
        CleanPrices = new Dictionary<string, long>(),
        BazaarPrices = [],
        Names = new Dictionary<string, string>()
    };

    [Test]
    public void EffectiveDuration_ShortPeriodCountsAsTheFloor()
        => MethodTask.EffectiveDuration(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(15))
            .Should().Be(TimeSpan.FromMinutes(15));

    [Test]
    public void EffectiveDuration_LongerPeriodIsUnchanged()
        => MethodTask.EffectiveDuration(TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(15))
            .Should().Be(TimeSpan.FromMinutes(40));

    [Test]
    public void EffectiveDuration_ZeroFloorKeepsTheRealDuration()
        => MethodTask.EffectiveDuration(TimeSpan.FromSeconds(20), TimeSpan.Zero)
            .Should().Be(TimeSpan.FromSeconds(20));

    [Test]
    public void TaskWithoutAFloor_UsesTheRealPeriodDuration()
    {
        var period = MakePeriod("The Garden", 0, TimeSpan.FromSeconds(20), []);
        new M7Task().EffectiveDuration(period).Should().Be(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task GardenVisitors_TwentySecondPeriodOfOnePointFiveMillion_IsSixMillionPerHour()
    {
        var period = MakePeriod("Garden", 1_500_000, TimeSpan.FromSeconds(20), new() { { "JACOBS_TICKET", 3 } });

        var result = await new GardenVisitorsTask().Execute(MakeParams(period));

        result.ProfitPerHour.Should().Be(6_000_000, "20s counts as the 15 minute visitor interval, not 270M/h");
        result.Breakdown.TrackedHours.Should().BeApproximately(0.25, 1e-9);
        result.Breakdown.Drops.Single(d => d.ItemTag == "JACOBS_TICKET").RatePerHour.Should().BeApproximately(12, 1e-9);
    }

    [Test]
    public async Task GardenVisitors_PeriodLongerThanTheFloor_IsUnchanged()
    {
        var period = MakePeriod("Garden", 3_000_000, TimeSpan.FromMinutes(30), new() { { "JACOBS_TICKET", 3 } });

        var result = await new GardenVisitorsTask().Execute(MakeParams(period));

        result.ProfitPerHour.Should().Be(6_000_000);
        result.Breakdown.TrackedHours.Should().BeApproximately(0.5, 1e-9);
    }

    [Test]
    public void EffectiveHours_ClaimsCloseTogether_DoNotEachCountAFullFloor()
    {
        // 12:00:00-12:00:20, 12:02:00-12:02:20 and 12:05:00-12:05:20: one 15 minute interval before
        // the first claim, then only the time since the previous claim - not 3 x 15 minutes
        var start = new DateTime(2025, 7, 24, 12, 0, 0);
        var periods = new[] { 0, 120, 300 }.Select(offset => new Period
        {
            StartTime = start.AddSeconds(offset), EndTime = start.AddSeconds(offset + 20)
        }).Reverse().ToList();

        MethodTask.EffectiveHours(periods, TimeSpan.FromMinutes(15))
            .Should().BeApproximately((15 + 2 + 3) / 60d, 1e-9);
    }

    [Test]
    public void EffectiveHours_WithoutAFloor_IsThePlainSumEvenForOverlappingPeriods()
    {
        var start = new DateTime(2025, 7, 24, 12, 0, 0);
        var periods = new[]
        {
            new Period { StartTime = start, EndTime = start.AddMinutes(10) },
            new Period { StartTime = start.AddMinutes(5), EndTime = start.AddMinutes(12) }
        };

        MethodTask.EffectiveHours(periods, TimeSpan.Zero).Should().BeApproximately(17 / 60d, 1e-9);
    }

    [Test]
    public void KuudraChestClaims_FloorIsOneRunDerivedFromActionsPerHour()
    {
        var period = MakePeriod("Dungeon Hub", 0, TimeSpan.FromSeconds(10), []);
        new KuudraChestClaimsTask().EffectiveDuration(period).Should().Be(TimeSpan.FromMinutes(7.5));
    }

    [Test]
    public void GardenVisitors_FoldAggregatePath_CountsTheFloorOncePerPeriod()
    {
        // TaskPeriodFolder.Fold takes its minutes from MethodTask.EffectiveDuration(period)
        var task = new GardenVisitorsTask();
        var periods = new[]
        {
            MakePeriod("Garden", 0, TimeSpan.FromSeconds(20), []),
            MakePeriod("Garden", 0, TimeSpan.FromMinutes(20), [])
        };
        periods.Select(p => task.EffectiveDuration(p).TotalMinutes).Should().Equal(15, 20);
    }
}
