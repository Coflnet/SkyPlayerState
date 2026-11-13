using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Base for Diana tasks — only accessible when the Mythological Ritual is active
/// </summary>
public abstract class BaseDianaTask : MethodTask
{
    protected override string Category => "Event";
    protected override string CheckAccessibility(TaskParams parameters)
    {
        if (parameters.CurrentMayor != null && parameters.CurrentMayor != "diana" && parameters.CurrentMayor != "jerry")
            return $"Requires Diana's Mythological Ritual perk, including Jerry's Diana rotation (current: {parameters.CurrentMayor}).";
        return base.CheckAccessibility(parameters);
    }
}

/// <summary>
/// The Mythological Ritual: dig burrow chains with the spade. 75% of burrows spawn a Mythological Creature
/// (Ancient Claws, shards, Daedalus Sticks), 25% give treasure (Griffin Feathers, coins) - one activity, so
/// one task (it used to be split into "Diana" and "Diana (Hunting)" depending on whether claws or gold
/// were worth more in the period).
/// </summary>
public class DianaTask : BaseDianaTask
{
    protected override string MethodName => "Diana";
    protected override HashSet<string> Locations => ["Hub", "Wilderness", "Forest", "Mountain", "Ruins", "Graveyard", "Farm", "Village"];
    protected override HashSet<string> DetectionItems => [
        "GRIFFIN_FEATHER", "MINOS_RELIC", "DAEDALUS_STICK", "ENCHANTMENT_ULTIMATE_CHIMERA_1",
        // treasure-burrow loot (ENCHANTED_GOLD is not here: every gold farm and minion period holds it, see EvidenceItems)
        "MYTHOS_FRAGMENT", "CROCHET_TIGER_PLUSHIE", "WASHED_UP_SOUVENIR", "CRETAN_URN",
        "ANTIQUE_REMEDIES", "DWARF_TURTLE_SHELMET",
        // rare drops that showed up alone in otherwise unclassified Hub periods (production 2026-10-01)
        "BRAIDED_GRIFFIN_FEATHER", "SHIMMERING_WOOL", "MANTI_CORE", "FATEFUL_STINGER", "BRAIN_FOOD", "CROWN_OF_GREED",
        // Ancient Claw is the bulk drop from every burrow mob and the main coin source; MINOS_CHAMPION/INQUISITOR are mob names, not items.
        "ANCIENT_CLAW", "ENCHANTED_ANCIENT_CLAW", "HILT_OF_REVELATIONS",
        // burrow mob shards (Harpy/Sphinx: 31% of the Harpy periods at Ruins/Graveyard/Mountain were unclassified)
        "SHARD_MINOS_HUNTER", "SHARD_CRETAN_BULL", "SHARD_HARPY", "SHARD_MINOTAUR", "SHARD_SPHINX", "SHARD_KING_MINOS"];
    // Gold next to a Diana-only item is still the treasure loot of the dig, but alone it is any gold activity (production
    // 2026-11: 29.8 h of "Diana" with 8 Ancient Claws/h against a 2,800/h seed, mostly gold and hard stone).
    protected override HashSet<string> EvidenceItems => ["ENCHANTED_GOLD"];
    // The Bazaar Alley is a shop street in the Hub, nobody digs burrows there (best zone 5.3M/h from 6 players)
    protected override HashSet<string> ExcludedZones => ["Bazaar Alley"];
    // Observed per hour over 603 session hours of the 2026-10-21..24 event (300 players, ~26M/h); the Chimera book is
    // about half of the value.
    protected override List<MethodDrop> FormulaDrops => [
        new("ANCIENT_CLAW", 2800), new("ENCHANTED_ANCIENT_CLAW", 33), new("GRIFFIN_FEATHER", 50), new("ENCHANTED_GOLD", 2100),
        new("HILT_OF_REVELATIONS", 5.4), new("ENCHANTMENT_ULTIMATE_CHIMERA_1", 0.58), new("MINOS_RELIC", 0.05),
        new("CROCHET_TIGER_PLUSHIE", 1), new("WASHED_UP_SOUVENIR", 1.1), new("DWARF_TURTLE_SHELMET", 0.95),
        new("ANTIQUE_REMEDIES", 0.88), new("CRETAN_URN", 1), new("DAEDALUS_STICK", 0.3), new("SHARD_KING_MINOS", 1)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Mythological_Ritual";
    protected override string HowTo =>
        "During the Mythological Ritual mayor event, use the spade to locate a burrow in the Hub, dig it and follow the chain "
        + "(Ancestral Spade 4 burrows per chain, Archaic 6, Deific 8). Most burrows spawn a Mythological Creature to kill "
        + "(Ancient Claws, shards, Daedalus Stick, Chimera), the rest give treasure (Griffin Feathers, coins).";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "ANCESTRAL_SPADE", Reason = "Required to dig Diana burrows (the Archaic and Deific Spade make longer chains)" },
        new() { ItemTag = "PET_GRIFFIN", Reason = "Equip a Griffin to summon Mythological creatures; higher rarity unlocks more creature types" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Griffin Pet Rarity", Description = "Higher rarity Griffin unlocks more Mythological creature types and scales their stats", EstimatedMultiplier = 1.3 },
        new() { Name = "Magic Find", Description = "Increases eligible rare creature drop chances, including Chimera and Daedalus Stick", EstimatedMultiplier = 1.3 },
        new() { Name = "Ferocity", Description = "More hits per attack for faster kills", EstimatedMultiplier = 1.15 }
    ];
}
