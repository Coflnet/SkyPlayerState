using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Empty Chum Buckets: filling Empty Chum Buckets with 10 Chum into Full Chum Buckets. Periods from production (2026-11-26..28),
/// Bazaar sell prices of 2026-11-28.
/// </summary>
public class EmptyChumBucketsTaskTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    private static readonly Dictionary<string, double> Prices = new()
    {
        { "FULL_CHUM_BUCKET", 18_004 }, { "EMPTY_CHUM_BUCKET", 10_000 }, { "CHUM", 0.2 }, { "GLOWING_MUSHROOM", 1_220 },
        { "RED_MUSHROOM", 5 }, { "BUILDER_BROWN_MUSHROOM", 5 }, { "BLAZE_ROD", 6 }, { "MAGMA_CORE", 16_500 }
    };

    private static MethodTask Task => (MethodTask)Registry.GetByName("Empty Chum Buckets");

    private static string Classify(string zone, Dictionary<string, int> items)
        => Classifier.Classify(zone, items, 5, prices: Prices)?.TaskName;

    /// <summary>
    /// The production path: a session classifies only the gains of its items (costs ride along for the folder), and the
    /// flushed period's DetectedTask is what gets folded.
    /// </summary>
    internal static string ClassifyAsSession(TaskClassifier classifier, string zone, Dictionary<string, int> items,
        Dictionary<string, double> prices)
    {
        var service = new TaskSessionService(classifier);
        var info = new ExtractedInfo();
        var start = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var fragment = new Period
        {
            PlayerUuid = "00000000000000000000000000000001", Location = zone, Server = "test",
            StartTime = start, EndTime = start.AddMinutes(5), ItemsCollected = new(items)
        };
        service.Accumulate(info, fragment.PlayerUuid, fragment, null, prices, fragment.EndTime);
        var idle = new Period { PlayerUuid = fragment.PlayerUuid, Location = zone, Server = "test", StartTime = fragment.EndTime, EndTime = start.AddMinutes(11), ItemsCollected = new() };
        var flushed = service.Accumulate(info, fragment.PlayerUuid, idle, null, prices, idle.EndTime);
        flushed.Should().NotBeNull();
        return flushed.DetectedTask;
    }

    private static string Session(string zone, Dictionary<string, int> items) => ClassifyAsSession(Classifier, zone, items, Prices);

    /// <summary>Second review: sessions see no consumed Chum, so a consumed-Chum rule made every craft next to a catch foreign.</summary>
    [Test]
    public void CraftSession_NextToFishingOrMinionItems_IsEmptyChumBuckets()
    {
        Session("Backwater Bayou", new() { { "RAW_FISH", 60 }, { "CHUM", -130 }, { "EMPTY_CHUM_BUCKET", -13 }, { "FULL_CHUM_BUCKET", 13 } })
            .Should().Be("Empty Chum Buckets");
        Session("Your Island", new() { { "INK_SACK", 2 }, { "FULL_CHUM_BUCKET", 13 } }).Should().Be("Empty Chum Buckets");
        Session("Hub", new() { { "CLAY_BALL", 3 }, { "FULL_CHUM_BUCKET", 13 } }).Should().Be("Empty Chum Buckets");
    }

    [Test]
    public void PlacedBucketSession_StaysWithTheFishing()
    {
        Session("Backwater Bayou", new() { { "RAW_FISH", 60 }, { "WATER_LILY", 10 }, { "FULL_CHUM_BUCKET", 1 } }).Should().Be("Bayou Fishing");
        Session("Hub", new() { { "RAW_FISH", 100 }, { "ENCHANTED_RAW_FISH", 5 }, { "PRISMARINE_SHARD", 10 }, { "FULL_CHUM_BUCKET", 1 } })
            .Should().Be("Water Fishing");
        Session("Backwater Bayou", new() { { "RAW_FISH", 14 }, { "WATER_LILY", 109 }, { "CLAY_BALL", 2 }, { "EMPTY_CHUM_BUCKET", -1 }, { "FULL_CHUM_BUCKET", 2 } })
            .Should().Be("Bayou Fishing");
    }

    [Test]
    public void PersonalView_AgreesWithTheSession()
    {
        Period Make(Dictionary<string, int> items) => new()
        {
            Location = "Backwater Bayou", ItemsCollected = items, PlayerUuid = "test",
            StartTime = new DateTime(2026, 11, 28, 12, 0, 0), EndTime = new DateTime(2026, 11, 28, 12, 5, 0)
        };
        var matched = Task.FindMatchingPeriodsForAggregation(new TaskParams
        {
            TestTime = new DateTime(2026, 11, 28, 12, 10, 0),
            LocationProfit = new()
            {
                { "Backwater Bayou", [Make(CraftPeriod()), Make(new() { { "RAW_FISH", 60 }, { "WATER_LILY", 10 }, { "FULL_CHUM_BUCKET", 1 } })] }
            }
        });
        matched.Should().ContainSingle().Which.ItemsCollected.Should().ContainKey("CHUM");
    }

    private static Dictionary<string, int> CraftPeriod() =>
        new() { { "CHUM", -130 }, { "EMPTY_CHUM_BUCKET", -13 }, { "FULL_CHUM_BUCKET", 13 } };

    [Test]
    [TestCase("Backwater Bayou")]
    [TestCase("Forest")]
    [TestCase("Your Island")]
    public void FillingBuckets_IsEmptyChumBuckets_WhereverItIsCrafted(string zone)
        => Classify(zone, CraftPeriod()).Should().Be("Empty Chum Buckets");

    [Test]
    public void FillingBucketsAtMobyWhileFarmingMushrooms_IsEmptyChumBuckets()
        => Classify("Glowing Mushroom Cave", new()
        {
            { "RED_MUSHROOM", -24 }, { "BUILDER_BROWN_MUSHROOM", -56 }, { "GLOWING_MUSHROOM", 24 }, { "CHUM", -60 }, { "FULL_CHUM_BUCKET", 12 }
        }).Should().Be("Empty Chum Buckets");

    /// <summary>Review probes: a bucket placed while fishing and picked up full outvalued the fish.</summary>
    [Test]
    public void PlacedBucketPickedUpWhileFishing_StaysWithTheFishing()
    {
        Classify("Backwater Bayou", new() { { "RAW_FISH", 60 }, { "WATER_LILY", 10 }, { "FULL_CHUM_BUCKET", 1 } }).Should().Be("Bayou Fishing");
        Classify("Hub", new() { { "RAW_FISH", 100 }, { "ENCHANTED_RAW_FISH", 5 }, { "PRISMARINE_SHARD", 10 }, { "FULL_CHUM_BUCKET", 1 } })
            .Should().Be("Water Fishing");
        // production: placing the bucket books the Empty one as spent, picking it up the Full one, a few Chum went elsewhere
        Classify("Backwater Bayou", new() { { "RAW_FISH", 14 }, { "WATER_LILY", 109 }, { "CLAY_BALL", 2 }, { "EMPTY_CHUM_BUCKET", -1 }, { "FULL_CHUM_BUCKET", 2 } })
            .Should().Be("Bayou Fishing");
        Classify("Backwater Bayou", new() { { "RAW_FISH", 26 }, { "WATER_LILY", 13 }, { "CHUM", -3 }, { "FULL_CHUM_BUCKET", 1 } })
            .Should().Be("Bayou Fishing");
    }

    [Test]
    public void CraftingNextToFishingCatches_IsEmptyChumBuckets()
        => Classify("Backwater Bayou", new() { { "RAW_FISH", 60 }, { "CHUM", -130 }, { "EMPTY_CHUM_BUCKET", -13 }, { "FULL_CHUM_BUCKET", 13 } })
            .Should().Be("Empty Chum Buckets");

    [Test]
    public void StaleLocationDump_StaysUnknownLocation()
        => Classify("Unknown", new() { { "FULL_CHUM_BUCKET", 2 }, { "GLOWING_MUSHROOM", 40 }, { "RED_MUSHROOM", 300 } })
            .Should().Be("Unknown Location");

    [Test]
    public void BucketBoughtFromMobyWithoutFilling_IsNotTheMethod()
        => Classify("Glowing Mushroom Cave", new()
        {
            { "RED_MUSHROOM", 24 }, { "BUILDER_BROWN_MUSHROOM", 8 }, { "GLOWING_MUSHROOM", 21 }, { "EMPTY_CHUM_BUCKET", 1 }
        }).Should().NotBe("Empty Chum Buckets");

    [Test]
    public void LavaFishingChum_IsNotTheMethod()
        => Classify("Magma Fields", new() { { "BLAZE_ROD", 400 }, { "CHUM", 23 }, { "MAGMA_CORE", 1 } }).Should().NotBe("Empty Chum Buckets");

    [Test]
    public void ConsumedBucketsAndChum_AreKeptAsTheCraftCost()
        => TaskPeriodFolder.FilterOwnedItems(CraftPeriod(), Task).Should()
            .Contain("EMPTY_CHUM_BUCKET", -13).And.Contain("CHUM", -130).And.Contain("FULL_CHUM_BUCKET", 13);

    [Test]
    public void FormulaRate_IsTheFillMarginTimesTheBuckets()
    {
        // 40 x (18,004 - 10,000 - 10 x 0.2) = 320,080/h
        TaskEstimator.FormulaRate(Task, Prices).Should().BeApproximately(320_080, 1);
    }

    [Test]
    public void FormulaRate_IsALoss_WhenFillingDoesNotPay()
    {
        var prices = new Dictionary<string, double>(Prices) { ["EMPTY_CHUM_BUCKET"] = 20_000 };
        TaskEstimator.FormulaRate(Task, prices).Should().BeNegative();
    }
}
