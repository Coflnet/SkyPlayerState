using Coflnet.Sky.Commands.MC;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Passive task: place a hunting trap in the area whose shard pays the most per hour. A Small Huntrap needs 8-12 hours
/// for a common shard up to 16-24 hours for a legendary one (wiki), see <see cref="CaptureHours"/>.
/// </summary>
public class HuntingTrapTask : MethodTask
{
    public enum ShardRarity { Common, Uncommon, Rare, Epic, Legendary }

    /// <summary>
    /// Shard types mapped to the spots the wiki lists for "Place Hunting Traps in ...". A trap catches one of the shards
    /// available at its spot, not necessarily the priciest one.
    /// </summary>
    private static readonly List<(string ShardTag, ShardRarity Rarity, string[] Locations)> ShardLocations =
    [
        ("SHARD_HELLWISP", ShardRarity.Legendary, ["Matriarch's Lair"]),
        ("SHARD_XYZ", ShardRarity.Epic, ["Mystic Marsh"]),
        ("SHARD_INVISIBUG", ShardRarity.Rare, ["Moonglade Marsh"]),
        ("SHARD_GHOST", ShardRarity.Epic, ["The Mist"]),
        ("SHARD_CINDER_BAT", ShardRarity.Legendary, ["Smoldering Tomb"]),
        ("SHARD_DROWNED", ShardRarity.Rare, ["Drowned Reliquary"]),
        // the valuable spots of the wiki's "Place Hunting Traps in ..." entries (underwater where noted there)
        ("SHARD_BAYOU_SLUDGE", ShardRarity.Uncommon, ["Backwater Bayou"]),
        ("SHARD_TORRID", ShardRarity.Rare, ["Spring Depths"]),
        ("SHARD_BAMBULEAF", ShardRarity.Uncommon, ["Tranquility Sanctum"]),
        ("SHARD_SNOWFIN", ShardRarity.Rare, ["Jerry's Workshop"]),
        ("SHARD_GOLDOLOT", ShardRarity.Rare, ["Spring Depths", "Spring Shallows", "Torrhus Springs"]),
        ("SHARD_ABYSSAL_LANTERN", ShardRarity.Rare, ["Dwarven Mines"]),
        ("SHARD_SPRAWL", ShardRarity.Uncommon, ["Spring Shallows"]),
        ("SHARD_SILENTDEPTH", ShardRarity.Rare, ["Crystal Hollows"]),
        ("SHARD_TEWTIL", ShardRarity.Uncommon, ["Lotus Atoll"]),
        ("SHARD_GIANT_ISOPOD", ShardRarity.Epic, ["Spring Depths"]),
        ("SHARD_GIANT_WATER_BUG", ShardRarity.Rare, ["Hotspot Haven"]),
        ("SHARD_INFERNO_KOI", ShardRarity.Rare, ["Crimson Isle"]),
        ("SHARD_THYST", ShardRarity.Rare, ["Jungle"]),
        ("SHARD_ANT", ShardRarity.Common, ["Ant's Cave"]),
        ("SHARD_QUEEN_ANT", ShardRarity.Uncommon, ["Ant's Cave"]),
    ];

    /// <summary>Midpoint of the wiki capture time of a Small Huntrap: common 8-12 h, uncommon 10-15 h, rare 12-18 h, epic 14-21 h, legendary 16-24 h.</summary>
    public static double CaptureHours(ShardRarity rarity) => rarity switch
    {
        ShardRarity.Common => 10,
        ShardRarity.Uncommon => 12.5,
        ShardRarity.Rare => 15,
        ShardRarity.Epic => 17.5,
        _ => 20
    };

    /// <summary>Every shard of the task with the spots a trap catches it at (for tests).</summary>
    public static IReadOnlyList<(string ShardTag, string[] Locations)> Spots => ShardLocations.Select(s => (s.ShardTag, s.Locations)).ToList();

    /// <summary>The capture time of <paramref name="shardTag"/>, 0 for an unknown shard.</summary>
    public static double CaptureHoursOf(string shardTag)
    {
        var shard = ShardLocations.FirstOrDefault(s => s.ShardTag == shardTag);
        return shard.ShardTag == null ? 0 : CaptureHours(shard.Rarity);
    }

    /// <summary>The spots a trap may be placed at for <paramref name="shardTag"/>, null for an unknown shard.</summary>
    public static string[] LocationsOf(string shardTag) => ShardLocations.FirstOrDefault(s => s.ShardTag == shardTag).Locations;

