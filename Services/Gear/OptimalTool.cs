using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The task's tool: players use the best tool they carry for a task (nobody farms with a sword, and for combat the
/// Aspect of the Dragons beats the Aspect of the End). Among the inventory items whose lore item type is one of the
/// category's tool types, the one with the highest primary lore stat wins:
/// <list type="bullet">
/// <item>farming: a tool made for the task's crop (SkyHanni CropType tool tags), then the crop's fortune line, then Farming Fortune;</item>
/// <item>mining: Mining Speed, then Breaking Power, then Mining Fortune;</item>
/// <item>fishing: Sea Creature Chance, then Fishing Speed; lava categories only lava rods, water categories none of them;</item>
/// <item>foraging: Foraging Fortune, then Sweep;</item>
/// <item>combat (dungeon, kuudra, unbound combat tasks): (5 + Damage) x (1 + Strength / 100), utility swords
/// (<see cref="UtilityWeapons"/>) only without another weapon; unbound non-combat tasks get no tool.</item>
/// </list>
/// An item without any primary stat is never the tool. Earlier inventory slots (hotbar first) win ties.
/// </summary>
public static class OptimalTool
{
    /// <summary>Candidates kept per tool group (each crop's farming tools, mining tools, water rods, lava rods, axes, weapons).</summary>
    public const int PerGroup = 3;

    /// <summary>Teleport and utility swords that are carried next to the real weapon (NotEnoughUpdates-REPO tags).</summary>
    public static readonly IReadOnlySet<string> UtilityWeapons = new HashSet<string>(StringComparer.Ordinal) { "ASPECT_OF_THE_END", "ASPECT_OF_THE_VOID" };

    private record Scored(double[] Rank, bool HasPrimary);

    /// <summary>
    /// The inventory's tools, hotbar first, then the main inventory: the best <see cref="PerGroup"/> of every tool group,
    /// so the slot order and other tool types never push a task's best tool out.
    /// </summary>
    public static List<GearPiece> Candidates(IReadOnlyList<Item> items)
    {
        var inventory = InventoryLayout.PlayerInventory(items);
        var hotbarFirst = inventory.Skip(InventoryLayout.PlayerSlots - InventoryLayout.HotbarSlots)
            .Concat(inventory.Take(InventoryLayout.PlayerSlots - InventoryLayout.HotbarSlots));
        var tools = hotbarFirst.Select(i => GearNormalizer.Normalize(i)).OfType<GearPiece>()
            .Where(p => p.ItemType != null && GearModifierTable.Capture.ToolTypes.Contains(p.ItemType))
            .Select((piece, order) => (piece, order)).ToList();
        return tools.GroupBy(t => Group(t.piece))
            .SelectMany(g => g.OrderByDescending(t => t.piece, RankComparer(g.Key)).ThenBy(t => t.order).Take(PerGroup))
            .OrderBy(t => t.order).Select(t => t.piece).ToList();
    }

    /// <summary>The best candidate for the task, null without a task or without a candidate with a primary stat.</summary>
    public static GearPiece? Pick(IEnumerable<GearPiece>? candidates, string? task)
    {
        var category = GearCategories.ForTask(task);
        if (category == null || candidates == null || IsAmbiguousOther(category, task))
            return null;
        var spec = GearModifierTable.For(category);
        var score = Scorer(category, task);
        GearPiece? best = null;
        double[]? bestRank = null;
        foreach (var piece in candidates.Where(p => Fits(p, spec, category)))
        {
            var current = score(piece);
            if (current.HasPrimary && (bestRank == null || Compare(current.Rank, bestRank) > 0))
                (best, bestRank) = (piece, current.Rank);
        }
        return best;
    }

    private static bool Fits(GearPiece piece, GearCategorySpec spec, string category)
    {
        if (piece.ItemType == null || !spec.ToolTypes.Contains(piece.ItemType))
            return false;
        var lava = GearModifierTable.LavaRods.Contains(piece.Tag);
        return category switch
        {
            GearModifierTable.FishingLava => lava,
            GearModifierTable.FishingWater => !lava,
            _ => true
        };
    }

