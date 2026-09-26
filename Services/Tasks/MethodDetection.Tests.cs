using Coflnet.Sky.PlayerState.Models;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class MethodDetectionTests
{
    private static Period MakePeriod(string location, long profit, Dictionary<string, int> items, int minutesDuration = 5)
    {
        var start = new DateTime(2025, 7, 24, 12, 0, 0);
        return new Period
        {
            PlayerUuid = "test",
            Server = "m1",
            Location = location,
            Profit = profit,
            StartTime = start,
            EndTime = start.AddMinutes(minutesDuration),
            ItemsCollected = items
        };
    }

    private static TaskParams MakeParams(params Period[] periods)
    {
        return new TaskParams
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
    }

    private static TaskParams MakeParamsWithTime(DateTime testTime, params Period[] periods)
    {
        var parameters = MakeParams(periods);
        parameters.TestTime = testTime;
        return parameters;
    }

    // ── Mob detection via SHARD_ items ──

    [Test]
    public async Task CinderbatDetected_ByShard()
    {
        var period = MakePeriod("Dive-Ember Pass", 500_000, new()
        {
            { "SHARD_CINDER_BAT", 42 },
            { "AGATHA_COUPON", 10 }
        });
        var task = new CinderbatTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Cinderbat");
        result.Details.Should().Contain("SHARD_CINDER_BAT");
    }

    [Test]
    public async Task BurningsoulDetected_AsInfernoDemonlord_AtSmolderingTomb()
    {
        // SHARD_BURNINGSOUL now drops from the Inferno Demonlord boss (Blaze Slayer) on the
        // Crimson Isle, not from any Galatea ember mob - see BurningsoulTask.
        var period = MakePeriod("Smoldering Tomb", 400_000, new()
        {
            { "SHARD_BURNINGSOUL", 30 },
            { "AGATHA_COUPON", 8 }
        });

        var burningsoul = new BurningsoulTask();
        var bResult = await burningsoul.Execute(MakeParams(period));
        bResult.ProfitPerHour.Should().BeGreaterThan(0);
        bResult.Name.Should().Be("Inferno Demonlord");

        // Cinderbat (a Galatea ember mob) should NOT detect a Crimson Isle period
        var cinderbat = new CinderbatTask();
        var cResult = await cinderbat.Execute(MakeParams(period));
        cResult.ProfitPerHour.Should().Be(0, "Cinderbat only matches Galatea ember zones, not the Crimson Isle");
    }

    /// <summary>
    /// Regression: BurningsoulTask (Inferno Demonlord / Blaze Slayer) used to list the bare island
    /// name "Crimson Isle" in its Locations, so SkyblockZones.Matches (island-level fallback) let
    /// ANY Crimson Isle zone count as Inferno Demonlord progress - e.g. Mycelium mining in Mystic
    /// Marsh or fishing in Scarleton/Oasis. It must only match the actual combat zone
    /// (Smoldering Tomb - see InfernoDemonlordGuide) plus its other specifically-declared zones.
    /// </summary>
    [TestCase("Mystic Marsh")]
    [TestCase("Scarleton")]
    [TestCase("Oasis")]
    public async Task BurningsoulTask_DoesNotMatchUnrelatedCrimsonIsleZones(string unrelatedZone)
    {
        var period = MakePeriod(unrelatedZone, 400_000, new() { { "SHARD_BURNINGSOUL", 30 } });
        var result = await new BurningsoulTask().Execute(MakeParams(period));
        result.ProfitPerHour.Should().Be(0,
            $"{unrelatedZone} is a Crimson Isle zone but not where Inferno Demonlord is fought");
    }

    /// <summary>
    /// Regression for the same bug as above, checked across every registered slayer task at once:
    /// none of IndividualSlayerTask's LocationNames (T3/T4 Inferno Demonlord, T5/T4 Tarantula,
    /// Ashfang, Barbarian Duke X) or the known slayer-boss-derived MethodTasks (BurningsoulTask,
    /// T4 Voidglooms(/FD)) may be a bare island or multi-island-group key - SkyblockZones.Matches
    /// would then let every zone of that island/group count as the slayer.
    /// </summary>
    [Test]
    public void NoSlayerTaskLocation_IsABareIslandOrGroupKey()
    {
        var registry = new TaskRegistry();
        var islandOrGroupKeys = new HashSet<string>(SkyblockZones.IslandInfo.Keys, StringComparer.Ordinal) { "Galatea" };

        void AssertNoIslandLocations(string taskName, IEnumerable<string> locations)
        {
            foreach (var loc in locations)
                islandOrGroupKeys.Should().NotContain(loc,
                    $"{taskName} lists \"{loc}\" as a Location, but it is a whole island/group key - " +
                    "SkyblockZones.Matches would then match EVERY zone of that island/group for this slayer");
        }

        foreach (var slayer in registry.Tasks.OfType<IndividualSlayerTask>())
            AssertNoIslandLocations(slayer.GetType().Name, slayer.LocationNamesForTest);

        var slayerDerivedMethodTasks = new[] { "Inferno Demonlord", "T4 Voidglooms", "T4 Voidglooms (FD)" };
        foreach (var method in registry.MethodTasks.Where(t => slayerDerivedMethodTasks.Contains(t.GetDetectionSignature().MethodName)))
            AssertNoIslandLocations(method.GetDetectionSignature().MethodName, method.GetDetectionSignature().Locations);
    }

    [Test]
    public async Task DrownedDetected_AtDrownedReliquary()
    {
        var period = MakePeriod("Drowned Reliquary", 600_000, new()
        {
            { "SHARD_DROWNED", 55 },
            { "DEEP_ROOT", 3 }
        });
        var task = new DrownedTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Drowned");
    }

    // ── Fishing vs Fishing-Hunting distinction ──

    [Test]
    public async Task RegularFishing_ExcludesShardsFromResults()
    {
        // A fishing session that has SHARD_ items = hunting, not regular fishing
        var huntingPeriod = MakePeriod("Piscary", 300_000, new()
        {
            { "RAW_FISH", 200 },
            { "SHARD_SQUID", 5 }
        });

        var regularTask = new PiscaryFishingTask();
        var result = await regularTask.Execute(MakeParams(huntingPeriod));
        // Should NOT match because ExcludeShardItems is true and period has SHARD_ items
        result.ProfitPerHour.Should().Be(0, "Regular fishing should exclude periods with SHARD_ items");
    }

    [Test]
    public async Task RegularFishing_MatchesPeriodWithoutShards()
    {
        var normalPeriod = MakePeriod("Piscary", 200_000, new()
        {
            { "RAW_FISH", 300 },
            { "ENCHANTED_RAW_FISH", 25 }
        });

        var regularTask = new PiscaryFishingTask();
        var result = await regularTask.Execute(MakeParams(normalPeriod));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Piscary Fishing");
    }

    [Test]
    public async Task HuntingFishing_RequiresShardItems()
    {
        var huntingPeriod = MakePeriod("Piscary", 500_000, new()
        {
            { "RAW_FISH", 150 },
            { "SHARD_SQUID", 8 }
        });

        var huntingTask = new PiscaryFishingHuntingTask();
        var result = await huntingTask.Execute(MakeParams(huntingPeriod));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Contain("Hunting");
    }

    [Test]
    public async Task HuntingFishing_RejectsPeriodsWithoutShards()
    {
        var normalPeriod = MakePeriod("Piscary", 200_000, new()
        {
            { "RAW_FISH", 300 },
            { "ENCHANTED_RAW_FISH", 25 }
        });

        var huntingTask = new PiscaryFishingHuntingTask();
        var result = await huntingTask.Execute(MakeParams(normalPeriod));
        // No matching periods (requires shards but none present) and no prices → 0 profit
        result.ProfitPerHour.Should().Be(0);
    }

    // ── Hunting tasks (non-fishing) ──

    [Test]
    public async Task YogHunting_DetectedByShard()
    {
        var period = MakePeriod("Magma Fields", 800_000, new()
        {
            { "SHARD_YOG", 35 },
            { "ENCHANTED_MAGMA_CREAM", 12 }
        });
        var task = new YogHuntingTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Yog (Hunting)");
    }

    [Test]
    public async Task GhostHunting_DetectedByGhostShard()
    {
        var period = MakePeriod("The Mist", 1_200_000, new()
        {
            { "SHARD_GHOST", 450 }
        });
        var task = new GhostHuntingTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Ghost (Hunting)");
    }

    // ── Diana tasks ──

    [Test]
    public async Task Diana_DetectedByGriffinFeather()
    {
        var period = MakePeriod("Hub", 2_000_000, new()
        {
            { "GRIFFIN_FEATHER", 15 },
            { "ENCHANTED_GOLD", 8 }
        });
        var task = new DianaTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Diana");
    }

    [Test]
    public async Task DianaHunting_DetectedByKingMinos()
    {
        var period = MakePeriod("Wilderness", 5_000_000, new()
        {
            { "SHARD_KING_MINOS", 3 },
            { "GRIFFIN_FEATHER", 20 },
            { "DAEDALUS_STICK", 1 }
        });
        var task = new DianaHuntingTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Diana (Hunting)");
    }

    [Test]
    public async Task DianaTask_IsUnavailable_WhenDifferentMayorIsActive()
    {
        var task = new DianaTask();
        var parameters = MakeParams();
        parameters.CurrentMayor = "derpy";

        var result = await task.Execute(parameters);

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Contain("Diana");
    }

    [Test]
    public async Task LimitedTask_SetsNextAvailableAt_WhenRecentlyCompleted()
    {
        var start = new DateTime(2025, 7, 24, 10, 0, 0, DateTimeKind.Utc);
        var period = new Period
        {
            PlayerUuid = "test",
            Server = "m1",
            Location = "Hub",
            Profit = 100_000,
            StartTime = start,
            EndTime = start.AddMinutes(5),
            ItemsCollected = new Dictionary<string, int> { { "GRAND_EXP_BOTTLE", 3 } }
        };
        var task = new ExperimentationTableTask();

        var result = await task.Execute(MakeParamsWithTime(start.AddHours(1), period));

        result.IsAccessible.Should().BeFalse();
        result.NextAvailableAt.Should().Be(start.AddMinutes(5).AddHours(24));
    }

    // PrepareTaskResult test stays in SkyModCommands, it covers the command layer.

    // ── Mining tasks ──

    [Test]
    public async Task JadeMining_DetectedByJadeGem()
    {
        var period = MakePeriod("Crystal Hollows", 1_500_000, new()
        {
            { "ROUGH_JADE_GEM", 500 },
            { "FINE_JADE_GEM", 20 }
        });
        var task = new JadeMiningTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Jade Mining");
    }

    // ── Multiple periods aggregation ──

    [Test]
    public async Task MultiplePeriodsAggregated_ForSameMob()
    {
        var p1 = MakePeriod("Dive-Ember Pass", 300_000, new() { { "SHARD_CINDER_BAT", 30 } }, 5);
        var p2 = MakePeriod("Stride-Ember Fissure", 400_000, new() { { "SHARD_CINDER_BAT", 40 } }, 5);

        var task = new CinderbatTask();
        var result = await task.Execute(MakeParams(p1, p2));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        // Both periods contribute to total
        result.Details.Should().Contain("SHARD_CINDER_BAT");
    }

    // ── Overlapping locations resolved by items ──

    [Test]
    public async Task OverlappingLocations_ResolvedByDetectionItems()
    {
        // Dive-Ember Pass is shared by Cinderbat and Stridersurfer (Burningsoul/Inferno Demonlord
        // moved to the Crimson Isle Blaze Slayer boss - see BurningsoulDetected_AsInfernoDemonlord_AtSmolderingTomb)
        var cinderbatPeriod = MakePeriod("Dive-Ember Pass", 500_000, new() { { "SHARD_CINDER_BAT", 40 } });
        var stridersurferPeriod = MakePeriod("Stride-Ember Fissure", 600_000, new() { { "SHARD_STRIDER_SURFER", 50 } });

        var allPeriods = new[] { cinderbatPeriod, stridersurferPeriod };
        var p = MakeParams(allPeriods);

        // Each task should only pick up its own periods
        var cinderbatResult = await new CinderbatTask().Execute(p);
        var stridersurferResult = await new StridersurferTask().Execute(p);

        cinderbatResult.ProfitPerHour.Should().BeGreaterThan(0);
        stridersurferResult.ProfitPerHour.Should().BeGreaterThan(0);

        // Each should only see its own items
        cinderbatResult.Details.Should().Contain("SHARD_CINDER_BAT");
        stridersurferResult.Details.Should().Contain("SHARD_STRIDER_SURFER");
    }

    // ── Dungeon tasks ──

    [Test]
    public async Task M7KismetDetection()
    {
        var period = MakePeriod("The Catacombs", 10_000_000, new()
        {
            { "KISMET_FEATHER", 3 },
            { "ENCHANTED_DIAMOND", 20 }
        }, 15);
        var task = new M7KismetTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("M7 (Kismet)");
    }

    // ── Galatea diving vs Galatea mob disambiguation ──

    [Test]
    public async Task GalateaDiving_MatchesGalateaLocations()
    {
        // GalateaDivingTask is an IslandTask that matches all diving locations
        // Mob tasks use SHARD_ detection to disambiguate
        var period = MakePeriod("Drowned Reliquary", 500_000, new()
        {
            { "SHARD_DROWNED", 40 },
            { "DEEP_ROOT", 5 }
        });

        // DrownedTask detects by SHARD_DROWNED
        var drownedResult = await new DrownedTask().Execute(MakeParams(period));
        drownedResult.ProfitPerHour.Should().BeGreaterThan(0);
        drownedResult.Name.Should().Be("Drowned");
    }

    // ── No data falls back to formula ──

    [Test]
    public async Task FormulaFallback_WhenNoPlayerData()
    {
        // Empty location profit = no matching periods
        var p = new TaskParams
        {
            TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            MaxAvailableCoins = 1_000_000_000,
            LocationProfit = new Dictionary<string, Period[]>(),
            CleanPrices = new Dictionary<string, long>
            {
                { "SHARD_CINDER_BAT", 2000 }
            },
            BazaarPrices = [],
            Names = new Dictionary<string, string>
            {
                { "SHARD_CINDER_BAT", "Cinderbat Shard" }
            }
        };

        var task = new CinderbatTask();
        var result = await task.Execute(p);
        // With price data, formula should give an estimate
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Details.Should().Contain("Estimated");
    }

    // ── Slayer tasks ──

    [Test]
    public async Task VoidgloomsDetection()
    {
        // "Void Sepulture" - the wiki-confirmed intended zone for Enderman Slayer (Voidgloom Seraph)
        // grinding. Not the bare "The End" main zone: that used to be in T4VoidgloomsTask's
        // Locations too, but since it's the island's own self-referencing zone name, it also
        // (via SkyblockZones.Matches' island fallback) wrongly matched every other The End zone,
        // e.g. Zealot Bruiser Hideout's unrelated Summoning Eye farming - see the Inferno Demonlord
        // regression test below for the same class of bug.
        var period = MakePeriod("Void Sepulture", 3_000_000, new()
        {
            { "SUMMONING_EYE", 5 },
            { "ENCHANTED_OBSIDIAN", 15 }
        }, 10);
        var task = new T4VoidgloomsTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("T4 Voidglooms");
    }

    /// <summary>
    /// Regression (corrected 2026-09, supersedes an earlier test with the opposite assertion):
    /// IndividualSlayerTask.Execute routes through SkyblockZones.Matches, so a slayer task's
    /// Locations must never include a bare island name (here "Crimson Isle") - Matches' island
    /// fallback would then let ANY Crimson Isle sub-zone count as this slayer, e.g. Mycelium mining
    /// in Mystic Marsh (a totally unrelated activity - no Blaze/Inferno Demonlord connection at
    /// all) being counted as Inferno Demonlord Blaze Slayer progress.
    /// </summary>
    [Test]
    public async Task InfernoDemonlordNotDetected_AtUnrelatedCrimsonIsleSubZone()
    {
        var period = MakePeriod("Mystic Marsh", 5_000_000, new() { { "BLAZE_ROD", 500 } }, 20);
        var task = new T4InfernoDemonlordTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().Be(0,
            "Mystic Marsh is a Crimson Isle sub-zone unrelated to the Inferno Demonlord boss (fought in the Smoldering Tomb) " +
            "and must not match just because \"Crimson Isle\" used to be a bare island name in LocationNames");
    }

    /// <summary>
    /// The actual combat zone must still match, of course - Stronghold/Smoldering Tomb/The Bastion
    /// remain explicitly listed in T4InfernoDemonlordTask.LocationNames after removing the bare
    /// "Crimson Isle" island name.
    /// </summary>
    [Test]
    public async Task InfernoDemonlordDetected_AtSmolderingTomb()
    {
        var period = MakePeriod("Smoldering Tomb", 5_000_000, new() { { "BLAZE_ROD", 500 } }, 20);
        var task = new T4InfernoDemonlordTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0, "Smoldering Tomb is explicitly listed and is where Inferno Demonlord is fought");
    }

    // ── Misc tasks ──

    [Test]
    public async Task ZealotsFd_DetectedByEnderPearl()
    {
        var period = MakePeriod("Dragon's Nest", 2_000_000, new()
        {
            { "ENDER_PEARL", 50 },
            { "SUMMONING_EYE", 2 }
        }, 10);
        var task = new ZealotsFdTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Zealots (FD)");
    }
}
