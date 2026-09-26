using Coflnet.Sky.Commands.MC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Reaper Scythe crafting task.
/// Craft recipe: Uses drops from M3 (Master Mode floor 3) combined in the forge.
/// Estimates ~110.86M/h based on:
/// - Time to run M3 and collect drops
/// - Forge time for crafting the Reaper Scythe
/// - Sell price minus material costs
/// This task checks the forge for Reaper Scythe profitability and falls back
/// to location profit tracking in the Catacombs.
/// </summary>
public class ReaperScytheTask : ProfitTask
{
    public override string Description => "Estimates profit from crafting Reaper Scythes via M3 runs";
    protected override string Where => "The Great Forge";
    protected override string WarpCommand => "/warp forge";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Reaper_Scythe";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Run Master Mode Floor 3 (M3) in the Catacombs and collect the Wither Essence/drops it needs." },
        new() { Number = 2, Text = "Type /warp forge to get close.", OnClick = "/warp forge" },
        new() { Number = 3, Text = "Craft the Reaper Scythe in the Forge with those drops.", OnClick = WikiUrl },
        new() { Number = 4, Text = "Wait for the craft to finish." },
        new() { Number = 5, Text = "Sell the Reaper Scythe on the Auction House." },
        new() { Number = 6, Text = "Check /cofl task again to see your real coins/hour." },
    ];

    public override async Task<TaskResult> Execute(TaskParams parameters)
    {
        var tag = "REAPER_SCYTHE";
        var sellPrice = parameters.CleanPrices.TryGetValue(tag, out var reaperPrice) ? reaperPrice : 0;
        if (sellPrice <= 0)
        {
            return new TaskResult
            {
                ProfitPerHour = 0,
                Message = "Reaper Scythe price data unavailable.",
                Details = "Cannot determine current sell price for Reaper Scythe.",
                OnClick = WarpCommand,
                Breakdown = NewGuidanceBreakdown("Crafting", "formula")
            };
        }

        // Check forge for existing profits
        var flips = await parameters.GetService<ForgeFlipService>().GetForgeFlips(parameters.PlayerUuid, parameters.ExtractedInfo);
        var reaperFlip = flips.FirstOrDefault(f =>
            f.CraftData.ItemId?.Contains("REAPER") == true ||
            f.CraftData.ItemName?.Contains("Reaper") == true);

        if (reaperFlip != null && reaperFlip.ProfitPerHour > 0)
        {
            return new TaskResult
            {
                ProfitPerHour = (int)reaperFlip.ProfitPerHour,
                Message = $"Craft Reaper Scythe, takes {parameters.Formatter.FormatTime(TimeSpan.FromSeconds(reaperFlip.Duration))}",
                Details = $"Ingredients: {string.Join(", ", reaperFlip.CraftData.Ingredients.Select(i => $"{i.ItemId} x{i.Count}"))}\nClick to warp to forge",
                OnClick = "/warp forge",
                Breakdown = NewGuidanceBreakdown("Crafting", "formula")
            };
        }

        return new TaskResult
        {
            ProfitPerHour = 0,
            Message = "Reaper Scythe not currently profitable in forge or recipe not available.",
            Details = "Check /cofl forge for current forge craft opportunities.",
            OnClick = WarpCommand,
            Breakdown = NewGuidanceBreakdown("Crafting", "formula")
        };
    }
}

