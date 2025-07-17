using System.Linq;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

public class HeartOfTheListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Heart of the Mountain" && args.msg.Chest?.Name != "Heart of the Forest")
            return;
        var targetHeart = args.msg.Chest.Name == "Heart of the Mountain" ? args.currentState.ExtractedInfo.HeartOfTheMountain : args.currentState.ExtractedInfo.HeartOfTheForest;
        if (targetHeart == null)
        {
            targetHeart = new HeartOfThe();
            if (args.msg.Chest.Name == "Heart of the Mountain")
                args.currentState.ExtractedInfo.HeartOfTheMountain = targetHeart;
            else
                args.currentState.ExtractedInfo.HeartOfTheForest = targetHeart;
        }
        var firstUnlockedItem = args.msg.Chest.Items.FirstOrDefault(i => (i.ItemName?.Contains("Tier") ?? false) && i.Description.Contains("UNLOCKED"));
        var tierString = firstUnlockedItem?.ItemName?.Split(' ').FirstOrDefault(s => int.TryParse(s, out _));
        var parsedTier = tierString != null && int.TryParse(tierString, out var tier) ? tier : 0;
        if(parsedTier > targetHeart.Tier)
        {
            targetHeart.Tier = parsedTier;
            Console.WriteLine($"Updated {args.msg.Chest.Name} tier to {targetHeart.Tier} for player {args.currentState.PlayerId}");
        }
    }
}
