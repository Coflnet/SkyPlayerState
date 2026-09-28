using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression tests for the 2026-09-28 fishing evidence fix (section A of the production log
/// analysis): location-only fishing tasks used to act as catch-alls for whole islands, absorbing
/// Hub purchases, slayer drops, farming, hard-stone/powder mining and wolf slayer that happened to
/// occur in the same zones. Every non-dedicated fishing task now requires <see cref="FishingEvidence"/>
/// (or WORM_MEMBRANE for the Water Worm tasks, or the Lava set for Crimson fishing).
/// </summary>
public class FishingTasksTests
{
    private static readonly TaskRegistry Registry = new();
    private static readonly TaskClassifier Classifier = new(Registry);

    private static void ClassifiesToNoFishingTask(string location, Dictionary<string, int> items)
    {
        var result = Classifier.Classify(location, items, 10);
        (result == null || !result.TaskName.Contains("Fishing"))
            .Should().BeTrue($"expected no fishing task, got {result?.TaskName ?? "null"}");
    }

    // ── Negative cases: real production periods that used to be swallowed by location-only fishing tasks ──

    [Test]
    public void HubAuctionHouseSpringBootsPurchase_DoesNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Auction House", new() { { "SPRING_BOOTS", 6 } });

    [Test]
    public void BazaarAlleyMutantNetherStalkPurchase_DoesNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Bazaar Alley", new() { { "MUTANT_NETHER_STALK", 2539 } });

    [Test]
    public void CryptsZombieSlayerDrops_DoNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Crypts", new() { { "ROTTEN_FLESH", 43 }, { "REVENANT_FLESH", 126 }, { "GOLD_INGOT", 89 } });

    [Test]
    public void SoulCaveWolfSlayerDrops_DoNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Soul Cave", new() { { "WOLF_TOOTH", 257 }, { "BONE", 36 }, { "LOG", 11 } });

    [Test]
    public void MinesOfDivanHardStoneMining_DoesNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Mines of Divan", new() { { "HARD_STONE", 263 }, { "ENCHANTED_HARD_STONE", 5 }, { "ENCHANTED_GOLD", 1493 } });

    [Test]
    public void CourtyardMageOutlawDrops_DoNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Courtyard", new() { { "GLOWSTONE_DUST", 30 }, { "SPELL_POWDER", 1 }, { "COAL", 30 } });

    [Test]
    public void BazaarAlleyShardPurchase_DoesNotClassifyAsFishing()
        => ClassifiesToNoFishingTask("Bazaar Alley", new() { { "SHARD_MINER_ZOMBIE", 81 }, { "SHARD_COD", 5 } });

    // ── Positive cases: real fishing must still classify correctly ──

    [Test]
    public void VillageWithRawFish_ClassifiesToWaterFishing()
    {
        var items = new Dictionary<string, int> { { "RAW_FISH", 40 }, { "RAW_FISH:1", 12 }, { "CLAY_BALL", 9 } };
        var result = Classifier.Classify("Village", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Water Fishing");
    }

    [Test]
    public void StrongholdWithMagmaFishAndTrophyFish_ClassifiesToCrimsonFishing()
    {
        var items = new Dictionary<string, int> { { "MAGMA_FISH", 6 }, { "BLOBFISH_BRONZE", 6 }, { "LAVA_HORSE_BRONZE", 2 }, { "MOOGMA_PELT", 1 } };
        var result = Classifier.Classify("Stronghold", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Crimson Fishing");
    }

    [Test]
    public void GoblinHoldoutWithWormMembrane_ClassifiesToWaterWormTask()
    {
        var items = new Dictionary<string, int> { { "WORM_MEMBRANE", 2 }, { "ROUGH_AMBER_GEM", 30 }, { "RAW_FISH", 5 } };
        var result = Classifier.Classify("Goblin Holdout", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Water Worm Fishing");
    }

    [Test]
    public void GoblinHoldoutHardStoneMining_DoesNotClassifyAsWaterWormFishing()
    {
        // Water Worm Fishing used to be island-wide ("Crystal Hollows") and swallowed hard-stone/
        // powder mining periods - it is now restricted to the Goblin Holdout zone AND requires
        // WORM_MEMBRANE or Water evidence, neither of which a pure mining period has.
        var items = new Dictionary<string, int> { { "HARD_STONE", 300 }, { "ENCHANTED_GOLD", 40 } };
        var result = Classifier.Classify("Goblin Holdout", items, 10);
        (result == null || result.TaskName != "Water Worm Fishing").Should().BeTrue();
    }

    [Test]
    public void ThePark_WaterFishingWinsTieOverSpookyFishing()
    {
        // Both require the same Water evidence and share "The Park" - Spooky Fishing has a lower
        // Priority so plain Water Fishing wins whenever the classifier cannot tell whether the
        // Spooky Festival is actually running.
        var items = new Dictionary<string, int> { { "RAW_FISH", 40 } };
        var result = Classifier.Classify("The Park", items, 10);
        result.Should().NotBeNull();
        result!.TaskName.Should().Be("Water Fishing");
    }

    [Test]
    public void CrimsonFishing_LavaEvidenceCoversGeneratedTrophyFishTags()
    {
        // Spot check a couple of the 72 generated SPECIES_TIER tags to guard the generation logic itself.
        FishingEvidence.Lava.Should().Contain("VOLCANIC_STONEFISH_DIAMOND");
        FishingEvidence.Lava.Should().Contain("SLUGFISH_SILVER");
        FishingEvidence.Lava.Should().HaveCount(10 + 18 * 4, "10 base lava drops + 18 species * 4 tiers of Trophy Fish");
    }
}