/// <summary>
/// Gauntlet of Contagion crafting task.
/// Crimson Isle crafting item using drops from dungeon/slayer activities.
/// </summary>
public class GauntletOfContagionTask : ProfitTask
{
    public override string Description => "Estimates profit from crafting Gauntlet of Contagion";
    protected override string Where => "The Great Forge";
    protected override string WarpCommand => "/warp forge";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Gauntlet_of_Contagion";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Collect the ingredients this craft needs (mostly slayer/dungeon drops)." },
        new() { Number = 2, Text = "Type /warp forge to get close.", OnClick = "/warp forge" },
        new() { Number = 3, Text = "Craft the Gauntlet of Contagion in the Forge.", OnClick = WikiUrl },
        new() { Number = 4, Text = "Wait for the craft to finish." },
        new() { Number = 5, Text = "Sell the Gauntlet of Contagion on the Auction House." },
        new() { Number = 6, Text = "Check /cofl task again to see your real coins/hour." },
    ];

    public override async Task<TaskResult> Execute(TaskParams parameters)
    {
        var tag = "GAUNTLET_OF_CONTAGION";
        var sellPrice = parameters.CleanPrices.TryGetValue(tag, out var gauntletPrice) ? gauntletPrice : 0;
        if (sellPrice <= 0)
        {
            return new TaskResult
            {
                ProfitPerHour = 0,
                Message = "Gauntlet of Contagion price data unavailable.",
                Details = "Cannot determine current sell price.",
                OnClick = WarpCommand,
                Breakdown = NewGuidanceBreakdown("Crafting", "formula")
            };
        }

        var flips = await parameters.GetService<ForgeFlipService>().GetForgeFlips(parameters.PlayerUuid, parameters.ExtractedInfo);
        var gauntletFlip = flips.FirstOrDefault(f =>
            f.CraftData.ItemId?.Contains("GAUNTLET") == true ||
            f.CraftData.ItemName?.Contains("Gauntlet") == true);

        if (gauntletFlip != null && gauntletFlip.ProfitPerHour > 0)
        {
            return new TaskResult
            {
                ProfitPerHour = (int)gauntletFlip.ProfitPerHour,
                Message = $"Craft Gauntlet of Contagion, takes {parameters.Formatter.FormatTime(TimeSpan.FromSeconds(gauntletFlip.Duration))}",
                Details = $"Ingredients: {string.Join(", ", gauntletFlip.CraftData.Ingredients.Select(i => $"{i.ItemId} x{i.Count}"))}\nClick to warp to forge",
                OnClick = "/warp forge",
                Breakdown = NewGuidanceBreakdown("Crafting", "formula")
            };
        }

        return new TaskResult
        {
            ProfitPerHour = 0,
            Message = "Gauntlet of Contagion not currently profitable or recipe unavailable.",
            Details = "Check /cofl forge for current forge craft opportunities.",
            OnClick = WarpCommand,
            Breakdown = NewGuidanceBreakdown("Crafting", "formula")
        };
    }
}

/// <summary>
/// Exportable Carrots crafting task (Garden).
/// Craft carrots into exportable form for profit.
/// Uses bazaar prices for carrot-related items.
/// </summary>
public class ExportableCarrotsCraftTask : ProfitTask
{
    public override string Description => "Estimates profit from crafting Exportable Carrots";

    public override Task<TaskResult> Execute(TaskParams parameters)
    {
        var buyTag = "CARROT_ITEM";
        var sellTag = "EXPORTABLE_CARROT";

        var buyPrice = parameters.BazaarPrices?.FirstOrDefault(p => p.ProductId == buyTag)?.BuyPrice ?? 0;
        var sellPrice = parameters.BazaarPrices?.FirstOrDefault(p => p.ProductId == sellTag)?.SellPrice ?? 0;

        if (buyPrice <= 0 || sellPrice <= 0)
        {
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = "Exportable Carrots price data unavailable.",
                Details = "Cannot determine bazaar prices for carrots."
            });
        }

        // Rough estimation: 160 crafts per hour (including gathering), each craft uses 160 carrots
        var craftsPerHour = 160;
        var carrotsPerCraft = 160;
        var profitPerCraft = (double)sellPrice - (buyPrice * carrotsPerCraft);
        var profitPerHour = profitPerCraft * craftsPerHour;
        var fmt = parameters.Formatter;

        if (profitPerHour <= 0)
        {
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = "Exportable Carrots crafting is not currently profitable.",
                Details = $"Buy price: {fmt.FormatPrice((long)buyPrice)}/carrot\nSell price: {fmt.FormatPrice((long)sellPrice)}/exportable"
            });
        }

        return Task.FromResult(new TaskResult
        {
            ProfitPerHour = (int)profitPerHour,
            Message = $"Craft Exportable Carrots for {McColorCodes.AQUA}{fmt.FormatPrice((long)profitPerHour)}/h",
            Details = $"Buy carrots at {fmt.FormatPrice((long)buyPrice)} each\n"
                + $"Craft and sell at {fmt.FormatPrice((long)sellPrice)} each\n"
                + $"~{craftsPerHour} crafts/hour possible\n"
                + $"NOTE: Estimates may vary.",
            OnClick = "/bz carrot"
        });
    }
}

