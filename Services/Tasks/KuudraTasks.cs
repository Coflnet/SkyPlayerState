using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

public abstract class BaseKuudraTask : MethodTask
{
    protected override string Category => "Kuudra";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Kuudra";
    protected override string ActionUnit => "runs";
    protected override string WarpCommand => "/warp kuudra";
    /// <summary>Kuudra tier this task covers, 1 (Basic) to 5 (Infernal); 0 for tasks not tied to one tier.</summary>
    protected virtual int KuudraTier => 0;
    /// <summary>Reputation with either Crimson Isle faction needed to start a tier (Basic needs none).</summary>
    private static readonly int[] ReputationPerTier = [0, 1_000, 3_000, 7_000, 12_000];

    /// <summary>
    /// Source: https://hypixelskyblock.minecraft.wiki/w/Kuudra. Kuudra is on the Crimson Isle (Combat 22). Each tier
    /// opens when the previous one is cleared; Hot, Burning, Fiery and Infernal also need 1,000 / 3,000 / 7,000 /
    /// 12,000 reputation in either faction (the higher of the two counts).
    /// </summary>
    protected override List<TaskRequirement> Requirements
    {
        get
        {
            var requirements = new List<TaskRequirement> { TaskRequirement.Skill("combat", 22) };
            if (KuudraTier > 1)
            {
                requirements.Add(TaskRequirement.KuudraTier(KuudraTier - 1));
                requirements.Add(TaskRequirement.KuudraReputation(ReputationPerTier[KuudraTier - 1]));
            }
            return requirements;
        }
    }
    // Current reward shards have named SHARD_* tags; no generic ATTRIBUTE_SHARD price/rate is meaningful.
    // A Kuudra tier zone ("Kuudra's Hollow (T1)".."(T5)") is a dedicated instance - it can only ever
    // mean that one tier, so a couple of essence/attribute shard items is already unambiguous
    // evidence, unlike a shared open-world zone. See DungeonTasks.BaseDungeonTask for the same idea.
    protected override int MinLocationOnlyItems => 1;
    // the paid chest's loot; its shard is a named SHARD_* tag of any kind, declared by family
    protected override HashSet<string> RareDrops => DeclaredLoot.KuudraChest;
    protected override bool DeclaresDropFamily(string tag) => tag.StartsWith("SHARD_", StringComparison.Ordinal);
    /// <summary>
    /// Consumables a run burns (production: 104 h of T5 periods consumed 47k Toxic Arrow Poison, Twilight Arrow Poison,
    /// Magma Arrow Bundles and Kismet Feathers for paid chest rerolls) and the Kuudra keys. How much depends on the
    /// player's class and bow, so they have no formula rate; a positive count of one is a supply, not loot (a key gained
    /// is bought or fished, see KuudraRewardAttribution). <see cref="PseudoItems.KUUDRA_PAID_CHEST"/> counts the confirmed
    /// paid chest opens for <see cref="ScaledFormulaCost"/>, worth nothing itself.
    /// </summary>
    protected override HashSet<string> ObservedCostItems =>
    [
        "TOXIC_ARROW_POISON", "TWILIGHT_ARROW_POISON", "ARROW_BUNDLE_MAGMA", "KISMET_FEATHER", PseudoItems.KUUDRA_PAID_CHEST,
        .. KuudraKeyCost.KeyTags
    ];
    /// <summary>
    /// Kuudra Teeth one paid chest gives on average, 0 when not known: the paid chest's guaranteed teeth plus the expected
    /// teeth of its rolled slots. A tier that knows it declares the paid chest's guaranteed loot in its
    /// <see cref="FormulaDrops"/> and charges the key in the formula baseline.
    /// <para>
    /// T1-T4 stay at 0 although their teeth are known (2026-11-28 production, modal teeth of single armor piece claims and
    /// the Paid Chest menu samples, plus the wiki's rolled teeth: T1 1.0, T2 1.0, T3 2.3, T4 2.4): their paid chest's
    /// guaranteed Crimson Essence is not sourced (the wiki lists it for Infernal only), and without it the formula
    /// baseline would charge a key per run against nothing but today's essence rate (T1: 600 essence/h against 12 keys/h,
    /// about -1.5M/h at production prices).
    /// </para>
    /// </summary>
    protected virtual double TeethPerPaidChest => 0;
    private bool EstimatesPaidChests => TeethPerPaidChest > 0 && KuudraKeyCost.OfTier(KuudraTier) != null;
    /// <summary>
    /// The formula baseline's key, one per run. Observed data is charged per paid chest instead, see
    /// <see cref="ScaledFormulaCost"/>.
    /// </summary>
    protected override List<MethodDrop> FormulaCosts =>
        EstimatesPaidChests ? [new(KuudraKeyCost.OfTier(KuudraTier).Tag, ActionsPerHour)] : [];

