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

        // The renamed Inferno Demonlord should still detect the BURNINGSOUL tag; the display name
        // comes straight from Names (sourced from skyitems), not a locally hardcoded mapping.
        var parameters = MakeParams(period);
        parameters.Names["SHARD_BURNINGSOUL"] = "Inferno Demonlord Shard";
        var namedResult = await burningsoul.Execute(parameters);
        namedResult.Breakdown.Drops.Should().ContainSingle(d => d.ItemTag == "SHARD_BURNINGSOUL")
            .Which.Name.Should().Be("Inferno Demonlord Shard");

        // Cinderbat (a Galatea ember mob) should NOT detect a Crimson Isle period
        var cinderbat = new CinderbatTask();
        var cResult = await cinderbat.Execute(MakeParams(period));
        cResult.ProfitPerHour.Should().Be(0, "Cinderbat only matches Galatea ember zones, not the Crimson Isle");
    }

    /// <summary>
    /// Restored from ef9d4629 (fix #426): MethodTask.Execute falls back to a ServerEstimates key
    /// of the class-derived <see cref="ProfitTask.Name"/> ("Burningsoul") when no estimate exists
    /// under the current MethodName ("Inferno Demonlord"). This is a generic rollout-safety net
    /// (any MethodName rename), not a re-opened door for the old wrong-location data:
    /// SkyUserState's TaskAggregateService buckets aggregates by MethodName with a 14 day Cassandra
    /// TTL, so it stops emitting anything keyed "Burningsoul" the moment this fix ships and the old
    /// (Galatea-located) data simply ages out unread.
    /// </summary>
    [Test]
    public async Task InfernoDemonlordUsesLegacyBurningsoulServerEstimate()
    {
        var parameters = MakeParams();
        parameters.ServerEstimates = new()
        {
            ["Burningsoul"] = new TaskEstimate { CoinsPerHour = 1_000_000, Source = "global", Drops = [] }
        };

        var result = await new BurningsoulTask().Execute(parameters);

        result.Name.Should().Be("Inferno Demonlord");
        result.ProfitPerHour.Should().Be(1_000_000);
        result.Breakdown.Source.Should().Be("community_estimate");
    }

    // ── ServerEstimates branch routing / cost netting (MethodTask.Execute, ComputeFromServerEstimate,
    // ComputeFromFormula) - TaskEstimator.EstimateAll returns an estimate for every task including
    // Source == "formula" ones, so this pins that those never take the community-estimate branch,
    // and that a recipe task's ingredient costs (FormulaCosts) show up (and are netted exactly once,
    // upstream in TaskEstimator - see RateFromAggregate/RateFromPlayerStat/FormulaRate) regardless
    // of which branch actually renders the result. ──

    /// <summary>
    /// A Source == "formula" server estimate (TaskEstimator's cold-start tier, priced whenever
    /// FormulaDrops have prices) must fall through to ComputeFromFormula instead of taking the
    /// server-estimate branch: ComputeFromServerEstimate has no per-drop/per-cost breakdown for the
    /// formula tier (TaskEstimator.BuildDrops's cold-start path only surfaces FormulaDrops, never
    /// FormulaCosts) and would mislabel a pure formula number as "community_estimate". Gem Mixture
    /// is used because it actually has FormulaCosts (the 16 Fine gems per mixture) to assert on.
    /// </summary>
    [Test]
    public async Task GemMixture_FormulaSourcedServerEstimate_FallsThroughToFormula_WithCostsBreakdown()
    {
        var task = new SludgeMiningGemMixtureTask();
        var parameters = MakeParams();
        parameters.CleanPrices = new Dictionary<string, long>
        {
            ["GEMSTONE_MIXTURE"] = 500_000,
            ["HARD_STONE"] = 1,
            ["SLUDGE_JUICE"] = 20,
            // Cheap enough that the flat (unscaled) formula net stays positive - see the netPerHour
            // > 0 guard on the rescale in ComputeFromFormula - while still leaving a non-empty,
            // materially different-from-123456 flat total for the rescale to visibly act on.
            ["FINE_JADE_GEM"] = 1_000,
            ["FINE_AMBER_GEM"] = 1_000,
            ["FINE_AMETHYST_GEM"] = 1_000,
            ["FINE_SAPPHIRE_GEM"] = 1_000,
        };
        parameters.ServerEstimates = new()
        {
            ["Sludge Mining (Gem Mixture)"] = new TaskEstimate
            {
                TaskName = "Sludge Mining (Gem Mixture)",
                Source = "formula",
                // Stat-bucket/saturation adjusted figure TaskEstimator computed for this same
                // formula tier - ComputeFromFormula should rescale its own flat total to match this
                // (see the stat-aware rescale there) rather than discard it.
                CoinsPerHour = 123_456,
                Drops = []
            }
        };

        var result = await task.Execute(parameters);

        result.Breakdown.Source.Should().Be("formula",
            "a formula-sourced server estimate must fall through to ComputeFromFormula, not ComputeFromServerEstimate");
        result.Breakdown.Costs.Should().NotBeEmpty("Gem Mixture consumes Fine gems - the recipe cost must show up in the breakdown");
        result.Message.Should().Contain("(estimated)");
        result.ProfitPerHour.Should().Be(123_456,
            "ComputeFromFormula should rescale to the more accurate stat/saturation adjusted server figure");
    }

    /// <summary>
    /// A community/personal-bucket server estimate (Source != "formula") for a recipe task DOES
    /// take the ComputeFromServerEstimate branch. TaskEstimator now nets ScaledFormulaCost directly
    /// into the pooled community/personal rate (see RateFromAggregate/RateFromPlayerStat) from the
    /// same gross item counts TaskPeriodFolder.FoldDerivedTasks stores, so the estimate arrives here
    /// already net of ingredient cost - ComputeFromServerEstimate must render it as-is and only
    /// build a Costs list for display (still scaled to this estimate's own observed drop rate), not
    /// subtract it a second time.
    /// </summary>
    [Test]
    public async Task GemMixture_CommunityServerEstimate_IsAlreadyNet_AndDisplaysCosts()
    {
        var task = new SludgeMiningGemMixtureTask();
        var parameters = MakeParams();
        parameters.CleanPrices = new Dictionary<string, long>
        {
            ["FINE_JADE_GEM"] = 50_000,
            ["FINE_AMBER_GEM"] = 50_000,
            ["FINE_AMETHYST_GEM"] = 50_000,
            ["FINE_SAPPHIRE_GEM"] = 50_000,
        };
        const double observedMixturesPerHour = 0.5; // below the 1.25/h forge cap FormulaDrops assumes
        parameters.ServerEstimates = new()
        {
            ["Sludge Mining (Gem Mixture)"] = new TaskEstimate
            {
                TaskName = "Sludge Mining (Gem Mixture)",
                Source = "global",
                // Already net of ingredient cost - TaskEstimator subtracts ScaledFormulaCost at the
                // source now (see the doc comment on ComputeFromServerEstimate).
                CoinsPerHour = 700_000,
                Drops =
                [
                    new TaskDropRate
                    {
                        ItemTag = "GEMSTONE_MIXTURE", RatePerHour = observedMixturesPerHour,
                        PriceEach = 500_000, ContributionPerHour = observedMixturesPerHour * 500_000
                    }
                ]
            }
        };

        var result = await task.Execute(parameters);

        result.Breakdown.Source.Should().Be("community_estimate");
        result.Breakdown.Drops.Should().ContainSingle(d => d.ItemTag == "GEMSTONE_MIXTURE");
        result.Breakdown.Costs.Should().HaveCount(4, "all 4 Fine gem types feed the recipe, shown for display only");

        var costPerHour = result.Breakdown.Costs.Sum(c => -c.ContributionPerHour);
        // 4 gem types x (design rate 1.25/h x 4 per mixture) scaled by 0.5/1.25 x 50k each
        costPerHour.Should().BeApproximately(400_000, 50, "the displayed cost breakdown must still reflect the recipe");
        result.ProfitPerHour.Should().Be(700_000,
            "the server estimate is already net; ComputeFromServerEstimate must not subtract the displayed costs again");
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

    // ── Zone-to-island matching (SkyblockZones) ──
    // The scoreboard reports the sub-zone ("Jungle", "Goblin Holdout", "Dwarven Village", ...),
    // never the island name, for most islands - these pin FindMatchingPeriods to matching a
    // task's island-level Locations (e.g. ["Crystal Hollows"]) against that sub-zone.

    [Test]
    public async Task SludgeMining_MatchesJungleZone()
    {
        // Sludge Juice is mined in the Jungle sub-zone of the Crystal Hollows, not the
        // Dwarven Mines - regression for Locations that never matched any real period.
        var period = MakePeriod("Jungle", 400_000, new() { { "SLUDGE_JUICE", 40 } });
        var task = new SludgeMiningTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Sludge Mining");
    }

    [Test]
    public async Task GoblinHoldoutZone_MatchesTaskWithCrystalHollowsIslandLocation()
    {
        // ScathaMiningTask declares "Crystal Hollows" among its Locations; Goblin Holdout is a
        // Crystal Hollows sub-zone and must match via the zone-to-island map, not exact string.
        var period = MakePeriod("Goblin Holdout", 300_000, new() { { "PET_SCATHA", 1 } });
        var task = new ScathaMiningTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Scatha Mining");
    }

    [Test]
    public async Task DwarvenVillageZone_MatchesDwarvenMinesIslandLocation()
    {
        var period = MakePeriod("Dwarven Village", 200_000, new() { { "COAL", 50 } });
        var task = new CoalMiningTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().BeGreaterThan(0);
        result.Name.Should().Be("Coal Mining");
    }

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
    [TestCase("Mystic Marsh")]
    [TestCase("Scarleton")]
    public async Task InfernoDemonlordNotDetected_AtUnrelatedCrimsonIsleSubZone(string unrelatedZone)
    {
        var period = MakePeriod(unrelatedZone, 5_000_000, new() { { "BLAZE_ROD", 500 } }, 20);
        var task = new T4InfernoDemonlordTask();
        var result = await task.Execute(MakeParams(period));
        result.ProfitPerHour.Should().Be(0,
            $"{unrelatedZone} is a Crimson Isle sub-zone unrelated to the Inferno Demonlord boss (fought in the Smoldering Tomb) " +
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
