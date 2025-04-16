using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class KuudraListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind == Models.UpdateMessage.UpdateKind.Scoreboard)
            await CheckKuudra(args);
        if(args.msg.Kind == Models.UpdateMessage.UpdateKind.INVENTORY)
            await GotInventory(args);
    }

    private async Task GotInventory(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Paid Chest")
            return;
        var type = args.msg.Chest.Items[31].Description.Split('\n').Last();
        var items = args.msg.Chest.Items.Take(30).Where(i => i.Tag != null).ToList();
        var value = await args.GetService<SniperService>().GetPrices(items);
        var coinSum = value.Sum(v => v.Median);
        var keyWorth = type switch
        {
            "§6Infernal Kuudra Key" => 3_500_000,
            "§5Fiery Kuudra Key" => 1_850_000,
            "§5Burning Kuudra Key" => 1_100_000,
            "§5Hot Kuudra Key" => 750_000,
            _ => 500_000
        };
        if (args.currentState.ExtractedInfo.KuudraStart > DateTime.UtcNow.AddMinutes(-5))
        {
            var timeElapsed = DateTime.UtcNow - args.currentState.ExtractedInfo.KuudraStart;
            args.SendMessage($"This run was worth {coinSum:N0} coins\n" +
                             $"Paid chest value: {value.Sum(i => i?.Median ?? 0):N0} coins\n" +
                             $"Run took {timeElapsed.Minutes}m {timeElapsed.Seconds}s\n" +
                             $"Thats an estimated {(coinSum - keyWorth) / timeElapsed.TotalHours:N0} coins per hour");
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
