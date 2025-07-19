using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class ForgeListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "The Forge")
            return;

        var inprocess = args.msg.Chest.Items.Take(17)
            .Where(i => i.Tag != null || (i.Description?.Contains("Click to select") ?? false))
            .ToList();

        args.currentState.ExtractedInfo.ForgeItems = inprocess.Select(i => i == null ? null : new ForgeItem
        {
            ItemName = i.ItemName!,
            ForgeEnd = ParseTimeFromDescription(i), // Placeholder, replace with actual end time
            Tag = i.Tag
        }).ToList();
    }

    public DateTime ParseTimeFromDescription(Item i)
    {
        // example: §7Time Remaining: §a55m 6s
        // optional hours: §7Time Remaining: §a4h 11m for item §5Refined Mithril
        var timeMatch = System.Text.RegularExpressions.Regex.Match(i.Description!, @"§7Time Remaining: §a(?:(\d+)d )?(?:(\d+)h )?(?:(\d+)m )?(?:(\d+)s)?");
        if (timeMatch.Success)
        {
            int days = timeMatch.Groups[1].Success ? int.Parse(timeMatch.Groups[1].Value) : 0;
            int hours = timeMatch.Groups[2].Success ? int.Parse(timeMatch.Groups[2].Value) : 0;
            int minutes = timeMatch.Groups[3].Success ? int.Parse(timeMatch.Groups[3].Value) : 0;
            int seconds = timeMatch.Groups[4].Success ? int.Parse(timeMatch.Groups[4].Value) : 0;
            var parsed = DateTime.Now.AddDays(days).AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);
            Console.WriteLine($"Parsed forge item time: {parsed} for item {i.ItemName}");
            return parsed;
        }
        else if (i.Tag == null)
            return default;
        else if (i.Description?.Contains("Completed") ?? false)
        {
            return DateTime.UtcNow.AddMinutes(-1);
        }
        else
        {
            Console.WriteLine($"Failed to parse time from description: {i.Description} for item {i.ItemName}");
            // Fallback if no time is found, could be an error or a default value
            return DateTime.Now.AddMinutes(30);
        }

    }
}