    /// <summary>
    /// The keys of this tier's paid chests in <paramref name="itemCounts"/> (absolute counts over the window, or per hour
    /// rates with <paramref name="hours"/> = 1):
    /// <list type="bullet">
    /// <item>A chest whose key Hypixel bought on opening (<see cref="PseudoItems.KUUDRA_PAID_CHEST"/>, see
    /// DungeonRewardListener) booked its coins as <see cref="PseudoItems.DUNGEON_CHEST_COST"/>. While the caller values that
    /// cost (with <see cref="TaskRateOptions.FullValueRates"/>) only the chest's materials are charged here, otherwise also
    /// its booked coins.</item>
    /// <item>A key taken from the inventory (a negative key count) is charged in full: soulbound, it has no market price.</item>
    /// <item>The paid chests the teeth stand for (<see cref="TeethPerPaidChest"/>; teeth come with the claimed loot whether
    /// or not the open was confirmed) beyond those two are charged in full.</item>
    /// </list>
    /// </summary>
    public override double ScaledFormulaCost(Dictionary<string, double> itemCounts, double hours, Func<string, double> priceOf)
    {
        var key = KuudraKeyCost.OfTier(KuudraTier);
        if (key == null || hours <= 0 || itemCounts == null)
            return 0;
        var confirmed = Math.Max(0, -itemCounts.GetValueOrDefault(PseudoItems.KUUDRA_PAID_CHEST));
        var fromInventory = Math.Max(0, -itemCounts.GetValueOrDefault(key.Tag));
        var unconfirmed = Math.Max(0, Math.Min(EstimatedPaidChests(itemCounts), MaxPaidChests(hours)) - confirmed - fromInventory);
        var materials = KuudraKeyCost.MaterialValue(key, priceOf);
        var fullKeys = (fromInventory + unconfirmed) * (key.Coins + materials);
        return fullKeys + confirmed * materials + UnvaluedBookedCoins(itemCounts, key, confirmed, priceOf);
    }

    /// <summary>
    /// The most paid chests <paramref name="hours"/> of a player's time can hold: one per run at twice the formula's run
    /// rate. Teeth also reach the counts without a paid chest open (2026-11-28: 2,000 teeth over 4.5 h of T5 charged 625
    /// keys, -190 M/h), so their estimate never charges more chests than runs.
    /// </summary>
    private double MaxPaidChests(double hours) => 2 * ActionsPerHour * hours;

    private double EstimatedPaidChests(Dictionary<string, double> itemCounts)
        => EstimatesPaidChests ? Math.Max(0, itemCounts.GetValueOrDefault("KUUDRA_TEETH")) / TeethPerPaidChest : 0;

    /// <summary>
    /// The coins booked for the confirmed opens when the caller values the chest cost at 0 (rate mode off): at most the
    /// highest price a confirmed key may have cost, so a stray dungeon chest cost in the counts is not charged.
    /// </summary>
    private static double UnvaluedBookedCoins(Dictionary<string, double> itemCounts, KuudraKeyCost.KeyPrice key,
        double confirmed, Func<string, double> priceOf)
    {
        if (confirmed <= 0 || priceOf(PseudoItems.DUNGEON_CHEST_COST) > 0)
            return 0;
        var booked = Math.Max(0, -itemCounts.GetValueOrDefault(PseudoItems.DUNGEON_CHEST_COST));
        return Math.Min(booked, confirmed * key.MaxCoins);
    }
    /// <summary>The breakdowns price this tier's keys like the rate does, see <see cref="KuudraKeyCost"/>.</summary>
    protected override double CostPriceOf(string tag, Dictionary<string, float> prices)
        => KuudraKeyCost.WithKeyValues(t => prices.GetValueOrDefault(t, 0))(tag);
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "AURORA_CHESTPLATE", Reason = "Mage armor for Hyperion clears; use Terror for bow damage" },
        new() { ItemTag = "HYPERION", Reason = "Weapon" }
    ];
    protected override List<DropEffect> Effects => [
        new() { Name = "Crimson Essence multiplier", Description = "Higher tier drops more essence per run", EstimatedMultiplier = 1.0 },
        new() { Name = "Party size", Description = "Full party of 4 speeds up runs significantly", EstimatedMultiplier = 1.5 }
    ];
}

