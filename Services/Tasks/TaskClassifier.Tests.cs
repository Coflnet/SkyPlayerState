using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskClassifierTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    // ── Parity with MethodTask.FindMatchingPeriods ──

    /// <summary>
    /// Whatever the classifier attributes a window to must be a task whose
    /// FindMatchingPeriods matches the same window, for every registered task's
    /// own fingerprint. This pins the classifier to the existing detection rules.
    /// </summary>
    [Test]
    public void ClassificationAgreesWithFindMatchingPeriods_ForEveryTaskFingerprint()
    {
        foreach (var task in Registry.MethodTasks)
        {
            var sig = task.GetDetectionSignature();
            if (sig.Locations.Count == 0)
                continue; // tasks without location can't build an unambiguous fixture
            var items = new Dictionary<string, int>();
            foreach (var tag in sig.DetectionItems.Take(2))
                items[tag] = 10;
            if (sig.RequireShardItems && !items.Keys.Any(k => k.StartsWith("SHARD_")))
                items["SHARD_TESTFIXTURE"] = 10;
            if (items.Count == 0)
                items["SOME_GENERIC_ITEM"] = 10; // location-only task, needs >=5 items
            var location = sig.Locations.First();

            var classification = Classifier.Classify(location, items, 10);

            classification.Should().NotBeNull($"the fingerprint of {sig.MethodName} should classify to something");
            // the attributed task must consider this window one of its own periods
            var attributed = Registry.MethodTasks.First(t => t.GetDetectionSignature().MethodName == classification.TaskName);
            var period = new Period
            {
                Location = location,
                ItemsCollected = items,
                StartTime = new DateTime(2025, 7, 24, 12, 0, 0),
                EndTime = new DateTime(2025, 7, 24, 12, 10, 0),
                PlayerUuid = "test"
            };
            var matched = attributed.FindMatchingPeriodsForAggregation(new TaskParams
            {
                TestTime = period.EndTime,
                LocationProfit = new() { { location, [period] } }
            });
            matched.Should().NotBeEmpty(
                $"{classification.TaskName} was attributed a window at {location} its own detection rules do not match");
        }
    }

    // ── Tie breaking ──

    [Test]
    public void HotspotBeatsPlainFishing_ItemEvidenceOverLocationOnly()
    {
        var items = new Dictionary<string, int> { { "HOTSPOT_CATCH", 8 }, { "RAW_FISH", 100 } };
        var result = Classifier.Classify("Backwater Bayou", items, 15);
        result.Should().NotBeNull();
        result.TaskName.Should().Be("Bayou Hotspot Fishing");
        result.ItemMatched.Should().BeTrue();
    }

    [Test]
    public void ShardCatch_GoesToHuntingVariant()
    {
        var items = new Dictionary<string, int> { { "SHARD_STRIDER_SURFER", 12 }, { "RAW_FISH", 50 } };
        var result = Classifier.Classify("Backwater Bayou", items, 15);
        result.Should().NotBeNull();
        // ExcludeShardItems disqualifies the regular variants, the hunting variant requires shards
        result.TaskName.Should().Be("Bayou Fishing (Hunting)");
    }

    [Test]
    public void NoShard_GoesToRegularVariant()
    {
        var items = new Dictionary<string, int> { { "RAW_FISH", 80 } };
        var result = Classifier.Classify("Backwater Bayou", items, 15);
        result.Should().NotBeNull();
        result.TaskName.Should().Be("Bayou Fishing");
    }

    [Test]
    public void MostValuableMatchedItems_WinAmongItemMatches()
    {
        // both magma core and flaming worm tasks can match in Magma Fields
        var items = new Dictionary<string, int> { { "MAGMA_CORE", 10 }, { "WORM_MEMBRANE", 10 } };
        var prices = new Dictionary<string, double> { { "MAGMA_CORE", 100_000 }, { "WORM_MEMBRANE", 1_000 } };
        var result = Classifier.Classify("Crystal Hollows", items, 15, prices: prices);
        result.Should().NotBeNull();
        result.TaskName.Should().Be("Magma Core Fishing");

        prices = new Dictionary<string, double> { { "MAGMA_CORE", 1_000 }, { "WORM_MEMBRANE", 100_000 } };
        result = Classifier.Classify("Crystal Hollows", items, 15, prices: prices);
        result.TaskName.Should().Be("Flaming Worm Fishing");
    }

    [Test]
    public void ClaimedTask_WinsAnyTieItMatches()
    {
        var items = new Dictionary<string, int> { { "HOTSPOT_CATCH", 8 }, { "RAW_FISH", 100 } };
        var result = Classifier.Classify("Backwater Bayou", items, 15, claimedTask: "Bayou Fishing");
        result.Should().NotBeNull();
        result.TaskName.Should().Be("Bayou Fishing", "the player explicitly claimed the plain variant");
    }

    [Test]
    public void ClaimedTaskThatDoesNotMatch_IsIgnored()
    {
        var items = new Dictionary<string, int> { { "RAW_FISH", 80 } };
        var result = Classifier.Classify("Backwater Bayou", items, 15, claimedTask: "Thyst Mining");
        result.Should().NotBeNull();
        result.TaskName.Should().Be("Bayou Fishing", "a claim only biases tasks whose rules actually match");
    }

    // ── Minimum signal ──

    [Test]
    public void TooShortWindow_ReturnsNull()
    {
        var items = new Dictionary<string, int> { { "RAW_FISH", 80 } };
        Classifier.Classify("Backwater Bayou", items, 2.5).Should().BeNull();
    }

    [Test]
    public void LocationOnlyMatchWithFewItems_ReturnsNull()
    {
        var items = new Dictionary<string, int> { { "RAW_FISH", 3 } };
        Classifier.Classify("Backwater Bayou", items, 15).Should().BeNull();
    }

    [Test]
    public void DetectionItemHitWithFewItems_StillClassifies()
    {
        var items = new Dictionary<string, int> { { "HOTSPOT_CATCH", 2 } };
        var result = Classifier.Classify("Backwater Bayou", items, 15);
        result.Should().NotBeNull("a detection item hit is strong evidence even at low counts");
        result.TaskName.Should().Be("Bayou Hotspot Fishing");
    }

    [Test]
    public void NoItems_ReturnsNull()
    {
        Classifier.Classify("Backwater Bayou", new Dictionary<string, int>(), 15).Should().BeNull();
        Classifier.Classify("Backwater Bayou", null, 15).Should().BeNull();
    }

    [Test]
    public void UnknownLocationWithoutItemEvidence_ReturnsNull()
    {
        var items = new Dictionary<string, int> { { "COBBLESTONE", 100 } };
        Classifier.Classify("Private Island", items, 15).Should().BeNull();
    }

    // ── Registry integrity for classification keys ──

    [Test]
    public void MethodNamesAreUniqueAcrossRegistry()
    {
        var duplicates = Registry.MethodTasks
            .Select(t => t.GetDetectionSignature().MethodName)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        duplicates.Should().BeEmpty("MethodName is the classification and aggregation key");
    }

    /// <summary>
    /// A real Lotus Atoll lily-pad session (Ekwav's reference sample) classifies to the
    /// new task rather than a region-only Galatea tracker.
    /// </summary>
    [Test]
    public void LotusAtoll_SampleClassifiesToLotusAtoll()
    {
        var items = new Dictionary<string, int> { { "LOTUS", 57 }, { "WATER_LILY", 64 }, { "SHARD_LOTUS_FISH", 22 } };
        var classification = Classifier.Classify("Lotus Atoll", items, 5);
        classification.Should().NotBeNull();
        classification!.TaskName.Should().Be("Lotus Atoll");
    }
}