    /// <summary>
    /// Unbound tasks fall back to the combat spec ("other"), but a gathering task without a binding must not get a sword
    /// as its tool: for them the tool stays unknown (ambiguous).
    /// </summary>
    private static bool IsAmbiguousOther(string category, string? task)
        => category == GearModifierTable.Other && !GearCategories.IsCombatTask(task);

    /// <summary>heldSlot when the mod reported the picked tool as held, optimal otherwise, ambiguous without a tool.</summary>
    public static string Evidence(GearPiece? tool, string? heldKey)
    {
        if (tool == null)
            return GearToolEvidence.Ambiguous;
        return heldKey != null && heldKey == GearFingerprint.PieceKey(tool) ? GearToolEvidence.HeldSlot : GearToolEvidence.Optimal;
    }

    private static Func<GearPiece, Scored> Scorer(string category, string? task) => category switch
    {
        GearModifierTable.Farming => p => FarmingScore(p, task),
        GearModifierTable.MiningOre or GearModifierTable.MiningGem => p => Ranked(Stat(p, "Mining Speed"), Stat(p, "Breaking Power"), Stat(p, "Mining Fortune")),
        GearModifierTable.FishingWater or GearModifierTable.FishingLava => p => Ranked(Stat(p, "Sea Creature Chance"), Stat(p, "Fishing Speed")),
        GearModifierTable.Foraging => p => Ranked(Stat(p, "Foraging Fortune"), Stat(p, "Sweep")),
        _ => CombatScore
    };

    private static Scored Ranked(params double[] stats) => new(stats, stats.Any(s => s > 0));

    private static Scored FarmingScore(GearPiece piece, string? task)
    {
        var crop = FarmingCrops.ForTask(task);
        var madeForCrop = crop != null && piece.Tag.StartsWith(crop.ToolTag, StringComparison.Ordinal) ? 1 : 0;
        var cropFortune = crop == null ? 0 : Stat(piece, crop.FortuneStat);
        return Ranked(madeForCrop, cropFortune, Stat(piece, "Farming Fortune"));
    }

    private static Scored CombatScore(GearPiece piece)
    {
        var damage = Stat(piece, "Damage");
        var strength = Stat(piece, "Strength");
        var mainWeapon = UtilityWeapons.Contains(piece.Tag) ? 0 : 1;
        return new([mainWeapon, (5 + damage) * (1 + strength / 100)], damage > 0 || strength > 0);
    }

    /// <summary>Tool group of a candidate: farming tools per crop, mining tools, water and lava rods, axes, weapons.</summary>
    private static string Group(GearPiece piece) => piece.ItemType switch
    {
        "FARMING TOOL" => FarmingCrops.All.FirstOrDefault(c => piece.Tag.StartsWith(c.ToolTag, StringComparison.Ordinal))?.Task ?? GearModifierTable.Farming,
        "DRILL" or "PICKAXE" or "GAUNTLET" => GearModifierTable.MiningOre,
        "FISHING ROD" => GearModifierTable.LavaRods.Contains(piece.Tag) ? GearModifierTable.FishingLava : GearModifierTable.FishingWater,
        "AXE" => GearModifierTable.Foraging,
        _ => GearModifierTable.Dungeon
    };

    /// <summary>Orders a group by the score of the category (or crop task) the group stands for.</summary>
    private static IComparer<GearPiece> RankComparer(string group)
    {
        var crop = FarmingCrops.ForTask(group);
        var score = crop != null ? Scorer(GearModifierTable.Farming, crop.Task) : Scorer(group, null);
        return Comparer<GearPiece>.Create((a, b) => Compare(score(a).Rank, score(b).Rank));
    }

    private static double Stat(GearPiece piece, string name) => piece.Stats?.GetValueOrDefault(name) ?? 0;

    private static int Compare(double[] a, double[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        return 0;
    }
}
