using System;
using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class KuudraRewardAttributionTests
{
    private static readonly DateTime Base = new(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
    private const string T5 = "Kuudra's Hollow (T5)";
    private static readonly Dictionary<string, int> Loot = new() { { "KUUDRA_TENTACLE", 10 }, { "ENCHANTED_BOOK", 5 } };

    [TestCase("Dungeon Hub")]
    [TestCase("Forgotten Skull")]
    public void ClaimLoot_WithinWindow_ResolvesToLastTier(string location)
    {
        KuudraRewardAttribution.ResolveLocation(location, Loot, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    [Test]
    public void ClaimLoot_AfterWindow_NotAttributed()
    {
        var start = Base + KuudraRewardAttribution.MaxClaimDelay + TimeSpan.FromMinutes(1);
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, T5, Base, start).Should().Be("Dungeon Hub");
    }

    [Test]
    public void NoKuudraLoot_NotAttributed()
    {
        var items = new Dictionary<string, int> { { "ESSENCE_WITHER", 40 }, { "KUUDRA_TENTACLE", -1 } };
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", items, T5, Base, Base.AddMinutes(3)).Should().Be("Dungeon Hub");
    }

    [Test]
    public void NoKnownTier_NotAttributed()
    {
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, null, default, Base).Should().Be("Dungeon Hub");
    }

    [Test]
    public void KuudraLoot_BeatsCatacombsFloorResolution()
    {
        // the listener applies the Dungeon resolution first, then the Kuudra one over the raw location
        var floor = DungeonRewardAttribution.ResolveLocation("Dungeon Hub", "The Catacombs (F4)", Base, Base.AddMinutes(3));
        floor.Should().Be("The Catacombs (F4)");
        KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, T5, Base, Base.AddMinutes(3)).Should().Be(T5);
    }

    [TestCase("KUUDRA_TIER_KEY", 1)]
    [TestCase("KUUDRA_HOT_TIER_KEY", 2)]
    [TestCase("KUUDRA_BURNING_TIER_KEY", 3)]
    [TestCase("KUUDRA_FIERY_TIER_KEY", 4)]
    [TestCase("KUUDRA_INFERNAL_TIER_KEY", 5)]
    public void KeyPurchase_ResolvesToMatchingTier(string key, int tier)
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_MYCELIUM", -3840 }, { "NETHER_STAR", -96 }, { key, 4 } };
        KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base)
            .Should().Be($"Kuudra's Hollow (T{tier})");
    }

    [Test]
    public void KeyPurchase_HighestTierWins()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_HOT_TIER_KEY", 1 }, { "KUUDRA_FIERY_TIER_KEY", 2 }, { "KUUDRA_TIER_KEY", 3 } };
        KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base).Should().Be("Kuudra's Hollow (T4)");
    }

    [Test]
    public void KeyConsumed_NotAttributed()
    {
        var items = new Dictionary<string, int> { { "KUUDRA_INFERNAL_TIER_KEY", -1 } };
        KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base).Should().Be("Scarleton");
    }

    [Test]
    public void KeyPurchaseWithMaterialsOnly_ClassifiesToKuudraTierNotMycelium()
    {
        var items = new Dictionary<string, int> { { "ENCHANTED_MYCELIUM", -3840 }, { "NETHER_STAR", -96 }, { "KUUDRA_INFERNAL_TIER_KEY", 4 } };
        var location = KuudraRewardAttribution.ResolveLocation("Scarleton", items, null, default, Base);
        new TaskClassifier(new TaskRegistry()).Classify(location, items, 10)!.TaskName.Should().Be("Kuudra T5");
    }

    [Test]
    public void ClaimLootOnly_ResolvedToTier_ClassifiesToKuudraTier()
    {
        var location = KuudraRewardAttribution.ResolveLocation("Dungeon Hub", Loot, "Kuudra's Hollow (T4)", Base, Base.AddMinutes(3));
        new TaskClassifier(new TaskRegistry()).Classify(location, Loot, 10)!.TaskName.Should().Be("Kuudra T4");
    }
}
