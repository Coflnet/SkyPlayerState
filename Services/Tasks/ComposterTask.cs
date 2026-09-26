using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.MC;

namespace Coflnet.Sky.PlayerState.Tasks;

public class ComposterTask : ProfitTask
{
    protected override string Where => "The Garden";
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Composter";

    public override async Task<TaskResult> Execute(TaskParams parameters)
    {
        var composterService = parameters.GetService<ComposterService>();
        var all = parameters.GetPrices();
        var compostPrice = all.GetValueOrDefault("COMPOST");
        var upgrades = parameters.ExtractedInfo?.Composter ?? new();
        var caclucated = composterService.GetBestFlip(parameters.GetPrices(), compostPrice, upgrades);
        var cropName = parameters.Names.GetValueOrDefault(caclucated.cropMatter, caclucated.cropMatter);
        var fuelName = parameters.Names.GetValueOrDefault(caclucated.fuel, caclucated.fuel);
        var breakdown = NewGuidanceBreakdown("Garden", "formula");
        breakdown.Steps =
        [
            new() { Number = 1, Text = "Type /warp garden to get close.", OnClick = "/warp garden" },
            new() { Number = 2, Text = $"Buy {cropName} on the Bazaar - that's the crop matter.", OnClick = $"/bz {cropName}" },
            new() { Number = 3, Text = $"Buy {fuelName} on the Bazaar too - that's the fuel.", OnClick = $"/bz {fuelName}" },
            new() { Number = 4, Text = "Open your Composter (near your Garden plots) and fill it with both." },
            new() { Number = 5, Text = "Wait for it to finish composting, then collect the Compost it made." },
            new() { Number = 6, Text = "Sell the Compost on the Bazaar for the top sell order." },
            new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
        ];
        return new TaskResult()
        {
            ProfitPerHour = (int)caclucated.profitPerHour,
            Type = TaskType.Passive, MostlyPassive = true,
            Message = $"Fill your composter with {McColorCodes.YELLOW}{cropName} {McColorCodes.GRAY}and {McColorCodes.YELLOW}{fuelName}",
            OnClick = $"/bz {cropName}",
            Details = $"{McColorCodes.YELLOW}Click to open {cropName} on bazaar\n"
                + $"{McColorCodes.GRAY}Then buy {McColorCodes.AQUA}{fuelName} {McColorCodes.GRAY}afterwards and fill your composter\n"
                + $"When its done composting sell order on bazaar for top order\n"
                + $"{McColorCodes.GRAY}This accounts for your {McColorCodes.AQUA}{upgrades.CostReductionPercent}% cost reduction\n"
                + $"{McColorCodes.GRAY}and {McColorCodes.AQUA}{upgrades.MultiDropChance}% extra drop chance\n"
                + $"{McColorCodes.GRAY}and {McColorCodes.AQUA}{upgrades.SpeedPercentIncrease}% speed increase\n",
            Breakdown = breakdown
        };
    }
    public override string Description => "Composter on the garden island.";
}