// ── Additional Crafting/Bazaar Flip Tasks ──

/// <summary>
/// Generic forge craft task helper - looks up a craft by item tag in forge API.
/// </summary>
public abstract class ForgeCraftTask : ProfitTask
{
    protected abstract string ItemTag { get; }
    protected abstract string SearchTerm { get; }
    protected abstract string ItemDisplayName { get; }

    public override string Description => $"Estimates profit from crafting {ItemDisplayName}";
    protected override string Where => "The Great Forge";
    protected override string WarpCommand => "/warp forge";
    /// <summary>Best-effort wiki page guess from the item's own display name - verify with curl when adding a new ForgeCraftTask.</summary>
    protected override string WikiUrl => SkyblockZones.WikiPageUrl(ItemDisplayName);
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = $"Collect the ingredients {ItemDisplayName} needs (check the recipe on the wiki).", OnClick = WikiUrl },
        new() { Number = 2, Text = "Type /warp forge to get close.", OnClick = "/warp forge" },
        new() { Number = 3, Text = $"Craft {ItemDisplayName} in the Forge." },
        new() { Number = 4, Text = "Wait for the craft to finish." },
        new() { Number = 5, Text = $"Sell {ItemDisplayName} on the Auction House or Bazaar." },
        new() { Number = 6, Text = "Check /cofl task again to see your real coins/hour." },
    ];

    public override async Task<TaskResult> Execute(TaskParams parameters)
    {
        var flips = await parameters.GetService<ForgeFlipService>().GetForgeFlips(parameters.PlayerUuid, parameters.ExtractedInfo);
        var flip = flips.FirstOrDefault(f =>
            (f.CraftData.ItemId?.Contains(SearchTerm) == true) ||
            (f.CraftData.ItemName?.Contains(SearchTerm) == true));

        if (flip != null && flip.ProfitPerHour > 0)
            return new TaskResult
            {
                ProfitPerHour = (int)flip.ProfitPerHour,
                Message = $"Craft {ItemDisplayName}, takes {parameters.Formatter.FormatTime(TimeSpan.FromSeconds(flip.Duration))}",
                Details = $"Ingredients: {string.Join(", ", flip.CraftData.Ingredients.Select(i => $"{i.ItemId} x{i.Count}"))}\nClick to warp to forge",
                OnClick = "/warp forge",
                MostlyPassive = true,
                Breakdown = NewGuidanceBreakdown("Crafting", "formula")
            };

        return new TaskResult
        {
            ProfitPerHour = 0,
            Message = $"{ItemDisplayName} not currently profitable or unavailable.",
            Details = "Check /cofl forge for current forge craft opportunities.",
            OnClick = WarpCommand,
            Breakdown = NewGuidanceBreakdown("Crafting", "formula")
        };
    }
}

public class ExtremelyRealShurikenTask : ForgeCraftTask
{
    protected override string ItemTag => "EXTREMELY_REAL_SHURIKEN";
    protected override string SearchTerm => "SHURIKEN";
    protected override string ItemDisplayName => "Extremely Real Shuriken";
}
public class ShimmeringLightHoodTask : ForgeCraftTask
{
    protected override string ItemTag => "SHIMMERING_LIGHT_HOOD";
    protected override string SearchTerm => "SHIMMERING";
    protected override string ItemDisplayName => "Shimmering Light Hood";
}
public class PolarvoidBookTask : ForgeCraftTask
{
    protected override string ItemTag => "POLARVOID_BOOK";
    protected override string SearchTerm => "POLARVOID";
    protected override string ItemDisplayName => "Polarvoid Book";
}
public class GrandmasKnittingNeedleTask : ForgeCraftTask
{
    protected override string ItemTag => "GRANDMA_KNITTING_NEEDLE";
    protected override string SearchTerm => "KNITTING";
    protected override string ItemDisplayName => "Grandma's Knitting Needle";
}
public class SoulOfTheAlphaTask : ForgeCraftTask
{
    protected override string ItemTag => "SOUL_OF_THE_ALPHA";
    protected override string SearchTerm => "SOUL_OF";
    protected override string ItemDisplayName => "Soul of the Alpha";
}
public class BluetoothRingTask : ForgeCraftTask
{
    protected override string ItemTag => "BLUETOOTH_RING";
    protected override string SearchTerm => "BLUETOOTH";
    protected override string ItemDisplayName => "Bluetooth Ring";
}
public class DiscriteTask : ForgeCraftTask
{
    protected override string ItemTag => "DISCRITE";
    protected override string SearchTerm => "DISCRITE";
    protected override string ItemDisplayName => "Discrite";
}
public class CaducousFeederTask : ForgeCraftTask
{
    protected override string ItemTag => "CADUCOUS_FEEDER";
    protected override string SearchTerm => "CADUCOUS";
    protected override string ItemDisplayName => "Caducous Feeder";
}

