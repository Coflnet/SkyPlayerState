using System.Collections.Generic;
using System.Text.Json;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

public class GearRateModelsTests
{
    private static RateModelSpec Spec(string json) => JsonSerializer.Deserialize<RateModelSpec>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static Dictionary<string, double> Stats(params (string Name, double Value)[] stats)
    {
        var result = new Dictionary<string, double>();
        foreach (var (name, value) in stats)
            result[name] = value;
        return result;
    }

    private static readonly RevenueShares BulkOnly = new(1, 0, 0, true);
    private static readonly RevenueShares RareOnly = new(0, 1, 0, true);

    private static double Multiplier(IGearRateModel model, RevenueShares shares, GearSetup setup, GearSetup anchor) =>
        GearSetupEstimator.RawMultiplier(shares, model.Factors(setup.Stats, setup), model.Factors(anchor.Stats, anchor));

    [Test]
    public void Fortune_100To200_GivesOneAndAHalf()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "fortune", "fortuneStat": "Farming Fortune" }"""));
        var anchor = new GearSetup { Id = "budget", Stats = Stats(("Farming Fortune", 100)) };
        var endgame = new GearSetup { Id = "endgame", Stats = Stats(("Farming Fortune", 200)) };

        Multiplier(model, BulkOnly, endgame, anchor).Should().BeApproximately(1.5, 1e-12);
    }

    [Test]
    public void Fortune_DivisorIsAParameter()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "fortune", "fortuneStat": "F", "fortuneDivisor": 50 }"""));

        model.Factors(Stats(("F", 100)), new GearSetup()).Bulk.Should().Be(3);
    }

    [Test]
    public void MagicFind_ChangesOnlyTheRareComponent()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "fortune", "fortuneStat": "Farming Fortune", "rareStat": "Magic Find" }"""));
        var anchor = new GearSetup { Stats = Stats(("Farming Fortune", 100), ("Magic Find", 0)) };
        var moreMagicFind = new GearSetup { Stats = Stats(("Farming Fortune", 100), ("Magic Find", 50)) };

        var anchorFactors = model.Factors(anchor.Stats, anchor);
        var factors = model.Factors(moreMagicFind.Stats, moreMagicFind);

        factors.Bulk.Should().Be(anchorFactors.Bulk);
        factors.Throughput.Should().Be(anchorFactors.Throughput);
        factors.Rare.Should().BeApproximately(1.5, 1e-12);
        Multiplier(model, BulkOnly, moreMagicFind, anchor).Should().Be(1);
        Multiplier(model, RareOnly, moreMagicFind, anchor).Should().BeApproximately(1.5, 1e-12);
    }

    // ticks = max(minTicks, round(30 * hardness / speed)), round half away from zero
    [TestCase(500, 500, 4, 30)]
    [TestCase(500, 1000, 4, 15)]
    [TestCase(500, 3000, 4, 5)]
    [TestCase(500, 4000, 4, 4)]   // 3.75 rounds to 4
    [TestCase(500, 10000, 4, 4)]  // 1.5 is below the floor
    [TestCase(100, 600, 4, 5)]
    [TestCase(100, 700, 4, 4)]    // 4.29
    [TestCase(5, 60, 1, 3)]       // 2.5 rounds up, not to even
    [TestCase(5, 100, 1, 2)]      // 1.5 rounds up
    [TestCase(500, 0, 4, 1073741823)]
    public void Mining_TickTable(double hardness, double speed, double minTicks, int expectedTicks)
    {
        var model = new MiningRateModel(Spec($$"""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": {{hardness}}, "minTicks": {{minTicks}} }"""));

        model.TicksPerBlock(speed).Should().Be(expectedTicks);
    }

    [Test]
    public void Mining_BlocksPerHourIncludesOverheadTicks()
    {
        var plain = new MiningRateModel(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 500 }"""));
        var withOverhead = new MiningRateModel(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 500, "overheadTicks": 1 }"""));

        plain.BlocksPerHour(1000).Should().Be(72000.0 / 15);
        withOverhead.BlocksPerHour(1000).Should().Be(72000.0 / 16);
    }

    [Test]
    public void Mining_InstamineBreaksInOneTickAboveTheThreshold()
    {
        var model = new MiningRateModel(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 50, "instamineFactor": 60 }"""));
        var noInstamine = new MiningRateModel(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 50 }"""));

        model.TicksPerBlock(3000).Should().Be(4, "speed must exceed 60 x strength, not equal it");
        model.TicksPerBlock(3001).Should().Be(1);
        noInstamine.TicksPerBlock(100000).Should().Be(4, "without instamineFactor the minTicks floor holds");
    }

    [Test]
    public void Mining_SumsEveryFortuneStatAndAppliesSpread()
    {
        var model = GearRateModels.Create(Spec("""
            { "kind": "mining", "speedStat": "S", "fortuneStats": ["Mining Fortune", "Gemstone Fortune"], "blockStrength": 500,
              "spreadStat": "Spread", "rareStat": "MF" }
            """));

        var factors = model.Factors(Stats(("S", 1000), ("Mining Fortune", 100), ("Gemstone Fortune", 50), ("Spread", 50), ("MF", 20)), new GearSetup());

        var bph = 72000.0 / 15;
        factors.Throughput.Should().Be(bph);
        factors.Bulk.Should().BeApproximately(bph * 2.5 * 1.5, 1e-9);
        factors.Rare.Should().BeApproximately(bph * 1.2, 1e-9);
    }

    [Test]
    public void Mining_PristineAddsGemValuePerStatPoint()
    {
        // gem value factor = 1 + pristineFactor * Pristine, Pristine in stat points
        var model = GearRateModels.Create(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 500, "pristineStat": "P" }"""));
        var none = new GearSetup { Stats = Stats(("S", 1000), ("F", 0), ("P", 0)) };
        var anchor = new GearSetup { Stats = Stats(("S", 1000), ("F", 0), ("P", 2)) };
        var more = new GearSetup { Stats = Stats(("S", 1000), ("F", 0), ("P", 4)) };

        var bph = 72000.0 / 15;
        model.Factors(none.Stats, none).Bulk.Should().BeApproximately(bph, 1e-9, "no Pristine leaves the plain gem value");
        model.Factors(anchor.Stats, anchor).Bulk.Should().BeApproximately(bph * (1 + 0.79 * 2), 1e-9);
        Multiplier(model, BulkOnly, more, anchor).Should().BeApproximately((1 + 0.79 * 4) / (1 + 0.79 * 2), 1e-12);
    }

    [Test]
    public void Mining_TickFloorKeepsBlocksPerHourFinite()
    {
        // minTicks 0 is rejected by validation; the model still never divides by zero
        var model = new MiningRateModel(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 1, "minTicks": 0 }"""));

        double.IsFinite(model.BlocksPerHour(1_000_000)).Should().BeTrue();
        model.BlocksPerHour(1_000_000).Should().Be(72000);
    }

    [Test]
    public void Mining_RareChanceStatScalesOnlyTheRareComponent()
    {
        // powder mining: chest loot scales with blocks x chest chance, Hard Stone with blocks x spread
        var model = GearRateModels.Create(Spec("""
            { "kind": "mining", "speedStat": "S", "fortuneStats": [], "blockStrength": 500, "spreadStat": "Spread",
              "rareChanceStat": "Chest Chance" }
            """));
        var anchor = new GearSetup { Stats = Stats(("S", 1000), ("Spread", 100), ("Chest Chance", 1)) };
        var moreChests = new GearSetup { Stats = Stats(("S", 1000), ("Spread", 100), ("Chest Chance", 1.5)) };

        var bph = 72000.0 / 15;
        var factors = model.Factors(moreChests.Stats, moreChests);
        factors.Bulk.Should().BeApproximately(bph * 2, 1e-9, "no fortune applies, spread doubles the blocks");
        factors.Rare.Should().BeApproximately(bph * 0.015, 1e-9);
        Multiplier(model, BulkOnly, moreChests, anchor).Should().Be(1);
        Multiplier(model, RareOnly, moreChests, anchor).Should().BeApproximately(1.5, 1e-12);
    }

    [Test]
    public void Mining_ChanceStatScalesPowderChests()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "mining", "speedStat": "S", "fortuneStat": "F", "blockStrength": 500, "chanceStat": "Chest Chance" }"""));
        var anchor = new GearSetup { Stats = Stats(("S", 1000), ("F", 0), ("Chest Chance", 10)) };
        var better = new GearSetup { Stats = Stats(("S", 1000), ("F", 0), ("Chest Chance", 15)) };

        Multiplier(model, BulkOnly, better, anchor).Should().BeApproximately(1.5, 1e-12);
    }

    [Test]
    public void Fishing_FasterRodsShortenOnlyTheWait()
    {
        var model = GearRateModels.Create(Spec("""
            { "kind": "fishing", "fishingSpeedStat": "FS", "seaCreatureChanceStat": "SCC", "baseWaitSeconds": 20, "reelSeconds": 2, "rareStat": "MF" }
            """));

        var slow = model.Factors(Stats(("FS", 0), ("SCC", 20), ("MF", 0)), new GearSetup());
        var fast = model.Factors(Stats(("FS", 100), ("SCC", 20), ("MF", 50)), new GearSetup());

        slow.Throughput.Should().BeApproximately(3600 / 22.0, 1e-9);
        fast.Throughput.Should().BeApproximately(3600 / 12.0, 1e-9);
        fast.Bulk.Should().BeApproximately(3600 / 12.0 * 0.2, 1e-9, "bulk follows sea creatures by default");
        fast.Rare.Should().BeApproximately(3600 / 12.0 * 0.2 * 1.5, 1e-9);
    }

    [Test]
    public void Fishing_CapsSpeedAndSeaCreatureChance()
    {
        var model = GearRateModels.Create(Spec("""
            { "kind": "fishing", "fishingSpeedStat": "FS", "seaCreatureChanceStat": "SCC", "baseWaitSeconds": 20, "fishingSpeedCap": 100 }
            """));

        var capped = model.Factors(Stats(("FS", 400), ("SCC", 150)), new GearSetup());

        capped.Throughput.Should().BeApproximately(3600 / 10.0, 1e-9, "fishing speed counts only up to the cap");
        capped.Bulk.Should().BeApproximately(capped.Throughput, 1e-9, "sea creature chance is capped at 100% by default");
    }

    [Test]
    public void Fishing_KillTimeAndCatchBasedBulk()
    {
        var model = GearRateModels.Create(Spec("""
            { "kind": "fishing", "fishingSpeedStat": "FS", "seaCreatureChanceStat": "SCC", "baseWaitSeconds": 10, "killSecondsPerCreature": 10, "bulkFrom": "catches" }
            """));

        var factors = model.Factors(Stats(("FS", 0), ("SCC", 50)), new GearSetup());

        // 10s wait + 0.5 * 10s killing per catch
        factors.Throughput.Should().BeApproximately(3600 / 15.0, 1e-9);
        factors.Bulk.Should().Be(factors.Throughput);
    }

    [Test]
    public void RunTime_120To90WithThirtySecondsOverhead_GivesOnePointTwoFive()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "runTime", "overheadSeconds": 30 }"""));
        var anchor = new GearSetup { RunSeconds = 120 };
        var faster = new GearSetup { RunSeconds = 90 };

        Multiplier(model, BulkOnly, faster, anchor).Should().BeApproximately(1.25, 1e-12);
    }

    [Test]
    public void RunTime_MagicFindOnlyWhenMfAffected()
    {
        var affected = GearRateModels.Create(Spec("""{ "kind": "runTime", "mfAffected": true, "rareStat": "MF" }"""));
        var unaffected = GearRateModels.Create(Spec("""{ "kind": "runTime", "rareStat": "MF" }"""));
        var setup = new GearSetup { RunSeconds = 360, Stats = Stats(("MF", 100)) };

        affected.Factors(setup.Stats, setup).Rare.Should().Be(20);
        unaffected.Factors(setup.Stats, setup).Rare.Should().Be(10);
    }

    [Test]
    public void TierUnlock_IsNeutral()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "tierUnlock" }"""));

        model.Factors(Stats(), new GearSetup { RunSeconds = 360 }).Should().Be(RateFactors.Neutral);
    }

    [Test]
    public void Fixed_UsesTheSetupsMultiplier()
    {
        var model = GearRateModels.Create(Spec("""{ "kind": "fixed", "multipliers": { "budget": 1, "endgame": 1.8 } }"""));

        model.Factors(Stats(), new GearSetup { Id = "endgame" }).Should().Be(new RateFactors(1.8, 1.8, 1.8));
    }

    [Test]
    public void Kind_IsMatchedCaseInsensitively()
    {
        GearRateModels.Create(Spec("""{ "kind": "RUNTIME" }""")).Kind.Should().Be(GearModelKinds.RunTime);
    }
}
