using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>Lore stats and base/effective stats on production "Stats &amp; Equipment" and "Loadouts" samples (trimmed lore).</summary>
public class GearBaseStatsTests
{
    private const string EnderArmor = "§7Health: §c+40\n§7Defense: §a+70\n§7Strength: §c+16 §9(+8)\n§7Crit Chance: §9+10% §9(+5%)\n§7Crit Damage: §9+28% §9(+14%)\n\nAll stats on this armor piece are\n§7multiplied by §52x §7while on the End\nIsland!\n\n";

    private static Item Piece(string tag, string lore, string footer) => new() { Tag = tag, ItemName = tag, Description = lore + footer };

    /// <summary>Stats_Equipment.json, first sample (2026-11-07T10:30:05Z): slot 2 is the held Void Sword.</summary>
    private static List<Item> StatsMenuSample()
    {
        var items = Enumerable.Range(0, 90).Select(_ => GearTestItems.Empty()).ToList();
        items[2] = Piece("VOID_SWORD", "§7Damage: §c+125\n§7Strength: §c+120\n§7Crit Chance: §9+17% §9(+17%)\n§7Crit Damage: §9+55% §9(+55%)\n\n§7Gain §c+20 Strength §7per piece of\n§5Ender Armor §7worn.\n\n", "EPIC SWORD");
        items[10] = Piece("ENDER_NECKLACE", "§7Health: §c+30\n§7Defense: §a+20\n\n", "EPIC NECKLACE");
        items[11] = Piece("END_HELMET", EnderArmor, "EPIC HELMET");
        items[20] = Piece("END_CHESTPLATE", EnderArmor, "EPIC CHESTPLATE");
        items[29] = Piece("END_LEGGINGS", EnderArmor, "EPIC LEGGINGS");
        items[38] = Piece("END_BOOTS", EnderArmor, "EPIC BOOTS");
        items[19] = new Item { Tag = null!, ItemName = "Empty Equipment Slot", Description = " §8> Cloak" };
        items[47] = new Item
        {
            Tag = "PET_GRANDMA_WOLF", ItemName = "§7[Lvl 49] §fGrandma Wolf",
            Description = "Combat Pet\n\n§7Health: §c+49\n§7Strength: §c+12.25\n\nKill Combo\n  §b+1% §b Magic Find\n\nCOMMON PET"
        };
        items[14] = new Item { ItemName = "Combat Stats", Description = "Stats that influence how much\n\n §c Health §f456.39\n §c Strength §f212.71\n §9 Crit Chance §f94.99%\n §9 Crit Damage §f219.47%\n\nClick for details!" };
        items[16] = new Item { ItemName = "Farming Stats", Description = "Stats that influence how many drops\n\n §6 Farming Fortune §f44\n\nClick for details!" };
        items[24] = new Item { ItemName = "Fishing Stats", Description = "Stats that influence what you catch\n\n §3 Sea Creature Chance §f20%\n\nClick for details!" };
        return items;
    }

