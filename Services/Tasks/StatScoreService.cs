using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// One stat signal affecting a task's rates.
/// Value is normalized as clamp(value / Max, 0, 1) and weighted into the effectiveness score.
/// </summary>
/// <param name="Key">namespaced signal key, e.g. skill:Fishing, attr:Fishing Speed, gear:FISHING_ROD, pet:FISHING, hotm:tier, hotf:tier, agatha:level</param>
/// <param name="Weight">relative importance among the task's factors</param>
/// <param name="Max">normalization cap for the raw value</param>
public record StatFactor(string Key, double Weight, double Max);

/// <summary>
/// Item tag ladders for gear based stat signals. The signal value is the
/// highest ladder index found in the player's inventory (0 when none).
/// </summary>
public static class GearLadders
{
    public static readonly Dictionary<string, string[]> Ladders = new()
    {
        ["FISHING_ROD"] =
        [
            "FISHING_ROD", "SPONGE_ROD", "CHALLENGE_ROD", "CHAMP_ROD", "LEGEND_ROD",
            "SHREDDER", "AUGER_ROD", "ROD_OF_THE_SEA", "HELLFIRE_ROD"
        ],
        ["GAUNTLET"] =
        [
            "MITHRIL_PICKAXE", "REFINED_MITHRIL_PICKAXE", "TITANIUM_PICKAXE", "REFINED_TITANIUM_PICKAXE",
            "DIVAN_DRILL", "GEMSTONE_GAUNTLET"
        ],
        ["HUNT_WEAPON"] =
        [
            "HUNTING_TOOLKIT", "SWEET_AXE", "TENDER_AXE", "HUNTAXE", "LUNGE_AXE"
        ],
    };

    /// <summary>
    /// Highest ladder index (1 based) of any ladder item found, 0 when none.
    /// </summary>
    public static int Resolve(string ladderName, IEnumerable<Item> inventory)
    {
        if (!Ladders.TryGetValue(ladderName, out var ladder) || inventory == null)
            return 0;
        var best = 0;
        foreach (var item in inventory)
        {
            if (item?.Tag == null)
                continue;
            var index = Array.IndexOf(ladder, item.Tag);
            if (index + 1 > best)
                best = index + 1;
        }
        return best;
    }
}

/// <summary>
/// Resolves stat signals from the player state and computes the per task
/// effectiveness score and stat bucket used to group similar players.
/// </summary>
public class StatScoreService
{
    /// <summary>Bucket for players whose stats are mostly unknown.</summary>
    public const byte UnknownBucket = 3;
    public const int BucketCount = 4;

    private readonly SkillService skillService;
    private readonly ILogger<StatScoreService> logger;
    private readonly ConcurrentDictionary<Guid, (Dictionary<string, int> skills, DateTime at)> skillCache = new();
    /// <summary>Decayed population mean per signal key, substituted for missing signals.</summary>
    private readonly ConcurrentDictionary<string, double> populationMeans = new();
    private static readonly TimeSpan SkillCacheDuration = TimeSpan.FromMinutes(30);

    public StatScoreService(SkillService skillService, ILogger<StatScoreService> logger)
    {
        this.skillService = skillService;
        this.logger = logger;
    }

    /// <summary>
    /// Effectiveness score of the player for the given factors, 0-1,
    /// and whether enough signal weight resolved to trust it.
    /// </summary>
    public async Task<(double score, bool known)> GetScore(List<StatFactor> factors, StateObject state)
    {
        if (factors == null || factors.Count == 0)
            return (0.5, false);
        double weightedSum = 0, totalWeight = 0, knownWeight = 0;
        foreach (var factor in factors)
        {
            totalWeight += factor.Weight;
            var raw = await Resolve(factor.Key, state);
            double x;
            if (raw.HasValue)
            {
                x = Math.Clamp(raw.Value / factor.Max, 0, 1);
                knownWeight += factor.Weight;
                // update the decayed population mean for this key
                populationMeans.AddOrUpdate(factor.Key, x, (_, prev) => prev + (x - prev) * 0.01);
            }
            else
            {
                x = populationMeans.GetValueOrDefault(factor.Key, 0.5);
            }
            weightedSum += factor.Weight * x;
        }
        var score = totalWeight > 0 ? weightedSum / totalWeight : 0.5;
        return (score, knownWeight / totalWeight >= 0.5);
    }

    /// <summary>
    /// Stat bucket for the player on this task: 0 low, 1 mid, 2 high, 3 unknown.
    /// </summary>
    public async Task<byte> GetBucket(List<StatFactor> factors, StateObject state)
    {
        var (score, known) = await GetScore(factors, state);
        if (!known)
            return UnknownBucket;
        return ScoreToBucket(score);
    }

    public static byte ScoreToBucket(double score) => (byte)(score < 1.0 / 3 ? 0 : score < 2.0 / 3 ? 1 : 2);

    /// <summary>
    /// Resolve one signal key against the player state. Null when unavailable.
    /// Stale values are used as-is: skyblock stats only ever grow, so a stale
    /// value at most puts the player into a lower bucket (the safe direction).
    /// </summary>
    private async Task<double?> Resolve(string key, StateObject state)
    {
        try
        {
            var info = state?.ExtractedInfo;
            var separator = key.IndexOf(':');
            if (separator < 0)
                return null;
            var prefix = key[..separator];
            var name = key[(separator + 1)..];
            switch (prefix)
            {
                case "skill":
                    var skills = await GetSkills(state);
                    return skills != null && skills.TryGetValue(name, out var level) ? level : null;
                case "attr":
                    return info?.AttributeLevel != null && info.AttributeLevel.TryGetValue(name, out var attr) ? attr : null;
                case "hotm":
                    return info?.HeartOfTheMountain?.Tier is > 0 and var hotm ? hotm : null;
                case "hotf":
                    return info?.HeartOfTheForest?.Tier is > 0 and var hotf ? hotf : null;
                case "agatha":
                    return info?.AgathaLevel is > 0 and var agatha ? agatha : null;
                case "gear":
                    var ladderValue = GearLadders.Resolve(name, state?.Inventory);
                    return ladderValue > 0 ? ladderValue : null;
                case "pet":
                    var pet = info?.Pets?.Where(p => p?.Type != null && p.Type.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(p => p.Level).FirstOrDefault();
                    return pet?.Level;
                default:
                    return null;
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to resolve stat signal {key}", key);
            return null;
        }
    }

    private async Task<Dictionary<string, int>> GetSkills(StateObject state)
    {
        var uuid = state?.McInfo?.Uuid ?? default;
        if (uuid == default || skillService == null)
            return null;
        if (skillCache.TryGetValue(uuid, out var cached) && DateTime.UtcNow - cached.at < SkillCacheDuration)
            return cached.skills;
        try
        {
            var skills = await skillService.GetSkills(uuid);
            var lookup = skills?.ToDictionary(s => s.Name, s => s.Level, StringComparer.OrdinalIgnoreCase);
            skillCache[uuid] = (lookup, DateTime.UtcNow);
            if (skillCache.Count > 2000) // eviction safety net, well above the live player cap
                skillCache.Clear();
            return lookup;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to load skills for {uuid}", uuid);
            skillCache[uuid] = (null, DateTime.UtcNow); // back off, retry after cache expiry
            return null;
        }
    }
}
