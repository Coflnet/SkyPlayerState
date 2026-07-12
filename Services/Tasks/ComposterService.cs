using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

public class ComposterService
{
    public Dictionary<string, float> MatterTable = new Dictionary<string, float>()
    {
        { "CROPIE", 2500f },
        { "SQUASH", 10000f },
        { "FERMENTO", 20000f },
        { "CONDENSED_FERMENTO", 180000f },
        { "FLOWERING_BOUQUET", 6000f },
        { "FINE_FLOUR", 150f },
        { "WHEAT", 1f },
        { "ENCHANTED_BREAD", 60f },
        { "ENCHANTED_WHEAT", 160f },
        { "ENCHANTED_HAY_BALE", 25600f },
        { "SEEDS", 1f },
        { "ENCHANTED_SEEDS", 160f },
        { "BOX_OF_SEEDS", 25600f },
        { "CARROT_ITEM", 0.29f },
        { "ENCHANTED_CARROT", 46.4f },
        { "ENCHANTED_GOLDEN_CARROT", 5939.2f },
        { "POTATO_ITEM", 0.33f },
        { "POISONOUS_POTATO", 0.33f },
        { "ENCHANTED_POTATO", 52.8f },
        { "ENCHANTED_POISONOUS_POTATO", 52.8f },
        { "ENCHANTED_BAKED_POTATO", 8448f },
        { "PUMPKIN", 1f },
        { "ENCHANTED_PUMPKIN", 160f },
        { "POLISHED_PUMPKIN", 25600f },
        { "MELON", 0.2f },
        { "MELON_BLOCK", 1.8f },
        { "ENCHANTED_MELON", 32f },
        { "ENCHANTED_MELON_BLOCK", 5120f },
        { "RED_MUSHROOM", 1f },
        { "HUGE_MUSHROOM_1", 9f },
        { "ENCHANTED_RED_MUSHROOM", 160f },
        { "ENCHANTED_HUGE_MUSHROOM_1", 5184f },
        { "BROWN_MUSHROOM", 1f },
        { "HUGE_MUSHROOM_2", 9f },
        { "ENCHANTED_BROWN_MUSHROOM", 160f },
        { "ENCHANTED_HUGE_MUSHROOM_2", 5184f },
        { "INK_SACK:3", 0.4f },
        { "ENCHANTED_COCOA", 64f },
        { "CACTUS", 0.5f },
        { "INK_SACK:2", 0.5f },
        { "ENCHANTED_CACTUS_GREEN", 80f },
        { "ENCHANTED_CACTUS", 12800f },
        { "SUGAR_CANE", 0.5f },
        { "ENCHANTED_SUGAR", 80f },
        { "ENCHANTED_PAPER", 96f },
        { "ENCHANTED_SUGAR_CANE", 12800f },
        { "NETHER_STALK", 0.33f },
        { "ENCHANTED_NETHER_STALK", 52.8f },
        { "MUTANT_NETHER_STALK", 8448f }
    };

    public Dictionary<string, float> FuelTable = new Dictionary<string, float>()
    {
        { "BIOFUEL", 3000f },
        { "VOLTA", 10000f },
        { "OIL_BARREL", 10000f },
        { "SUNFLOWER_OIL", 20000f},
        { "CLAW_FOSSIL", 50000f },
        { "CLUBBED_FOSSIL", 50000f },
        { "FOOTPRINT_FOSSIL", 50000f },
        { "SPINE_FOSSIL", 50000f },
        { "TUSK_FOSSIL", 50000f },
        { "UGLY_FOSSIL", 50000f },
        { "WEBBED_FOSSIL", 50000f }
    };

    public (string cropMatter, string fuel, float profitPerHour) GetBestFlip(Dictionary<string, float> itemPrices, float compostPrice, Coflnet.Sky.PlayerState.Models.Composter composterState)
    {
        var bestFuel = FuelTable.Select(f => (f.Key, itemPrices.GetValueOrDefault(f.Key, 0) / f.Value)).OrderBy(f => f.Item2).FirstOrDefault();
        var bestCrop = MatterTable.Select(c => (c.Key, itemPrices.GetValueOrDefault(c.Key, 0) / c.Value)).OrderBy(c => c.Item2).FirstOrDefault();
        if (bestFuel.Item2 <= 0 || bestCrop.Item2 <= 0)
            return (null, null, 0);


        var increase = composterState.SpeedPercentIncrease;
        var extraDropChance = composterState.MultiDropChance;
        var costReduction = composterState.CostReductionPercent;
        var producedperHour = 6 * (1 - increase / 100f);

        var profitPerHour = (compostPrice - (bestCrop.Item2 * (1 + extraDropChance / 100f) + bestFuel.Item2 * (1 - costReduction / 100f))) * producedperHour;
        return (bestCrop.Key, bestFuel.Key, profitPerHour);
    }
}