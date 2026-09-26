using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Tasks;

public class ForgeTask : ProfitTask
{
    protected override string Where => "The Great Forge";
    protected override string WarpCommand => "/warp forge";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/The_Forge";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Type /warp forge to get close.", OnClick = "/warp forge" },
        new() { Number = 2, Text = "Open the Forge and start crafting the item this task suggests." },
        new() { Number = 3, Text = "Wait for it to finish (some crafts take minutes, others take hours or days)." },
        new() { Number = 4, Text = "Come back and collect it, then sell it on the Bazaar or Auction House." },
        new() { Number = 5, Text = "Check /cofl task again to see your real coins/hour." },
    ];

    public override async Task<TaskResult> Execute(TaskParams parameters)
    {
        var forgeStatus = parameters.ExtractedInfo.ForgeItems;
        if (forgeStatus == null || forgeStatus.Count == 0)
        {
            return new TaskResult
            {
                ProfitPerHour = 0,
                Type = TaskType.Passive, MostlyPassive = true,
                Message = "The status of forge is unknown, if you have unlocked it, please open the forge menu and try again",
                OnClick = WarpCommand,
                Breakdown = NewGuidanceBreakdown("Forge", "formula")
            };
        }
        if (forgeStatus.All(f => f.Tag != null && f.ForgeEnd > DateTime.UtcNow))
        {
            var forgeEnd = forgeStatus.Min(f => f.ForgeEnd);
            return new TaskResult
            {
                ProfitPerHour = 0,
                Type = TaskType.Passive, MostlyPassive = true,
                Message = $"You have an active forge task, the next will be finished in {parameters.Formatter.FormatTime(forgeEnd - DateTime.UtcNow)}.",
                OnClick = WarpCommand,
                Breakdown = NewGuidanceBreakdown("Forge", "formula")
            };
        }

        var flips = await parameters.GetService<ForgeFlipService>()
            .GetForgeFlips(parameters.PlayerUuid, parameters.ExtractedInfo);
        var best = flips
            .Where(f => f.CraftData.CraftCost < 20_000_000_000 && f.ProfitPerHour > 0)
            .OrderByDescending(f => f.ProfitPerHour)
            .FirstOrDefault();

        if (best == null)
        {
            return new TaskResult
            {
                ProfitPerHour = 0,
                Type = TaskType.Passive, MostlyPassive = true,
                Message = "No profitable forge flips found, please try again later.",
                OnClick = WarpCommand,
                Breakdown = NewGuidanceBreakdown("Forge", "formula")
            };
        }

        return new TaskResult
        {
            ProfitPerHour = (int)best.ProfitPerHour,
            Type = TaskType.Passive, MostlyPassive = true,
            Message = $"Forge {best.CraftData.ItemName}, takes {parameters.Formatter.FormatTime(TimeSpan.FromSeconds(best.Duration))}",
            Details = $"Ingredients required:\n" +
                      string.Join("\n", best.CraftData.Ingredients.Select(i => $"{i.ItemId} x{i.Count} ({parameters.Formatter.FormatPrice(i.Cost)})"))
                      + $"\nClick to warp to forge",
            OnClick = WarpCommand,
            Breakdown = NewGuidanceBreakdown("Forge", "formula")
        };
    }

    public override string Description => "Calculates profit using the forge";
}
