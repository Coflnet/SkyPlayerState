using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// Crop of a farming task: its crop fortune stat name (tab Stats widget / Garden crop line) and its Turbo enchant (NBT
/// name, NotEnoughUpdates-REPO constants/enchants.json). Stat names checked against SkyHanni (2026-11-07): the Garden crop
/// line pattern " (?&lt;crop&gt;CropType.cropName) Fortune:" with cropName "Melon Slice" and "Cocoa Beans" (CropType.kt,
/// FarmingFortuneDisplay.kt), the Stats widget names MELON_FORTUNE = "Melon Slice Fortune" and COCOA_BEANS_FORTUNE
/// (SkyblockStat.kt). The Stats widget line and the Garden crop line therefore use the same names.
/// </summary>
public static class FarmingCrops
{
    /// <param name="ToolTag">tag prefix of the crop's own tool (SkyHanni CropType.toolName; the Mk./tier suffix varies)</param>
    public record Crop(string Task, string FortuneStat, string TurboEnchant, string ToolTag);

    public static readonly IReadOnlyList<Crop> All =
    [
        new("Wheat Farming", "Wheat Fortune", "turbo_wheat", "THEORETICAL_HOE_WHEAT"),
        new("Carrot Farming", "Carrot Fortune", "turbo_carrot", "THEORETICAL_HOE_CARROT"),
        new("Potato Farming", "Potato Fortune", "turbo_potato", "THEORETICAL_HOE_POTATO"),
        new("Pumpkin Farming", "Pumpkin Fortune", "turbo_pumpkin", "PUMPKIN_DICER"),
        new("Melon Farming", "Melon Slice Fortune", "turbo_melon", "MELON_DICER"),
        new("Sugar Cane Farming", "Sugar Cane Fortune", "turbo_cane", "THEORETICAL_HOE_CANE"),
        new("Cactus Farming", "Cactus Fortune", "turbo_cactus", "CACTUS_KNIFE"),
        new("Cocoa Beans Farming", "Cocoa Beans Fortune", "turbo_coco", "COCO_CHOPPER"),
        new("Nether Wart Farming", "Nether Wart Fortune", "turbo_warts", "THEORETICAL_HOE_WARTS"),
        new("Mushroom Farming", "Mushroom Fortune", "turbo_mushrooms", "FUNGI_CUTTER"),
        new("Sunflower Farming", "Sunflower Fortune", "turbo_sunflower", "THEORETICAL_HOE_SUNFLOWER"),
        new("Moonflower Farming", "Moonflower Fortune", "turbo_moonflower", "THEORETICAL_HOE_SUNFLOWER"),
        new("Wild Rose Farming", "Wild Rose Fortune", "turbo_rose", "THEORETICAL_HOE_WILD_ROSE"),
    ];

    private static readonly Dictionary<string, Crop> ByTask = All.ToDictionary(c => c.Task, StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> CropFortuneStats = All.Select(c => c.FortuneStat).ToHashSet(StringComparer.Ordinal);

    public static Crop? ForTask(string? task) => task != null && ByTask.TryGetValue(task, out var crop) ? crop : null;

    public static bool IsTurbo(string enchant) => enchant.StartsWith("turbo_", StringComparison.Ordinal);
}