    [Test]
    public void LoreStats_FirstValueIsTheTotal_PercentAndDecimals()
    {
        var names = GearModifierTable.Capture.Stats;

        LoreStats.Parse("§7Strength: §c+300 §e(+30) §6[+5] §9(+165)\n§7Gemstones: §6[§b§6]", names).Should().BeEquivalentTo(new Dictionary<string, double> { ["Strength"] = 300 });
        LoreStats.Parse("§7Gear Score: §d574 §8(3010)\n§7Strength: §c+51.5 §9(+35) §8(+280)\n§7Crit Chance: §9+15% §9(+15%) §8(+23.25%)", names)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["Strength"] = 51.5, ["Crit Chance"] = 15 });
        LoreStats.Parse("§7Bonus Pest Chance: §2+27.75% §9(+2.75%)\n§7Farming Fortune: §6+65 §9(+10) §d(+5)\n§7Grants §6+10 Farming Fortune §7and", names)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["Bonus Pest Chance"] = 27.75, ["Farming Fortune"] = 65 });
        LoreStats.Parse("§8Breaking Power 10\n\n§7Mining Speed: §a+1,800", names)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["Breaking Power"] = 10, ["Mining Speed"] = 1800 });
    }

    [Test]
    public void PetLoreStats_AreParsed()
    {
        var pet = GearNormalizer.NormalizePet(StatsMenuSample()[47])!;

        pet.Stats.Should().BeEquivalentTo(new Dictionary<string, double> { ["Strength"] = 12.25 });
    }

    [Test]
    public async Task StatsMenu_BaseIsTheTotalMinusTheShownGear()
    {
        var state = new StateObject();
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Chest = new ChestView { Name = "Stats & Equipment", Items = StatsMenuSample() } }
        };

        await new GearSnapshotListener().Process(args);

        var reading = state.ExtractedInfo.BaseStats!;
        var baseStats = reading.Stats;
        // 212.71 - 120 (held Void Sword, slot 2) - 4 x 16 (armor) - 12.25 (pet)
        baseStats["Strength"].Should().BeApproximately(16.46, 1e-9);
        baseStats["Crit Chance"].Should().BeApproximately(94.99 - 17 - 40, 1e-9);
        baseStats["Farming Fortune"].Should().Be(44);
        baseStats["Sea Creature Chance"].Should().Be(20);
        baseStats.Should().NotContainKey("Damage", "the menu has no Damage total, nothing is subtracted from it");
        baseStats.Should().NotContainKey("Health");
        reading.HeldStats["Damage"].Should().Be(125);
        reading.Pet!.Type.Should().Be("GRANDMA_WOLF", "without petInfo (as in the sample) the type comes from the PET_ tag");
        reading.Pet.Tier.Should().Be("COMMON");
        state.ExtractedInfo.CurrentGear!.Slots[GearSnapshot.Helmet].Stats["Strength"].Should().Be(16);
    }

    [Test]
    public void Derive_ClampsAtZero()
    {
        // Ender armor counts twice on the End island: the lore sum can exceed the total
        var result = GearBaseStats.Derive(new Dictionary<string, double> { ["Strength"] = 50 },
            [new Dictionary<string, double> { ["Strength"] = 64, ["Damage"] = 125 }]);

        result.Should().BeEquivalentTo(new Dictionary<string, double> { ["Strength"] = 0 });
    }

    [TestCase("The Catacombs (F7)", null)]
    [TestCase("Kuudra's Hollow (T5)", null)]
    [TestCase("Dungeon Hub", "Kuudra")]
    public async Task StatsMenu_InsideADungeonOrKuudra_DerivesNoBase(string location, string? island)
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentLocation = location;
        state.ExtractedInfo.CurrentIsland = island;

        await new GearSnapshotListener().Process(new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Chest = new ChestView { Name = "Stats & Equipment", Items = StatsMenuSample() } }
        });

        state.ExtractedInfo.BaseStats.Should().BeNull();
        state.ExtractedInfo.CurrentGear!.Slots.Should().ContainKey(GearSnapshot.Helmet, "the gear is still read");
    }

    [Test]
    public async Task StatsMenu_RecordsTheProfile()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        await new GearSnapshotListener().Process(new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Chest = new ChestView { Name = "Stats & Equipment", Items = StatsMenuSample() } }
        });

        state.ExtractedInfo.BaseStats!.ProfileId.Should().Be(state.ExtractedInfo.CurrentProfileId);
    }

    [Test]
    public async Task WornMenu_WithoutAPet_ClearsThePet()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentGear = new GearSnapshot { Pet = new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY" } };
        var items = StatsMenuSample();
        items[47] = new Item { Tag = null!, ItemName = "Empty Pet Slot" };

        await new GearSnapshotListener().Process(new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Chest = new ChestView { Name = "Stats & Equipment", Items = items } }
        });

        state.ExtractedInfo.CurrentGear!.Pet.Should().BeNull();
    }

    [Test]
    public void Effective_SameGear_GivesTheMenuTotal_AndALoadoutSwitchUsesTheNewGear()
    {
        var items = StatsMenuSample();
        var baseStats = GearBaseStats.Derive(PlayerStatsListener.ParseStats(items),
            new[] { 2, 10, 11, 20, 29, 38 }.Select(i => GearNormalizer.Normalize(items[i])!.Stats).Append(GearNormalizer.NormalizePet(items[47])!.Stats));
        var gear = new GearSnapshot { Pet = GearNormalizer.NormalizePet(items[47]) };
        foreach (var (slot, index) in new[] { (GearSnapshot.Helmet, 11), (GearSnapshot.Chestplate, 20), (GearSnapshot.Leggings, 29), (GearSnapshot.Boots, 38), (GearSnapshot.Tool, 2) })
            gear.Slots[slot] = GearNormalizer.Normalize(items[index])!;

        var reading = new BaseStatsReading { Stats = baseStats };
        GearBaseStats.Effective(reading, gear, GearModifierTable.Dungeon, "F7", out _)["Strength"].Should().BeApproximately(212.71, 1e-9);

        // switched to a loadout whose helmet has more Strength (3_Loadouts.json, Strong Dragon Helmet: Strength +25)
        gear.Slots[GearSnapshot.Helmet] = GearNormalizer.Normalize(Piece("STRONG_DRAGON_HELMET", "§7Health: §c+160 §9(+15)\n§7Strength: §c+25\n\n", "LEGENDARY HELMET"))!;
        var switched = GearBaseStats.Effective(reading, gear, GearModifierTable.Dungeon, "F7", out _);
        switched["Strength"].Should().BeApproximately(212.71 + 9, 1e-9);
        switched.Should().NotContainKey("Farming Fortune", "farming stats are not dungeon stats");
    }
}
