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

    // ── Zone-to-island matching (SkyblockZones) ──
    // The scoreboard reports the sub-zone ("Jungle", "Goblin Holdout", "Dwarven Village", ...),
    // never the island name, for most islands - these pin the classifier to matching a task's
    // island-level Locations (e.g. ["Crystal Hollows"]) against that sub-zone.

    [Test]
    public void JungleZone_WithSludgeJuice_DetectsSludgeMining()
    {
        var items = new Dictionary<string, int> { { "SLUDGE_JUICE", 20 } };
        var result = Classifier.Classify("Jungle", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Sludge Mining",
            "Sludge Juice is mined in the Jungle sub-zone of the Crystal Hollows, not the Dwarven Mines");
    }

    [Test]
    public void GoblinHoldoutZone_MatchesTaskWithCrystalHollowsIslandLocation()
    {
        // ScathaMiningTask declares Locations = ["Crystal Hollows"] only (no sub-zones); the
        // classifier must still attribute a Goblin Holdout (a Crystal Hollows sub-zone) window
        // to it via the zone-to-island map instead of requiring an exact string match.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var result = Classifier.Classify("Goblin Holdout", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Scatha Mining");
    }

    [Test]
    public void DwarvenVillageZone_MatchesDwarvenMinesIslandLocation()
    {
        var items = new Dictionary<string, int> { { "COAL", 50 } };
        var result = Classifier.Classify("Dwarven Village", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Coal Mining");
    }

    // ── currentIsland (tab list) fallback: only for zones the static map can't resolve, and only
    // when fresh - received while the player was actually in the current zone: at/after
    // CurrentLocationSince (zone start) and confirmed by a scoreboard update at/after it, i.e.
    // at/before CurrentLocationSeenAt (see TaskClassifier.Classify). The tab list is sent far less
    // often than the scoreboard, so CurrentIsland can be stale (production logs, player Ekwav: zone
    // "Your Island" seen 9x while the last tab Area was still "Torrhus Canyon"/Galatea from before
    // the player left for their private island).

    [Test]
    public void StaleCurrentIsland_AmbiguousZone_DoesNotMatch()
    {
        // "Your Island" is deliberately ambiguous/unmapped (SkyblockZones.AmbiguousZones), so the
        // classifier would otherwise fall back to currentIsland. No MethodTask lists the literal
        // island name "Galatea" (all its mob tasks key off specific sub-zones instead), so
        // ScathaMiningTask (Locations = ["Crystal Hollows"]) + a stale "Crystal Hollows" tab
        // reading is the equivalent real fixture for the exact same bug.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 10, 0);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(10);
        var staleCurrentIslandAt = currentLocationSince.AddMinutes(-5); // tab reading predates the move to "Your Island"

        var result = Classifier.Classify("Your Island", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: staleCurrentIslandAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().BeNull("a stale tab-reported island must not be used to match an ambiguous zone");
    }

    [Test]
    public void TabForNewZoneArrivingAfterLastConfirmationOfOldZone_DoesNotMatchOldFragment()
    {
        // The tab can arrive for the NEW island before the scoreboard even shows the new zone - a
        // fragment still being flushed for the OLD (ambiguous) zone must not pick up that new tab
        // reading just because it is "fresh" by naive at/after-start standards: it must also have
        // been confirmed by a scoreboard update of the OLD zone at/after it arrived.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 10, 0);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(5); // last scoreboard confirmation of the OLD zone
        var tabForNewIslandAt = currentLocationSeenAt.AddMinutes(1);    // tab arrived AFTER that last confirmation

        var result = Classifier.Classify("Your Island", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: tabForNewIslandAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().BeNull("a tab reading that arrived after the old zone's last confirmation belongs to the new zone, not this fragment");
    }

    [Test]
    public void FreshCurrentIsland_AmbiguousZone_StillMatches()
    {
        // Same fixture as above, but the tab reading arrived while the player was confirmed to be
        // in the current zone (at/after it started, at/before it was last confirmed) - the fallback
        // must still work in this (the normal, intended) case.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 10, 0);
        var freshCurrentIslandAt = currentLocationSince.AddMinutes(1);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(10);

        var result = Classifier.Classify("Your Island", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: freshCurrentIslandAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Scatha Mining");
    }

    [Test]
    public void CurrentIsland_NeverOverridesAZoneTheMapAlreadyResolves()
    {
        // "Village" resolves unambiguously to "Hub" via the static map (not ambiguous/unmapped),
        // so a currentIsland fallback - fresh or not - must never be consulted for it, even though
        // it doesn't match any Hub-only task's Locations here.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 10, 0);
        var freshCurrentIslandAt = currentLocationSince.AddMinutes(1);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(10);

        var result = Classifier.Classify("Village", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: freshCurrentIslandAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().BeNull("Village already resolves to Hub via the static map, so Crystal Hollows tasks must not match it");
    }

    // ── Same fixtures phrased against "Dragon's Lair" (Crystal Hollows AND Galatea - the exact
    // ambiguous zone named in the finding this pins down), covering all three CollectionListener
    // scenarios that feed CurrentLocationSince/CurrentLocationSeenAt into Classify. ──

    [Test]
    public void DragonsLair_TabReceivedMidStay_ThenFlushWithoutNewTab_StillResolvedViaIsland()
    {
        // (a) the tab island was read once, mid-stay in the ambiguous zone; a later 5-minute
        // same-zone flush (CollectionListener.StoreLocationProfit) bumps CurrentLocationSeenAt to
        // the flush time WITHOUT a new tab arriving - the earlier tab reading must still apply
        // since it falls within [CurrentLocationSince, CurrentLocationSeenAt].
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 0, 0);
        var tabReceivedMidStayAt = currentLocationSince.AddMinutes(2);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(5); // the 5-min flush, no new tab since

        var result = Classifier.Classify("Dragon's Lair", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: tabReceivedMidStayAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Scatha Mining");
    }

    [Test]
    public void DragonsLair_TabForNewIslandArrivesAfterOldZonesLastScoreboard_DoesNotApplyToOldFragment()
    {
        // (b) the tab for a NEW island can arrive before the scoreboard even shows the new zone -
        // if that tab reading is AFTER the old (ambiguous) zone's last scoreboard confirmation, the
        // old zone's fragment must not be classified using it.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 0, 0);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(5); // last scoreboard update of the OLD zone
        var tabForNewIslandAt = currentLocationSeenAt.AddMinutes(1);

        var result = Classifier.Classify("Dragon's Lair", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: tabForNewIslandAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().BeNull(
            "a tab reading that arrived after this zone's last scoreboard confirmation belongs to the next zone, not this fragment");
    }

    [Test]
    public void DragonsLair_TabFromBeforeEnteringZone_IsIgnored()
    {
        // (c) a tab reading from before the player even arrived at the current zone must not apply.
        var items = new Dictionary<string, int> { { "PET_SCATHA", 2 } };
        var currentLocationSince = new DateTime(2025, 7, 24, 12, 0, 0);
        var currentLocationSeenAt = currentLocationSince.AddMinutes(5);
        var tabFromBeforeArrivalAt = currentLocationSince.AddMinutes(-1);

        var result = Classifier.Classify("Dragon's Lair", items, 10,
            currentIsland: "Crystal Hollows", currentIslandAt: tabFromBeforeArrivalAt,
            currentLocationSince: currentLocationSince, currentLocationSeenAt: currentLocationSeenAt);

        result.Should().BeNull("a tab reading from before the player entered this zone must not classify it");
    }

    // ── Derived tasks (shared detection, no classification tie) ──
    // Regression for: "Sludge Mining" and "Sludge Mining (Gem Mixture)" both matched on
    // SLUDGE_JUICE in the Jungle; the alphabetical tie-break meant "Sludge Mining" always won and
    // the Gem Mixture variant never got its own doers/community data. DerivedFrom takes it out of
    // the tie entirely instead of hoping it wins.

    [Test]
    public void SludgeMiningGemMixture_NeverCompetesForClassification()
    {
        // Even with BOTH tasks' detection items present, the derived task must never be returned -
        // only its primary "Sludge Mining" is a valid classification target.
        var items = new Dictionary<string, int> { { "SLUDGE_JUICE", 40 }, { "GEMSTONE_MIXTURE", 5 } };
        var result = Classifier.Classify("Jungle", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Sludge Mining");
        result.TaskName.Should().NotBe("Sludge Mining (Gem Mixture)");
    }

    [Test]
    public void GetDerivedTaskNames_ReturnsGemMixtureVariant_ForSludgeMining()
    {
        var derived = Classifier.GetDerivedTaskNames("Sludge Mining");
        derived.Should().Contain("Sludge Mining (Gem Mixture)",
            "players mining Sludge Juice should count as doing the Gem Mixture variant too");
    }

    [Test]
    public void GetDerivedTaskNames_EmptyForTaskWithNoDerivedVariants()
    {
        Classifier.GetDerivedTaskNames("Bayou Fishing").Should().BeEmpty();
        Classifier.GetDerivedTaskNames(null).Should().BeEmpty();
        Classifier.GetDerivedTaskNames("Not A Real Task").Should().BeEmpty();
    }
}