// Bare "Kuudra"/"Kuudra's Hollow" deliberately removed from every tier below: neither ever occurs
// on the real scoreboard (verified against production logs), and the bare island-level name matched
// EVERY tier's periods via SkyblockZones.Matches' island fallback - the same "every floor matches
// every dungeon task" bug as DungeonTasks.cs. See SkyblockZones.cs for the real per-tier zone
// strings ("Kuudra's Hollow (T1)" etc.) each tier now matches instead.
public class KuudraT1Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T1";
    protected override int KuudraTier => 1;
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T1)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 600)];
    protected override double ActionsPerHour => 12;
    protected override string HowTo => "Have Elle take you to Kuudra Basic (T1) from the Forgotten Skull on the Crimson Isle. Fish up supply crates with a lava rod, build and fuel the ballista, then fire at Kuudra. A matching Kuudra Key opens the paid chest; compare its cost with the loot.";
}
public class KuudraT2Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T2";
    protected override int KuudraTier => 2;
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T2)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 900)];
    protected override double ActionsPerHour => 10;
    protected override string HowTo => "Queue for Kuudra Hot (T2). Needs Basic cleared and 1,000 reputation; better gear than T1 is recommended, drops more Crimson Essence.";
}
public class KuudraT3Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T3";
    protected override int KuudraTier => 3;
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T3)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 1200)];
    protected override double ActionsPerHour => 8;
    protected override string HowTo => "Queue for Kuudra Burning (T3). Needs Hot cleared and 3,000 reputation. Coordinate supplies, construction and the stun/damage phases with your party.";
}
public class KuudraT4Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T4";
    protected override int KuudraTier => 4;
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T4)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_CRIMSON", 1500)];
    protected override double ActionsPerHour => 6;
    protected override string HowTo => "Queue for Kuudra Fiery (T4). Needs Burning cleared and 7,000 reputation. Coordinate supplies, construction and the stun/damage phases; bring gear appropriate to your role.";
}
public class KuudraT5Task : BaseKuudraTask
{
    protected override string MethodName => "Kuudra T5";
    protected override int KuudraTier => 5;
    protected override HashSet<string> Locations => ["Kuudra's Hollow (T5)"];
    /// <summary>
    /// Per run: 200 Crimson Essence from the free chest (wiki Kuudra loot table) plus the paid chest's guaranteed 2,000
    /// Crimson Essence, 3 Kuudra Teeth and a Kraken Shard (vexo KuudraProfitTracker INFERNAL_GUARANTEED_DROPS, Odin's
    /// tracker example shows the same "Crimson Essence x2000"), at 5 runs/h. The rolled loot (armor, books, shards) is
    /// left to the rare pool. Teeth first: they are the cost reference, see <see cref="TeethPerPaidChest"/>.
    /// </summary>
    protected override List<MethodDrop> FormulaDrops =>
        [new("KUUDRA_TEETH", TeethPerPaidChest * RunsPerHour), new("ESSENCE_CRIMSON", 2_200 * RunsPerHour), new("SHARD_KRAKEN", RunsPerHour)];
    /// <summary>
    /// 3 guaranteed plus the rolled slots' 25 (0.59%) and 100 (0.06%) teeth (vexo INFERNAL_SLOT tables): 3.2. Consistent
    /// with production (2026-11-27 audit, 104 h of T5): 2,900 teeth over 1,311 player runs (distinct player and server;
    /// party members share a server, the 817 distinct servers are no run count) are 2.2 per run, 2.9 per run for the 14
    /// players who claimed any loot, so 3.2 per chest stands for fewer chests than runs.
    /// </summary>
    protected override double TeethPerPaidChest => 3.2;
    private const double RunsPerHour = 5;
    protected override double ActionsPerHour => RunsPerHour;
    protected override string HowTo => "Queue for Kuudra Infernal (T5). Needs Fiery cleared and 12,000 reputation. Bring an experienced party of 4 and gear for the final Kuudra damage phase. Compare paid chest loot with the Infernal Kuudra Key cost before claiming.";
}

/// <summary>
/// Kuudra loot is claimed outside the instance (Croesus in the Dungeon Hub, Forgotten Skull) so the
/// chest rewards land in a different zone than the Kuudra tier zones. Most claims are re-attributed
/// to the tier zone they belong to (see KuudraRewardAttribution); this task only receives claims whose
/// run tier is unknown. Periods that were already resolved to a Catacombs floor by
/// DungeonRewardAttribution are not affected. A claim takes seconds, so each period counts as one run.
/// </summary>
public class KuudraChestClaimsTask : BaseKuudraTask
{
    // claims whose Kuudra tier is unknown are not a method a player can choose, and a bulk claim of several
    // runs counts only one run's 7.5 minutes (same reasoning as the hidden "Dungeon Chest Claims")
    protected override bool Hidden => true;
    protected override string MethodName => "Kuudra Chest Claims";
    protected override HashSet<string> Locations => ["Dungeon Hub", "Forgotten Skull"];
    protected override HashSet<string> DetectionItems =>
    [
        "KUUDRA_TEETH", "KUUDRA_TENTACLE", "KUUDRA_MANDIBLE",
        .. new[] { "CRIMSON", "AURORA", "TERROR", "FERVOR", "HOLLOW" }
            .SelectMany(set => new[] { "HELMET", "CHESTPLATE", "LEGGINGS", "BOOTS" }.Select(piece => $"{set}_{piece}"))
    ];
    protected override List<MethodDrop> FormulaDrops => [new("KUUDRA_TEETH", 100)];
    protected override double ActionsPerHour => 8;
    // one run per claim: the claim period counts as the time one run takes (7.5 minutes)
    protected override TimeSpan MinimumPeriodDuration => TimeSpan.FromHours(1 / ActionsPerHour);
    protected override string HowTo => "Claim your Kuudra chest rewards after a run. Loot whose Kuudra tier is unknown shows up here; claims for a known tier are counted at that tier.";
    public override string Description =>
        "Claiming Kuudra chest loot (teeth, tentacles, mandibles, Kuudra armor) outside the instance when the run's tier is unknown.";
}
