using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Base for Diana tasks — only accessible when the current mayor is Diana
/// </summary>
public abstract class BaseDianaTask : MethodTask
{
    protected override string Category => "Event";
    protected override string CheckAccessibility(TaskParams parameters)
    {
        if (parameters.CurrentMayor != null && parameters.CurrentMayor != "diana")
            return $"Only available when Diana is mayor (current: {parameters.CurrentMayor}).";
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
        "GRIFFIN_FEATHER", "MINOS_RELIC", "DAEDALUS_STICK",
        // treasure-burrow loot (ENCHANTED_GOLD alone makes up most dug-burrow periods)
        "ENCHANTED_GOLD", "MYTHOS_FRAGMENT", "CROCHET_TIGER_PLUSHIE", "WASHED_UP_SOUVENIR", "CRETAN_URN",
        "ANTIQUE_REMEDIES", "DWARF_TURTLE_SHELMET",
        // rare drops that showed up alone in otherwise unclassified Hub periods (production 2026-10-01)
        "BRAIDED_GRIFFIN_FEATHER", "SHIMMERING_WOOL", "MANTI_CORE", "FATEFUL_STINGER", "BRAIN_FOOD", "CROWN_OF_GREED",
        // Ancient Claw is the bulk drop from every burrow mob and the main coin source; MINOS_CHAMPION/INQUISITOR are mob names, not items.
        "ANCIENT_CLAW", "ENCHANTED_ANCIENT_CLAW", "HILT_OF_REVELATIONS",
        // burrow mob shards (Harpy/Sphinx: 31% of the Harpy periods at Ruins/Graveyard/Mountain were unclassified)
        "SHARD_MINOS_HUNTER", "SHARD_CRETAN_BULL", "SHARD_HARPY", "SHARD_MINOTAUR", "SHARD_SPHINX", "SHARD_KING_MINOS"];
    protected override List<MethodDrop> FormulaDrops => [
        new("ANCIENT_CLAW", 3000), new("GRIFFIN_FEATHER", 40), new("ENCHANTED_GOLD", 40), new("DAEDALUS_STICK", 3),
        new("MINOS_RELIC", 0.02), new("SHARD_KING_MINOS", 1)];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Mythological_Ritual";
    protected override string HowTo =>
        "During the Mythological Ritual mayor event, use the spade to locate a burrow in the Hub, dig it and follow the chain "
        + "(Ancestral Spade 4 burrows per chain, Archaic 6, Deific 8). Most burrows spawn a Mythological Creature to kill "
        + "(Ancient Claws, shards, Daedalus Stick, Chimera), the rest give treasure (Griffin Feathers, coins).";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "ANCESTRAL_SPADE", Reason = "Required to dig Diana burrows (the Archaic and Deific Spade make longer chains)" },
        new() { ItemTag = "PET_GRIFFIN", Reason = "Griffin pet reveals burrow locations via particles" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Griffin Pet Rarity", Description = "Higher rarity Griffin reveals burrows further away", EstimatedMultiplier = 1.3 },
        new() { Name = "Magic Find", Description = "Increases rare mob spawn and drop chance, and the chance of Chimera book and Daedalus Stick", EstimatedMultiplier = 1.3 },
        new() { Name = "Ferocity", Description = "More hits per attack for faster kills", EstimatedMultiplier = 1.15 }
    ];
}