/// <summary>
/// Bazaar flip tasks - buy materials from bazaar, craft, sell result on bazaar.
/// </summary>
public abstract class BazaarCraftTask : ProfitTask
{
    protected abstract string BuyTag { get; }
    protected abstract string SellTag { get; }
    protected abstract string CraftName { get; }
    protected abstract int InputPerCraft { get; }
    protected abstract int CraftsPerHour { get; }

    public override string Description => $"Estimates profit from {CraftName} bazaar crafting";

    public override Task<TaskResult> Execute(TaskParams parameters)
    {
        var buyPrice = parameters.BazaarPrices?.FirstOrDefault(p => p.ProductId == BuyTag)?.BuyPrice ?? 0;
        var sellPrice = parameters.BazaarPrices?.FirstOrDefault(p => p.ProductId == SellTag)?.SellPrice ?? 0;

        if (buyPrice <= 0 || sellPrice <= 0)
            return Task.FromResult(new TaskResult { ProfitPerHour = 0, Message = $"{CraftName} price data unavailable." });

        var profitPerCraft = (double)sellPrice - (buyPrice * InputPerCraft);
        var profitPerHour = profitPerCraft * CraftsPerHour;

        if (profitPerHour <= 0)
            return Task.FromResult(new TaskResult { ProfitPerHour = 0, Message = $"{CraftName} not currently profitable." });

        var fmt = parameters.Formatter;
        return Task.FromResult(new TaskResult
        {
            ProfitPerHour = (int)profitPerHour,
            Message = $"{CraftName} for {McColorCodes.AQUA}{fmt.FormatPrice((long)profitPerHour)}/h",
            Details = $"Buy {BuyTag} at {fmt.FormatPrice((long)buyPrice)}\nSell {SellTag} at {fmt.FormatPrice((long)sellPrice)}\n~{CraftsPerHour} crafts/hour",
            OnClick = $"/bz {BuyTag}"
        });
    }
}

public class BladeSoulBzTask : BazaarCraftTask
{
    protected override string BuyTag => "BLADESOUL_FRAGMENT";
    protected override string SellTag => "BLADESOUL_BLADE";
    protected override string CraftName => "Bladesoul (BZ)";
    protected override int InputPerCraft => 8;
    protected override int CraftsPerHour => 120;
}
public class AshfangBzTask : BazaarCraftTask
{
    protected override string BuyTag => "DERELICT_ASHE";
    protected override string SellTag => "EMBER_ROD";
    protected override string CraftName => "Ashfang (BZ)";
    protected override int InputPerCraft => 4;
    protected override int CraftsPerHour => 120;
}
public class EmptyChumcapBucketTask : BazaarCraftTask
{
    protected override string BuyTag => "CHUMCAP";
    protected override string SellTag => "EMPTY_CHUMCAP_BUCKET";
    protected override string CraftName => "Empty Chumcap Bucket";
    protected override int InputPerCraft => 1;
    protected override int CraftsPerHour => 200;
}
public class EndermanPetFdTask : BazaarCraftTask
{
    protected override string BuyTag => "NULL_SPHERE";
    protected override string SellTag => "ENDERMAN_PET_ITEM";
    protected override string CraftName => "Enderman Pet (FD)";
    protected override int InputPerCraft => 8;
    protected override int CraftsPerHour => 60;
}
public class ExportableCarrotsTask : BazaarCraftTask
{
    protected override string BuyTag => "CARROT_ITEM";
    protected override string SellTag => "EXPORTABLE_CARROT";
    protected override string CraftName => "Exportable Carrots";
    protected override int InputPerCraft => 160;
    protected override int CraftsPerHour => 160;
}
