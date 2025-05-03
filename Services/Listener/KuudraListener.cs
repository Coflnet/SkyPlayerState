using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.MC;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class KuudraListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
            await CheckKuudra(args);
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
            await GotInventory(args);
    }

    private async Task GotInventory(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Paid Chest")
            return;
        var type = args.msg.Chest.Items[31].Description?.Split('\n').Last()
            ?? args.msg.Chest.Items.FirstOrDefault(i=>i.ItemName?.Contains("Kuudra Key") ?? false)?.ItemName;
        var items = args.msg.Chest.Items.Take(30).Where(i => i.Tag != null).ToList();
        var essence = args.msg.Chest.Items.Take(30).FirstOrDefault(i => i.ItemName.Contains("Essence"));
        if (essence != null)
        {
            essence.Tag = essence.ItemName.Substring(2).Split('x').First().Replace(" ", "_").ToUpper();
            items.Add(essence);
        }
        var value = await args.GetService<SniperService>().GetPrices(items);
        if (essence != null)
        {
            var essenceValue = value.Last(); // last index is last added
            if (int.TryParse(essence.ItemName.Split('x').Last(), out var count))
                essenceValue.Median = essenceValue.Median * count;
        }
        var coinSum = value.Sum(v => v.Median);
        var keyWorth = type switch
        {
            "§6Infernal Kuudra Key" => 3_500_000,
            "§5Fiery Kuudra Key" => 1_850_000,
            "§5Burning Kuudra Key" => 1_100_000,
            "§5Hot Kuudra Key" => 685_000,
            _ => 500_000
        };
        var combined = items.Zip(value, (i, v) => new { i, v });
        if (args.currentState.ExtractedInfo.KuudraStart > DateTime.UtcNow.AddMinutes(-5))
        {
            var timeElapsed = DateTime.UtcNow - args.currentState.ExtractedInfo.KuudraStart;
            args.SendMessage($"{McColorCodes.WHITE}This run was worth {McColorCodes.GOLD}{coinSum:N0} coins\n" +
                             $"Paid chest value: {McColorCodes.GOLD}{value.Sum(i => i?.Median ?? 0):N0} coins\n" +
                             $"Run took {McColorCodes.AQUA}{timeElapsed.Minutes}m {timeElapsed.Seconds}s\n" +
                             $"Thats an estimated {McColorCodes.GOLD}{(coinSum - keyWorth) / timeElapsed.TotalHours:N0} coins {McColorCodes.AQUA}per hour\n" +
                             $"{McColorCodes.DARK_GRAY}Thats {string.Join(' ', combined.Select(i => i.i.ItemName + ":" + i.v.Median).Distinct())}");
            args.currentState.ExtractedInfo.KuudraStart = DateTime.MinValue;
        }
        else
            args.SendMessage($"Kuudra chest is worth {coinSum - keyWorth:N0} coins");
        Console.WriteLine($"Got kuudra paid chest {args.msg.PlayerId} {type} {JsonConvert.SerializeObject(args.msg.Chest.Items.Take(30))}");
    }

    private async Task CheckKuudra(UpdateArgs args)
    {
        if (args.msg.Scoreboard == null)
            return;
        var kuudra = args.msg.Scoreboard.Any(s => s.StartsWith(" ⏣ Kuudra's Hollow"));
        if (!kuudra)
            return;
        Console.WriteLine($"Started kuudra {args.msg.PlayerId} {string.Join(" ", args.msg.Scoreboard)}");
        args.currentState.ExtractedInfo.KuudraStart = DateTime.UtcNow;
        args.SendMessage("Started kuudra run", source: "KuudraListener");
    }
}
