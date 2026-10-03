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

        // "Your Island" now always falls back to the hidden "Private Island Activity" catch-all
        // (HiddenTasks.cs, section D) when nothing item-matches - if the stale tab reading had
        // wrongly been used, PET_SCATHA would have item-matched Scatha Mining instead.
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Private Island Activity",
            "a stale tab-reported island must not be used to match an ambiguous zone");
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

        // See StaleCurrentIsland_AmbiguousZone_DoesNotMatch - "Your Island" now falls back to the
        // hidden "Private Island Activity" catch-all instead of null.
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Private Island Activity",
            "a tab reading that arrived after the old zone's last confirmation belongs to the new zone, not this fragment");
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

        // "Village" is now a literal zone the hidden "Hub Trading" catch-all matches directly
        // (HiddenTasks.cs, section D) - what this regression actually pins is that the fresh
        // currentIsland fallback is never consulted for it, so PET_SCATHA never item-matches
        // Scatha Mining (which would only happen if Village had wrongly resolved to Crystal Hollows).
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Hub Trading",
            "Village already resolves to Hub via the static map, so Crystal Hollows tasks must not match it");
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

    // ── Floor/tier specific dungeon and Kuudra locations (2026-09 "80% of coin value went
    // unclassified" investigation): a bare island-level Location used to make every floor/tier task
    // match every floor/tier's periods, with the alphabetical tie-break always picking M4/whichever
    // sorts first - see DungeonTasks.cs/KuudraTasks.cs. ──

    [Test]
    public void CatacombsM7Zone_WithGenericItems_ClassifiesToM7Only()
    {
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 10 } };
        var result = Classifier.Classify("The Catacombs (M7)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("M7");
    }

    [Test]
    public void CatacombsM5Zone_WithGenericItems_ClassifiesToM5Only()
    {
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 10 } };
        var result = Classifier.Classify("The Catacombs (M5)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("M5");
    }

    [Test]
    public void KuudraT4Zone_WithGenericItems_ClassifiesToKuudraT4Only()
    {
        var items = new Dictionary<string, int> { { "ATTRIBUTE_SHARD", 5 } };
        var result = Classifier.Classify("Kuudra's Hollow (T4)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Kuudra T4");
    }

    [Test]
    public void CatacombsFloor7NormalMode_ClassifiesToF7()
    {
        // Normal-mode Floor 7 now has its own task (F1Task..F7Task, DungeonTasks.cs) distinct from
        // Master Mode M7/M7 Kismet - previously this zone had no matching task at all.
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 10 } };
        var result = Classifier.Classify("The Catacombs (F7)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("F7");
    }

    [Test]
    public void CatacombsF6Zone_WithRewardChestDrops_ClassifiesToF6()
    {
        // F6 is location-only (no DetectionItems), so it needs the classifier's generic >=5 items
        // signal, same as any other location-only task.
        var items = new Dictionary<string, int> { { "ENCHANTED_BOOK", 3 }, { "RECOMBOBULATOR_3000", 2 } };
        var result = Classifier.Classify("The Catacombs (F6)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("F6");
    }

    [Test]
    public void DungeonHubClaimResolvedToM3_ClassifiesToM3()
    {
        // Simulates CollectionListener.StoreLocationProfit already having resolved a "Dungeon Hub"
        // reward-claim period back to "The Catacombs (M3)" via Tasks.DungeonRewardAttribution -
        // the classifier itself only ever sees the resolved location.
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 40 } };
        var result = Classifier.Classify("The Catacombs (M3)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("M3");
    }

    [Test]
    public void M7ZoneWithKismetFeatherConsumed_ClassifiesToM7Kismet()
    {
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 40 }, { "KISMET_FEATHER", -2 } };
        var result = Classifier.Classify("The Catacombs (M7)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("M7 (Kismet)");
    }

    /// <summary>
    /// Purchase evidence must still win the tie inside the classifier even when it happens right
    /// after a dungeon run in the same zone (e.g. buying a Hyperion in the Dungeon Hub right after
    /// clearing M7, before walking away) - TaskClassifier orders item-evidence matches ahead of
    /// location-only matches regardless of matchedValue, so the floor task (location-only, no
    /// DetectionItems) never outranks the AUCTION_PURCHASE-matched hidden task.
    /// </summary>
    [Test]
    public void AuctionPurchaseInDungeonZone_ClassifiesToAuctionPurchases_NotTheFloorTask()
    {
        var items = new Dictionary<string, int> { { "HYPERION", 1 }, { PseudoItems.AUCTION_PURCHASE, 970_000_000 } };
        var prices = new Dictionary<string, double> { { "HYPERION", 970_000_000 } };
        var result = Classifier.Classify("The Catacombs (M7)", items, 10, prices: prices);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Auction Purchases");
    }

    // ── New tasks discovered from unclassified production revenue (2026-09) ──

    [Test]
    public void TorrhusCanyonWithHelixLog_ClassifiesToHelixForaging()
    {
        var items = new Dictionary<string, int> { { "HELIX_LOG", 200 } };
        var result = Classifier.Classify("Torrhus Canyon", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Helix Foraging");
    }

    [Test]
    public void TangleburgWithFigLog_ClassifiesToFigForaging()
    {
        var items = new Dictionary<string, int> { { "FIG_LOG", 300 } };
        var result = Classifier.Classify("Tangleburg", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Fig Foraging");
    }

    [Test]
    public void TheMistWithVolta_ClassifiesToGhostTheMist()
    {
        var items = new Dictionary<string, int> { { "VOLTA", 20 } };
        var result = Classifier.Classify("The Mist", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Ghost (The Mist)");
    }

    [Test]
    public void TheMistWithShardGhost_ClassifiesToGhostHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_GHOST", 20 } };
        var result = Classifier.Classify("The Mist", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Ghost (Hunting)");
    }

    [Test]
    public void SmolderingTombWithBurningsoulShard_StillClassifiesToInfernoDemonlord()
    {
        // BlazeFarmingTask must not steal BurningsoulTask's classification.
        var items = new Dictionary<string, int> { { "SHARD_BURNINGSOUL", 5 } };
        var result = Classifier.Classify("Smoldering Tomb", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Inferno Demonlord");
    }

    [Test]
    public void SmolderingTombWithDerelictAshe_ClassifiesToBlazeSlayer()
    {
        var items = new Dictionary<string, int> { { "DERELICT_ASHE", 100 } };
        var result = Classifier.Classify("Smoldering Tomb", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Blaze Slayer");
    }

    [Test]
    public void GardenWithPestCountAndWheat_ClassifiesToWheatFarming()
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_WHEAT", 50 } };
        var result = Classifier.Classify("The Garden  x3", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Wheat Farming");
    }

    [Test]
    public void RenamedPlotWithCarrot_ClassifiesToCarrotFarming()
    {
        var items = new Dictionary<string, int> { { "CARROT_ITEM", 40 } };
        var result = Classifier.Classify("Plot - Left Farm 2", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Carrot Farming");
    }

    [Test]
    public void GardenWithEnchantedPotato_ClassifiesToPotatoFarming()
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_POTATO", 40 } };
        var result = Classifier.Classify("The Garden", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Potato Farming");
    }

    [Test]
    public void GardenMixedCrops_MoreCoinValueInMelons_ClassifiesToMelonFarming()
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_WHEAT", 50 }, { "ENCHANTED_MELON", 50 } };
        var prices = new Dictionary<string, double> { { "ENCHANTED_WHEAT", 100 }, { "ENCHANTED_MELON", 10_000 } };
        var result = Classifier.Classify("The Garden", items, 10, prices: prices);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Melon Farming", "the matched-value tie-break picks the more valuable matched crop");
    }

    [Test]
    public void GardenWithBuilderCactus_ClassifiesToCactusFarming()
    {
        // Production tags the raw cactus drop as BUILDER_CACTUS, not the vanilla CACTUS item id.
        var items = new Dictionary<string, int> { { "BUILDER_CACTUS", 60 } };
        var result = Classifier.Classify("The Garden", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Cactus Farming");
    }

    [Test]
    public void GardenWithPestVinyl_StillClassifiesToPest()
    {
        // PEST_KILL/PESTERMINATOR were fabricated tags that never occur in production - PestTask now
        // detects on real pest drops (Pest Vinyls + rarer pest items, see PestEvidence in GardenTasks.cs).
        var items = new Dictionary<string, int> { { "VINYL_SLOW_AND_GROOVY", 1 }, { "LOCUST_LARVA", 2 } };
        var result = Classifier.Classify("The Garden", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Pest", "PestTask's own detection items must still win over the per-crop farming tasks");
    }

    // ── New tasks discovered from unclassified production revenue (2026-09-27) ──

    [Test]
    public void RampartsQuarryWithMithrilOre_ClassifiesToMithrilMining()
    {
        var items = new Dictionary<string, int> { { "MITHRIL_ORE", 400 }, { "TITANIUM_ORE", 40 } };
        var result = Classifier.Classify("Rampart's Quarry", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Mithril Mining");
    }

    [Test]
    public void GlaciteTunnelsWithGlacite_ClassifiesToGlaciteMining()
    {
        var items = new Dictionary<string, int> { { "GLACITE", 500 } };
        var result = Classifier.Classify("Glacite Tunnels", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Glacite Mining");
    }

    [Test]
    public void GlaciteTunnelsWithTungsten_StillClassifiesToTungstenMining()
    {
        var items = new Dictionary<string, int> { { "TUNGSTEN", 300 } };
        var result = Classifier.Classify("Glacite Tunnels", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Tungsten Mining");
    }

    [Test]
    public void GravelMinesWithFlint_ClassifiesToFlintMining()
    {
        var items = new Dictionary<string, int> { { "FLINT", 300 } };
        var result = Classifier.Classify("Gravel Mines", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Flint Mining");
    }

    [Test]
    public void MurkwaterLochWithMangroveLog_ClassifiesToMangroveForaging()
    {
        var items = new Dictionary<string, int> { { "MANGROVE_LOG", 300 } };
        var result = Classifier.Classify("Murkwater Loch", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Mangrove Foraging");
    }

    [Test]
    public void MurkwaterLochWithFigLog_StillClassifiesToFigForaging()
    {
        var items = new Dictionary<string, int> { { "FIG_LOG", 300 } };
        var result = Classifier.Classify("Murkwater Loch", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Fig Foraging");
    }

    [Test]
    public void StrongholdWithMagmaFish_ClassifiesToCrimsonFishing()
    {
        var items = new Dictionary<string, int> { { "MAGMA_FISH", 150 } };
        var result = Classifier.Classify("Stronghold", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Crimson Fishing");
    }

    [Test]
    public void TangleburgWithInvisibugShard_ClassifiesToInvisibugHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_INVISIBUG", 200 } };
        var result = Classifier.Classify("Tangleburg", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Invisibug (Hunting)");
    }

    // ── Hidden accounting tasks (HiddenTasks.cs / PseudoItems.cs) ──

    [Test]
    public void YourIsland_WithMinionProductAndEnchantedForm_ClassifiesToMinionCollection()
    {
        // "Your Island" is deliberately ambiguous/unmapped in SkyblockZones (AmbiguousZones), but
        // SkyblockZones.Matches checks a task's Locations against the exact zone string first, so
        // MinionCollectionTask's literal ["Your Island"] Locations still matches directly.
        var items = new Dictionary<string, int> { { "SLIME_BALL", 300 }, { "ENCHANTED_SLIME_BALL", 5 } };
        var result = Classifier.Classify("Your Island", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Minion Collection");
    }

    [Test]
    public void YourIsland_WithNonMinionItem_ClassifiesToPrivateIslandActivity()
    {
        // MinionCollectionTask always needs its own DetectionItems (never matches HYPERION), so a
        // non-minion item on the private island now falls back to the hidden "Private Island
        // Activity" catch-all (HiddenTasks.cs, section D) instead of going unclassified.
        var items = new Dictionary<string, int> { { "HYPERION", 1 } };
        var result = Classifier.Classify("Your Island", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Private Island Activity");
    }

    [Test]
    public void BazaarPurchase_DominatingWindow_ClassifiesToBazaarPurchases()
    {
        var items = new Dictionary<string, int> { { "BAZAAR_PURCHASE", 50_000_000 }, { "ENCHANTED_WHEAT", 2000 } };
        var prices = new Dictionary<string, double> { { "ENCHANTED_WHEAT", 200 } };
        var result = Classifier.Classify("Bazaar Alley", items, 10, prices: prices);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Bazaar Purchases", "50,000,000 coins spent beats the 400,000 matched grind value");
    }

    [Test]
    public void GardenGrindValue_BeatsSmallerBazaarPurchase_ClassifiesToWheatFarming()
    {
        var items = new Dictionary<string, int> { { "WHEAT", 3000 }, { "BAZAAR_PURCHASE", 20_000 } };
        var prices = new Dictionary<string, double> { { "WHEAT", 10 } };
        var result = Classifier.Classify("Plot - 3", items, 10, prices: prices);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Wheat Farming", "30,000 coins of grind value beats the 20,000 spent buying");
    }

    [Test]
    public void AuctionPurchase_DominatingWindow_ClassifiesToAuctionPurchases()
    {
        var items = new Dictionary<string, int> { { "AUCTION_PURCHASE", 973_000_000 }, { "HYPERION", 2 } };
        var result = Classifier.Classify("Village", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Auction Purchases");
    }

    // ── section B: DetectionSignature.MinLocationOnlyItems (2026-09-28 production log analysis) ──

    [Test]
    public void CatacombsF6Zone_WithOnlyTwoRewardChestItems_StillClassifiesToF6()
    {
        // A Catacombs floor zone is a dedicated instance that can only mean that one floor - unlike
        // a shared open-world zone, 2 items is already unambiguous evidence (BaseDungeonTask sets
        // MinLocationOnlyItems = 1).
        var items = new Dictionary<string, int> { { "SUPERBOOM_TNT", 2 } };
        var result = Classifier.Classify("The Catacombs (F6)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("F6");
    }

    [Test]
    public void SharedOpenWorldLocationOnlyTask_StillNeedsDefaultFiveItems()
    {
        // Contrast with CatacombsF6Zone_WithOnlyTwoRewardChestItems_StillClassifiesToF6 above -
        // BayouFishingTask keeps the default MinLocationOnlyItems (5).
        var items = new Dictionary<string, int> { { "COCONUT", 3 } };
        Classifier.Classify("Backwater Bayou", items, 10).Should().BeNull();
    }

    // ── section C: new/corrected tasks (2026-09-28 production log analysis) ──

    [Test]
    public void CryptsWithRevenantFlesh_ClassifiesToRevenantSlayer()
    {
        var items = new Dictionary<string, int> { { "ROTTEN_FLESH", 43 }, { "REVENANT_FLESH", 126 }, { "GOLD_INGOT", 89 } };
        var result = Classifier.Classify("Crypts", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Revenant Slayer",
            "GoldenGhoulTask also matches via GOLD_INGOT, but Revenant Slayer's higher Priority breaks the tie");
    }

    [Test]
    public void SoulCaveWithWolfToothAndHamsterWheel_ClassifiesToSvenSlayer()
    {
        var items = new Dictionary<string, int> { { "WOLF_TOOTH", 257 }, { "BONE", 36 }, { "HAMSTER_WHEEL", 12 }, { "LOG", 11 } };
        var result = Classifier.Classify("Soul Cave", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Sven Slayer");
    }

    [Test]
    public void BurningDesertWithTarantulaWeb_ClassifiesToTarantulaSlayerCrimsonIsle()
    {
        var items = new Dictionary<string, int> { { "STRING", 18 }, { "TARANTULA_WEB", 71 }, { "BURNING_EYE", 4 }, { "TOXIC_ARROW_POISON", 64 } };
        var result = Classifier.Classify("Burning Desert", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Tarantula Slayer (Crimson Isle)");
    }

    [Test]
    public void CourtyardWithGlowstoneDust_ClassifiesToMageOutlaw()
    {
        var items = new Dictionary<string, int> { { "GLOWSTONE_DUST", 30 }, { "ENCHANTED_GLOWSTONE_DUST", 1 }, { "SPELL_POWDER", 1 } };
        var result = Classifier.Classify("Courtyard", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Mage Outlaw");
    }

    [Test]
    public void TorrhusCanyonWithHoneycomb_ClassifiesToHoneycombGathering()
    {
        var items = new Dictionary<string, int> { { "HONEYCOMB", 81 }, { "ENCHANTED_HONEYCOMB", 3 }, { "SHARD_HONEYBUZZ", 2 } };
        var result = Classifier.Classify("Torrhus Canyon", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Honeycomb Gathering");
    }

    [Test]
    public void TorrhusHeightsWithTikiShards_ClassifiesToTikiHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_SHRIEKY_TIKI", 3 }, { "SHARD_SNEAKY_TIKI", 3 }, { "SHARD_CHEEKY_TIKI", 3 } };
        var result = Classifier.Classify("Torrhus Heights", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Tiki (Hunting)");
    }

    [Test]
    public void WestReachesWithMudwormShard_ClassifiesToMudwormHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_MUDWORM", 3 } };
        var result = Classifier.Classify("West Reaches", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Mudworm (Hunting)");
    }

    [Test]
    public void NorthReachesWithBirriesShardAndLushlilac_ClassifiesToBirriesHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_BIRRIES", 3 }, { "LUSHLILAC", 12 } };
        var result = Classifier.Classify("North Reaches", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Birries (Hunting)");
    }

    [Test]
    public void MurkwaterLochWithVinesapAndDeepRoot_ClassifiesToMangroveForaging()
    {
        var items = new Dictionary<string, int> { { "VINESAP", 6 }, { "DEEP_ROOT", 1 } };
        var result = Classifier.Classify("Murkwater Loch", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Mangrove Foraging");
    }

    [Test]
    public void UpperMinesWithTreasureHoarderShard_ClassifiesToTreasureHoarderHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_TREASURE_HOARDER", 4 }, { "STARFALL", 2 } };
        var result = Classifier.Classify("Upper Mines", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Treasure Hoarder (Hunting)");
    }

    [Test]
    public void GoblinBurrowsWithGoblinEgg_ClassifiesToGoblinFarming()
    {
        var items = new Dictionary<string, int> { { "GOBLIN_EGG", 3 }, { "GOBLIN_LEGGINGS", 1 } };
        var result = Classifier.Classify("Goblin Burrows", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Goblin Farming");
    }

    [Test]
    public void GreatIceWallWithGlaciteWalkerShard_ClassifiesToGlaciteWalkerHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_GLACITE_WALKER", 26 } };
        var result = Classifier.Classify("Great Ice Wall", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Glacite Walker (Hunting)");
    }

    [Test]
    public void DwarvenBaseCampWithTungsten_ClassifiesToTungstenMining()
    {
        var items = new Dictionary<string, int> { { "TUNGSTEN", 443 }, { "ENCHANTED_TUNGSTEN", 3 } };
        var result = Classifier.Classify("Dwarven Base Camp", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Tungsten Mining");
    }

    [Test]
    public void MinesOfDivanWithChestLoot_ClassifiesToCrystalHollowsPowderMining()
    {
        var items = new Dictionary<string, int> { { "HARD_STONE", 263 }, { "ENCHANTED_HARD_STONE", 5 }, { "TREASURITE", 2 }, { "PICKONIMBUS", 1 } };
        var result = Classifier.Classify("Mines of Divan", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Crystal Hollows Powder Mining");
    }

    [Test]
    public void MithrilDepositsWithChestLoot_ClassifiesToMithrilDepositsPowderMining()
    {
        var items = new Dictionary<string, int> { { "HARD_STONE", 200 }, { "WISHING_COMPASS", 3 } };
        var result = Classifier.Classify("Mithril Deposits", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Mithril Deposits Powder Mining",
            "the zone-specific task has higher Priority than the island-wide Crystal Hollows Powder Mining fallback");
    }

    [Test]
    public void MithrilDepositsGemstonesDominate_StillClassifiesToJadeMining_WithPrices()
    {
        var items = new Dictionary<string, int> { { "ROUGH_JADE_GEM", 900 }, { "FLAWED_JADE_GEM", 40 }, { "HARD_STONE", 100 } };
        var prices = new Dictionary<string, double> { { "ROUGH_JADE_GEM", 50 }, { "FLAWED_JADE_GEM", 400 }, { "HARD_STONE", 5 } };
        var result = Classifier.Classify("Mithril Deposits", items, 10, prices: prices);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Jade Mining", "900*50 + 40*400 = 61,000 matched value beats 100*5 = 500 for the powder mining task");
    }

    /// <summary>
    /// Documents current behavior only (not a guaranteed design invariant): without prices every
    /// matched candidate's matchedValue is 0 (PseudoItems.ClassifierWeight falls back to 0 with no
    /// price dict), so the tie-break falls through Priority (both default 0, equal) to alphabetical
    /// MethodName order - "Jade Mining" sorts before "Mithril Deposits Powder Mining" and happens to
    /// still win, but that is a naming coincidence, not something callers should rely on.
    /// </summary>
    [Test]
    public void MithrilDepositsGemstonesDominate_WithoutPrices_StillPicksJadeMiningAlphabetically()
    {
        var items = new Dictionary<string, int> { { "ROUGH_JADE_GEM", 900 }, { "FLAWED_JADE_GEM", 40 }, { "HARD_STONE", 100 } };
        var result = Classifier.Classify("Mithril Deposits", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Jade Mining");
    }

    [Test]
    public void TheGardenWithPestVinyl_ClassifiesToPest()
    {
        var items = new Dictionary<string, int> { { "VINYL_SLOW_AND_GROOVY", 1 }, { "LOCUST_LARVA", 2 } };
        var result = Classifier.Classify("The Garden", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Pest");
    }

    [Test]
    public void ObsidianSanctuaryWithMinerZombieShard_ClassifiesToMinerZombieHunting()
    {
        var items = new Dictionary<string, int> { { "SHARD_MINER_ZOMBIE", 81 }, { "ROTTEN_FLESH", 34 } };
        var result = Classifier.Classify("Obsidian Sanctuary", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Miner Zombie (Hunting)");
    }

    [Test]
    public void ObsidianSanctuaryWithObsidianAndNoShard_StillClassifiesToObsidianMining()
    {
        // Miner Zombie (Hunting) must only win when its own shard is present, not steal every
        // Obsidian Sanctuary period from Obsidian Mining.
        var items = new Dictionary<string, int> { { "OBSIDIAN", 120 }, { "ENCHANTED_OBSIDIAN", 3 } };
        var result = Classifier.Classify("Obsidian Sanctuary", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Obsidian Mining");
    }

    [Test]
    public void CritterSafariWithShard_ClassifiesToCritterSafari()
    {
        var items = new Dictionary<string, int> { { "SHARD_FOXTROT", 3 } };
        var result = Classifier.Classify("Critter Safari", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Critter Safari");
    }

    /// <summary>
    /// Production 2026-10-03 (nameeagleismy): a Safari run period that also held 100 Helix Logs was
    /// classified as Helix Foraging, which matched the whole Torrhus island.
    /// </summary>
    [Test]
    public void CritterSafariWithHelixLogs_StaysCritterSafari()
    {
        var items = new Dictionary<string, int> { { "SHARD_BLOODBAT", 5 }, { "SHARD_HIDEONWALL", 3 }, { "HELIX_LOG", 100 } };
        Classifier.Classify("Critter Safari", items, 10)!.TaskName.Should().Be("Critter Safari");
        Classifier.Classify("Torrhus Heights", new() { { "HELIX_LOG", 100 } }, 10)!.TaskName.Should().Be("Helix Foraging");
        Classifier.Classify("Spring Path", new() { { "HELIX_LOG", 100 } }, 10)!.TaskName.Should().Be("Helix Foraging");
    }

    // ── section D: hidden accounting tasks for zones with no money making method (HiddenTasks.cs) ──

    [Test]
    public void VillageWithRawFish_PublicFishingTask_StillWinsOverHubTrading()
    {
        var items = new Dictionary<string, int> { { "RAW_FISH", 40 }, { "CLAY_BALL", 9 } };
        var result = Classifier.Classify("Village", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Water Fishing");
    }

    [Test]
    public void VillageWithUnrelatedItem_ClassifiesToHubTrading()
    {
        var items = new Dictionary<string, int> { { "GREEN_CANDY", 3 } };
        var result = Classifier.Classify("Village", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Hub Trading");
    }

    [Test]
    public void YourIslandWithNonMinionShard_ClassifiesToPrivateIslandActivity()
    {
        var items = new Dictionary<string, int> { { "SHARD_COCOALEECH", 256 } };
        var result = Classifier.Classify("Your Island", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Private Island Activity");
    }

    [Test]
    public void TheForgeWithSkeletonKey_ClassifiesToForgeClaims()
    {
        var items = new Dictionary<string, int> { { "SKELETON_KEY", 6 } };
        var result = Classifier.Classify("The Forge", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Forge Claims");
    }

    [Test]
    public void YourIslandWithMinionProduct_StillClassifiesToMinionCollection()
    {
        var items = new Dictionary<string, int> { { "CRUDE_GABAGOOL", 628 } };
        var result = Classifier.Classify("Your Island", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Minion Collection");
    }

    [Test]
    public void CommunityCenterAndWizardTower_LiteralMatch_ClassifyToHubTrading()
    {
        // "Community Center"/"Wizard Tower" stay in SkyblockZones.AmbiguousZones (shared with other
        // islands - see SkyblockZonesTests.AmbiguousZone_WithFormatCodeNoise_StillResolvesNull), but
        // Matches() checks a task's Locations against the exact zone string first, so the literal
        // entries on HubTradingTask still match directly (same trick as MinionCollectionTask/"Your Island").
        Classifier.Classify("Community Center", new() { { "GREEN_CANDY", 2 } }, 10)?.TaskName.Should().Be("Hub Trading");
        Classifier.Classify("Wizard Tower", new() { { "GREEN_CANDY", 2 } }, 10)?.TaskName.Should().Be("Hub Trading");
    }

    [Test]
    public void PlayerMuseumZone_CanonicalizesAndClassifiesToHubTrading()
    {
        var items = new Dictionary<string, int> { { "GREEN_CANDY", 2 } };
        var result = Classifier.Classify("LXMini's Museum", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Hub Trading");
    }

    [Test]
    public void Classify_TwoPseudoTagsNearIntMax_DoesNotOverflow()
    {
        var items = new Dictionary<string, int>
        {
            { PseudoItems.BAZAAR_PURCHASE, int.MaxValue - 1 },
            { PseudoItems.AUCTION_PURCHASE, int.MaxValue - 1 },
            { PseudoItems.DUNGEON_CHEST_COST, int.MinValue },
        };
        Assert.DoesNotThrow(() => Classifier.Classify("Catacombs", items, 30));
    }

    [Test]
    public void Diana_UnincorporatedWithAncientClaw_IsDiana()
        => Classifier.Classify("Unincorporated", new() { { "ANCIENT_CLAW", 500 } }, 10).Should().NotBeNull().And.Subject.As<Classification>().TaskName.Should().Be("Diana");

    [Test]
    public void Diana_BurrowChainPeriods_AllClassifyToTheSingleDianaTask()
    {
        // claws only, treasure only (gold + feathers) and a lone Harpy shard are the same activity
        Classifier.Classify("Hub", new() { { "ANCIENT_CLAW", 300 } }, 10)?.TaskName.Should().Be("Diana");
        Classifier.Classify("Hub", new() { { "ENCHANTED_GOLD", 12 }, { "GRIFFIN_FEATHER", 6 } }, 10)?.TaskName.Should().Be("Diana");
        Classifier.Classify("Ruins", new() { { "SHARD_HARPY", 3 } }, 10)?.TaskName.Should().Be("Diana");
        Classifier.Classify("Graveyard", new() { { "SHARD_SPHINX", 2 } }, 10)?.TaskName.Should().Be("Diana");
        // mixed period, claws far more valuable than gold: still the one task
        Classifier.Classify("Hub", new() { { "ANCIENT_CLAW", 300 }, { "ENCHANTED_GOLD", 4 } }, 10,
            prices: new() { { "ANCIENT_CLAW", 5000 }, { "ENCHANTED_GOLD", 100 } })?.TaskName.Should().Be("Diana");
    }

    [Test]
    public void Diana_HuntingVariantIsGone()
    {
        var registry = new TaskRegistry();
        registry.GetByName("Diana (Hunting)").Should().BeNull();
        registry.MethodTasks.Count(t => t.GetDetectionSignature().MethodName.StartsWith("Diana")).Should().Be(1);
    }

    [TestCase("MANTI_CORE")]
    [TestCase("FATEFUL_STINGER")]
    [TestCase("BRAIN_FOOD")]
    [TestCase("SHIMMERING_WOOL")]
    [TestCase("BRAIDED_GRIFFIN_FEATHER")]
    [TestCase("CROWN_OF_GREED")]
    public void Diana_RuinsSingleRareDrop_IsDiana(string tag)
        => Classifier.Classify("Ruins", new() { { tag, 1 } }, 10).Should().NotBeNull().And.Subject.As<Classification>().TaskName.Should().Be("Diana");

    [Test]
    public void Diana_ForestEnchantedGoldOnly_IsDiana()
        => Classifier.Classify("Forest", new() { { "ENCHANTED_GOLD", 8 } }, 10).Should().NotBeNull().And.Subject.As<Classification>().TaskName.Should().Be("Diana");

    [Test]
    public void Diana_TradeCenterGriffinFeather_IsADianaTask()
    {
        var result = Classifier.Classify("Trade Center", new() { { "GRIFFIN_FEATHER", 5 } }, 10);
        result.Should().NotBeNull();
        result.TaskName.Should().StartWith("Diana");
    }

    [Test]
    public void GoldMineEnchantedGold_IsNotDiana()
    {
        var result = Classifier.Classify("Gold Mine", new() { { "ENCHANTED_GOLD", 8 } }, 10);
        (result?.TaskName ?? "").Should().NotStartWith("Diana");
    }

    // ── Dungeon/Kuudra periods with only cost/consumables (production 2026-10-01) ──

    [Test]
    public void FloorPeriodWithOnlyChestCost_ClassifiesToThatFloor()
    {
        var result = Classifier.Classify("The Catacombs (F7)", new() { { "DUNGEON_CHEST_COST", -6_000_000 } }, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("F7");
    }

    [Test]
    public void KuudraTierPeriodWithOnlyConsumedItems_ClassifiesToKuudraTier()
    {
        var result = Classifier.Classify("Kuudra's Hollow (T4)", new() { { "TOXIC_ARROW_POISON", -120 }, { "ENDER_PEARL", -6 } }, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Kuudra T4");
    }

    [Test]
    public void NonInstanceZoneWithOnlyNegativeItems_StaysUnclassified()
    {
        Classifier.Classify("Gold Mine", new() { { "TOXIC_ARROW_POISON", -120 }, { "ENDER_PEARL", -6 } }, 10).Should().BeNull();
    }

    [Test]
    public void ResolvedFloorPeriod_BiggerChestCostThanBazaarPurchase_ClassifiesToFloor()
    {
        var items = new Dictionary<string, int> { { "BAZAAR_PURCHASE", 4_134_672 }, { "DUNGEON_CHEST_COST", -7_100_000 } };
        var result = Classifier.Classify("The Catacombs (F7)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("F7");
    }

    [Test]
    public void ResolvedFloorPeriod_SmallerChestCostThanBazaarPurchase_StaysBazaarPurchases()
    {
        var items = new Dictionary<string, int> { { "BAZAAR_PURCHASE", 4_088_598 }, { "DUNGEON_CHEST_COST", -1_000_000 } };
        var result = Classifier.Classify("The Catacombs (F7)", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Bazaar Purchases");
    }

    [Test]
    public void GardenPlotWithOnlyCommonPestDrops_ClassifiesToPest()
    {
        var items = new Dictionary<string, int>
        {
            { "DUNG", 4 }, { "PLANT_MATTER", 4 }, { "COMPOST", 4 }, { "JELLY", 4 }, { "CHEESE_FUEL", 3 }
        };
        var result = Classifier.Classify("Plot - 3", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Pest");
    }

    private static string? NameAt(string location, Dictionary<string, int> items) => Classifier.Classify(location, items, 10)?.TaskName;

    private static bool IsHidden(string taskName) =>
        Registry.MethodTasks.First(t => t.GetDetectionSignature().MethodName == taskName).IsHidden;

    // ── 2026-10-01 coverage pass ──

    [Test]
    public void RuinsWithWolfTooth_ClassifiesToSvenSlayer()
        => NameAt("Ruins", new() { { "WOLF_TOOTH", 711 }, { "BONE", 221 }, { "FURBALL", 2 } }).Should().Be("Sven Slayer");

    [Test]
    public void RuinsWithOnlyBone_IsNotSvenSlayer()
        => NameAt("Ruins", new() { { "BONE", 30 } }).Should().NotBe("Sven Slayer");

    [Test]
    public void SpidersDenFishing_ClassifiesToWaterFishing()
        => NameAt("Spider's Den", new() { { "RAW_FISH", 310 }, { "WATER_LILY", 210 }, { "INK_SACK", 89 } }).Should().Be("Water Fishing");

    [Test]
    public void JungleFishing_ClassifiesToWaterFishing()
        => NameAt("Jungle", new() { { "RAW_FISH", 310 }, { "WATER_LILY", 210 }, { "INK_SACK", 89 } }).Should().Be("Water Fishing");

    [Test]
    public void GoblinHoldoutFishWithWormMembrane_StillWaterWormFishing()
        => NameAt("Goblin Holdout", new() { { "RAW_FISH", 100 }, { "WORM_MEMBRANE", 5 } }).Should().Be("Water Worm Fishing");

    [Test]
    public void SpidersDenSpiderDropsOnly_IsNotWaterFishing()
    {
        var items = new Dictionary<string, int> { { "STRING", 200 }, { "SPIDER_EYE", 150 } };
        NameAt("Spider's Den", items).Should().NotBe("Water Fishing").And.NotBe("Spooky Fishing");
    }

    [Test]
    public void SpidersDenSpookyFishing_ClassifiesToSpookyFishing()
        => NameAt("Spider's Den", new() { { "PUMPKIN", 410 }, { "GREEN_CANDY", 256 }, { "RAW_FISH", 205 }, { "WATER_LILY", 149 }, { "WEREWOLF_SKIN", 3 } })
            .Should().Be("Spooky Fishing");

    [Test]
    public void JungleSpookyFishing_ClassifiesToSpookyFishing()
        => NameAt("Jungle", new() { { "PUMPKIN", 410 }, { "GREEN_CANDY", 256 }, { "RAW_FISH", 205 }, { "WATER_LILY", 149 }, { "WEREWOLF_SKIN", 3 } })
            .Should().Be("Spooky Fishing");

    [Test]
    public void GraveyardMobCandy_IsNotSpookyFishing()
        => NameAt("Graveyard", new() { { "GREEN_CANDY", 25 }, { "PURPLE_CANDY", 17 } }).Should().NotBe("Spooky Fishing");

    [Test]
    public void BayouFishWithWerewolfSkin_StaysBayouFishing()
        => NameAt("Backwater Bayou", new() { { "RAW_FISH", 100 }, { "WEREWOLF_SKIN", 2 } }).Should().Be("Bayou Fishing");

    [Test]
    public void BellyOfTheBeastHeavyPearls_ClassifiesToHeavyPearls()
        => NameAt("Belly of the Beast", new() { { "HEAVY_PEARL", 5 } }).Should().Be("Heavy Pearls");

    [Test]
    public void DungeonHubKuudraLoot_ClassifiesToKuudraChestClaims()
        => NameAt("Dungeon Hub", new()
        {
            { "KUUDRA_TEETH", 124 }, { "ENCHANTED_BOOK", 5 }, { "KISMET_FEATHER", 2 }, { "CRIMSON_BOOTS", 2 },
            { "AURORA_CHESTPLATE", 2 }, { "WHEEL_OF_FATE", 1 }
        }).Should().Be("Kuudra Chest Claims");

    [Test]
    public void DungeonHubDungeonLootOnly_IsNotKuudraChestClaims()
        => NameAt("Dungeon Hub", new() { { "ENCHANTED_BONE", 7 }, { "HOLY_FRAGMENT", 6 }, { "CONJURING_SWORD", 3 } })
            .Should().NotBe("Kuudra Chest Claims");

    [Test]
    public void KuudraT5ZoneWithTeeth_StillKuudraT5()
        => NameAt("Kuudra's Hollow (T5)", new() { { "KUUDRA_TEETH", 50 }, { "ESSENCE_CRIMSON", 500 } }).Should().Be("Kuudra T5");

    [Test]
    public void GardenVisitorRewards_ClassifiesToGardenVisitors()
        => NameAt("The Garden", new() { { "CARNIVAL_TICKET", 15 }, { "FEAST_FLASK", 4 } }).Should().Be("Garden Visitors");

    [Test]
    public void GardenCarrotsWithJacobsTicket_StaysCarrotFarming()
    {
        // null prices, like the other crop tests here
        NameAt("Plot - 4", new() { { "CARROT_ITEM", 5000 }, { "ENCHANTED_CARROT", 40 }, { "JACOBS_TICKET", 1 } }).Should().Be("Carrot Farming");
    }

    [Test]
    public void GardenPestDropsWithSqueakyToy_StaysPest()
        => NameAt("The Garden", new() { { "DUNG", 4 }, { "PLANT_MATTER", 4 }, { "COMPOST", 4 }, { "JELLY", 4 }, { "SQUEAKY_TOY", 1 } }).Should().Be("Pest");

    [Test]
    public void WyldWoodsRiftItems_ClassifiesToHiddenRiftActivity()
    {
        var name = NameAt("Wyld Woods", new() { { "HEMOVIBE", 64 }, { "VAMPIRIC_MELON", 20 } });
        name.Should().Be("Rift Activity");
        IsHidden(name!).Should().BeTrue();
    }

    [Test]
    public void RiftBazaarPurchase_ItemMatchedTaskStillWinsOverRiftActivity()
        => NameAt("Wyld Woods", new() { { "BAZAAR_PURCHASE", 100_000 }, { "HEMOVIBE", 64 } }).Should().Be("Bazaar Purchases");

    [Test]
    public void UnknownLocation_ClassifiesToHiddenUnknownLocation()
    {
        var name = NameAt("Unknown", new() { { "HYPERION", 1 }, { "ENCHANTED_BOOK", 4 } });
        name.Should().Be("Unknown Location");
        IsHidden(name!).Should().BeTrue();
    }

    // ── 2026-10-03 production method tasks ──

    private static string? NameAtPriced(string location, Dictionary<string, int> items, Dictionary<string, double> prices)
        => Classifier.Classify(location, items, 10, prices: prices)?.TaskName;

    [Test]
    [TestCase("Forest", "LOG", "ENCHANTED_OAK_LOG", "Oak Foraging")]
    [TestCase("Birch Park", "LOG:2", "ENCHANTED_BIRCH_LOG", "Birch Foraging")]
    [TestCase("Spruce Woods", "LOG:1", "ENCHANTED_SPRUCE_LOG", "Spruce Foraging")]
    [TestCase("Dark Thicket", "LOG_2:1", "ENCHANTED_DARK_OAK_LOG", "Dark Oak Foraging")]
    [TestCase("Savanna Woodland", "LOG_2", "ENCHANTED_ACACIA_LOG", "Acacia Foraging")]
    [TestCase("Jungle Island", "LOG:3", "ENCHANTED_JUNGLE_LOG", "Jungle Foraging")]
    public void WoodZones_ClassifyToTheirForagingTask(string zone, string log, string enchanted, string expected)
    {
        NameAt(zone, new() { { log, 250 } }).Should().Be(expected);
        NameAt(zone, new() { { enchanted, 3 } }).Should().Be(expected);
    }

    [Test]
    public void ForestDianaPeriodWithAFewLogs_StaysDiana()
        => NameAtPriced("Forest", new() { { "ENCHANTED_GOLD", 40 }, { "ANCIENT_CLAW", 20 }, { "GRIFFIN_FEATHER", 2 }, { "LOG", 12 } },
            new() { { "ENCHANTED_GOLD", 15_000 }, { "ANCIENT_CLAW", 400_000 }, { "GRIFFIN_FEATHER", 20_000 }, { "LOG", 5 } }).Should().Be("Diana");

    [Test]
    [TestCase("Crystal Hollows", "RUBY", "Ruby Mining")]
    [TestCase("Mines of Divan", "RUBY", "Ruby Mining")]
    [TestCase("Crystal Nucleus", "RUBY", "Ruby Mining")]
    [TestCase("Khazad-dûm", "TOPAZ", "Topaz Mining")]
    [TestCase("Magma Fields", "TOPAZ", "Topaz Mining")]
    [TestCase("Glacite Tunnels", "AQUAMARINE", "Aquamarine Mining")]
    [TestCase("Glacite Mineshafts", "CITRINE", "Citrine Mining")]
    [TestCase("Dwarven Base Camp", "ONYX", "Onyx Mining")]
    public void NewGemstoneZones_ClassifyToTheirGemTask(string zone, string gem, string expected)
        => NameAt(zone, new() { { $"FLAWED_{gem}_GEM", 90 }, { $"FINE_{gem}_GEM", 30 } }).Should().Be(expected);

    [Test]
    public void GlaciteRoughRubyIsRuby_ButAquamarineNeedsItsOwnGems()
    {
        NameAt("Glacite Tunnels", new() { { "FINE_ONYX_GEM", 40 } }).Should().Be("Onyx Mining");
        NameAt("Glacite Tunnels", new() { { "FINE_AQUAMARINE_GEM", 40 }, { "FLAWED_AQUAMARINE_GEM", 100 } }).Should().Be("Aquamarine Mining");
    }

    [Test]
    [TestCase("Fossil Research Center", "CLAW_FOSSIL")]
    [TestCase("Fossil Research Center", "FOSSIL_THE_FISH")]
    public void FossilItems_ClassifyToFossilExcavation(string zone, string item)
        => NameAt(zone, new() { { item, 6 } }).Should().Be("Fossil Excavation");

    [Test]
    public void GlaciteMiningWithExpensiveScrap_StaysMining()
        => NameAtPriced("Glacite Mineshafts", new() { { "UMBER", 900 }, { "ENCHANTED_UMBER", 4 }, { "SUSPICIOUS_SCRAP", 2 } },
            new() { { "ENCHANTED_UMBER", 2_000 }, { "SUSPICIOUS_SCRAP", 500_000 } }).Should().Be("Umber Mining");

    [Test]
    [TestCase("Torrhus Canyon", "SHARD_DUNG_BEETLE", "Dung Beetle (Hunting)")]
    [TestCase("Torrhus Springs", "SHARD_DUNG_BEETLE", "Dung Beetle (Hunting)")]
    [TestCase("Torrhus Heights", "SHARD_HIDEONSUN", "Hideonsun (Hunting)")]
    [TestCase("Spring Path", "SHARD_HIDEONSUN", "Hideonsun (Hunting)")]
    public void TorrhusShards_ClassifyToTheirHuntingTask(string zone, string shard, string expected)
        => NameAt(zone, new() { { shard, 3 } }).Should().Be(expected);

    [Test]
    [TestCase("South Reaches")]
    [TestCase("Stride-Ember Fissure")]
    public void StriderSurferShards_ClassifyToStridersurfer(string zone)
        => NameAt(zone, new() { { "SHARD_STRIDER_SURFER", 40 } }).Should().Be("Stridersurfer");

    [Test]
    [TestCase("Gold Mine", "GOLD_INGOT", "Gold Mining")]
    [TestCase("Royal Mines", "ENCHANTED_GOLD", "Gold Mining")]
    [TestCase("Gunpowder Mines", "IRON_INGOT", "Iron Mining")]
    [TestCase("Gold Mine", "ENCHANTED_IRON", "Iron Mining")]
    [TestCase("Lapis Quarry", "INK_SACK:4", "Lapis Mining")]
    [TestCase("Slimehill", "EMERALD", "Emerald Mining")]
    public void PureOreZones_ClassifyToTheirOreTask(string zone, string item, string expected)
        => NameAt(zone, new() { { item, 600 } }).Should().Be(expected);

    // ── Tarantula Slayer in the Spider's Den (was Voracious Spider) ──

    private static readonly Dictionary<string, double> SpiderPrices =
        new() { { "TARANTULA_WEB", 50 }, { "TOXIC_ARROW_POISON", 20 }, { "STRING", 100 }, { "SPIDER_EYE", 100 } };

    [Test]
    [TestCase("Arachne's Burrow")]
    [TestCase("Arachne's Sanctuary")]
    [TestCase("Spider's Den")]
    [TestCase("Spider Mound")]
    public void SpidersDenPeriodWithTarantulaWeb_IsTarantulaSlayer_EvenWithPricierString(string zone)
    {
        NameAtPriced(zone, new() { { "TARANTULA_WEB", 450 }, { "TOXIC_ARROW_POISON", 120 }, { "STRING", 300 } }, SpiderPrices).Should().Be("Tarantula Slayer");
        NameAt(zone, new() { { "TOXIC_ARROW_POISON", 120 }, { "TARANTULA_SILK", 2 } }).Should().Be("Tarantula Slayer");
    }

    [Test]
    [TestCase("Arachne's Burrow")]
    [TestCase("Arachne's Sanctuary")]
    public void SpidersDenPeriodWithOnlyStringAndSpiderEye_StaysVoraciousSpider(string zone)
        => NameAtPriced(zone, new() { { "STRING", 600 }, { "SPIDER_EYE", 150 } }, SpiderPrices).Should().Be("Voracious Spider");

    // ── Voidgloom slayer at the Zealot Bruiser Hideout (was Zealots (FD)) ──

    private static readonly Dictionary<string, double> EndPrices =
        new() { { "NULL_SPHERE", 100_000 }, { "SUMMONING_EYE", 800_000 }, { "ENDER_PEARL", 500 }, { "ENCHANTED_ENDER_PEARL", 90_000 } };

    [Test]
    public void ZealotBruiserHideoutWithNullSphere_IsT4Voidglooms_EvenWithPricierPearls()
    {
        NameAtPriced("Zealot Bruiser Hideout", new() { { "NULL_SPHERE", 4 }, { "ENDER_PEARL", 300 }, { "ENCHANTED_ENDER_PEARL", 20 } }, EndPrices)
            .Should().Be("T4 Voidglooms");
        NameAtPriced("Zealot Bruiser Hideout", new() { { "NULL_SPHERE", 4 }, { "SUMMONING_EYE", 1 } }, EndPrices).Should().Be("T4 Voidglooms");
    }

    [Test]
    public void ZealotBruiserHideoutWithoutNullSphere_StaysZealotsFd()
    {
        NameAtPriced("Zealot Bruiser Hideout", new() { { "ENDER_PEARL", 300 }, { "ENCHANTED_ENDER_PEARL", 20 }, { "SUMMONING_EYE", 1 } }, EndPrices)
            .Should().Be("Zealots (FD)");
    }

    [Test]
    public void DragonsNestSummoningEyeOnly_KeepsItsClassification()
        => NameAtPriced("Dragon's Nest", new() { { "SUMMONING_EYE", 2 } }, EndPrices).Should().Be(DragonsNestEyeOnlyBaseline);

    private const string DragonsNestEyeOnlyBaseline = "T4 Voidglooms";

    [Test]
    public void T4VoidgloomsPersonalView_DropsHideoutPeriodsWithoutNullSphere()
    {
        Period Make(string tag) => new()
        {
            Location = "Zealot Bruiser Hideout",
            ItemsCollected = new() { { tag, 3 } },
            StartTime = new DateTime(2026, 10, 3, 12, 0, 0),
            EndTime = new DateTime(2026, 10, 3, 12, 10, 0),
            PlayerUuid = "test"
        };
        var task = Registry.MethodTasks.First(t => t.GetDetectionSignature().MethodName == "T4 Voidglooms");
        var matched = task.FindMatchingPeriodsForAggregation(new TaskParams
        {
            TestTime = new DateTime(2026, 10, 3, 12, 10, 0),
            LocationProfit = new() { { "Zealot Bruiser Hideout", [Make("SUMMONING_EYE"), Make("NULL_SPHERE")] } }
        });
        matched.Should().ContainSingle().Which.ItemsCollected.Should().ContainKey("NULL_SPHERE");
    }
}