    protected override string MethodName => "Hunting Trap";
    protected override string Category => "Hunting";
    // No single Where - it dynamically picks whichever zone currently has the priciest shard (see
    // Execute below), so link to the Huntraps mechanic instead of one specific zone.
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Huntraps";
    protected override TaskType TaskType => TaskType.Passive;
    protected override string ActionUnit => "traps";
    // No fixed ActionsPerHour: the capture time depends on the rarity of the shard Execute picks, and the breakdown
    // carries that shard's rate (a fixed Rare time disagreed with it whenever another rarity paid the most).
    // Small Huntrap item ID verified against the Hypixel items API.
    protected override List<RequiredItem> RequiredItems =>
        [new() { ItemTag = "RETIA_BASICA", Reason = "Place a Small Huntrap or better" }];

    public override Task<TaskResult> Execute(TaskParams parameters)
    {
        var prices = parameters.GetPrices();
        var bestShard = ShardLocations
            .Select(s => (s.ShardTag, s.Locations, Price: prices.GetValueOrDefault(s.ShardTag, 0), Hours: CaptureHours(s.Rarity)))
            .Where(s => s.Price > 0)
            .OrderByDescending(s => s.Price / s.Hours)
            .FirstOrDefault();

        if (bestShard.ShardTag == null)
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Type = TaskType.Passive, MostlyPassive = true,
                Message = "Hunting Trap - no shard prices available.",
                Name = MethodName,
                Breakdown = NewGuidanceBreakdown("Hunting", "formula")
            });

        var fmt = parameters.Formatter;
        var profitPerHour = bestShard.Price / bestShard.Hours;
        var shardName = parameters.Names.GetValueOrDefault(bestShard.ShardTag, bestShard.ShardTag);
        var location = bestShard.Locations[0];

        var breakdown = new MethodBreakdown
        {
            HowTo = HowTo,
            RequiredItems = RequiredItems,
            Drops = [new DropInfo
            {
                ItemTag = bestShard.ShardTag,
                Name = shardName,
                RatePerHour = 1.0 / bestShard.Hours,
                PriceEach = bestShard.Price,
                ContributionPerHour = profitPerHour
            }],
            ActionsPerHour = 1.0 / bestShard.Hours,
            ActionUnit = "traps",
            Category = "Hunting",
            Source = "formula",
            Type = TaskType.Passive
        };
        PopulateGuidanceFields(breakdown);

        return Task.FromResult(new TaskResult
        {
            ProfitPerHour = (int)profitPerHour,
            Type = TaskType.Passive, MostlyPassive = true,
            IsAccessible = true,
            Message = $"Trap at {McColorCodes.GREEN}{location} {McColorCodes.GRAY}— upper-bound estimate for {McColorCodes.YELLOW}{shardName} {McColorCodes.GRAY}({fmt.FormatPrice(profitPerHour)}/h; depends on capture)",
            Details = $"Selected shard: {McColorCodes.YELLOW}{shardName} {McColorCodes.GRAY}worth {McColorCodes.AQUA}{fmt.FormatPrice(bestShard.Price)}\n"
                + $"Location: {McColorCodes.GREEN}{string.Join(", ", bestShard.Locations)}\n"
                + $"{McColorCodes.GRAY}Upper-bound estimate: selected shard value divided by its ~{bestShard.Hours:0.#}h midpoint capture time.\n"
                + $"{McColorCodes.GRAY}Traps can catch other shards here; this shard is not guaranteed, and actual profit depends on the capture.\n"
                + $"{McColorCodes.DARK_GRAY}(Passive — can be done alongside active tasks)",
            Name = MethodName,
            Breakdown = breakdown
        });
    }
    protected override string HowTo => HuntingText.Steps(
        "Get a Huntrap; they unlock with the Tender Wood collection.",
        "Place it in the suggested spot and follow the required land or underwater placement rules; Bambuleaf requires Spring or Summer.",
        "Come back after the capture time, collect the shard and place the trap again.");
    public override string Description =>
        "A passive task: Huntraps placed where shard creatures live catch a shard on their own. A Small Huntrap needs 8-12 hours for a common shard and up to 16-24 hours for a legendary one, and bigger Huntraps are up to 50% faster. This task ranks an optimistic upper-bound estimate: selected shard value divided by its midpoint capture time. Traps may catch other shards, so actual profit depends on the capture.";
}
