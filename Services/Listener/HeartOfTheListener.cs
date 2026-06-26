using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

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
            Logger.LogInformation("Updated {chestName} tier to {tier} for player {playerId}", args.msg.Chest.Name, targetHeart.Tier, args.currentState.PlayerId);
        }

        var mithrilPowder = ParseMithrilPowderFromHeartOfThe(args.msg.Chest);
        if (mithrilPowder.HasValue)
            CollectionListener.TrackMithrilPowder(args, mithrilPowder.Value);
    }

    internal static int? ParseMithrilPowderFromHeartOfThe(ChestView? chest)
    {
        if (chest?.Items == null)
            return null;

        foreach (var item in chest.Items)
        {
            var cleanName = StripFormatting(item?.ItemName ?? string.Empty);
            if (!cleanName.Contains("Mithril Powder", StringComparison.OrdinalIgnoreCase))
                continue;

            var mergedText = StripFormatting($"{item.ItemName}\n{item.Description}");

            var lineMatch = Regex.Match(mergedText, @"Mithril Powder\s*[:\-]?\s*([\d,]+)", RegexOptions.IgnoreCase);
            if (lineMatch.Success && TryParseNumber(lineMatch.Groups[1].Value, out var byLabel))
                return byLabel;

            var reverseMatch = Regex.Match(mergedText, @"([\d,]+)\s*Mithril Powder", RegexOptions.IgnoreCase);
            if (reverseMatch.Success && TryParseNumber(reverseMatch.Groups[1].Value, out var byReverse))
                return byReverse;

            // Fallback for variants where the amount is shown on the powder item without repeating the label.
            var anyNumber = Regex.Match(mergedText, @"([\d,]+)");
            if (anyNumber.Success && TryParseNumber(anyNumber.Groups[1].Value, out var fallback))
                return fallback;
        }

        return null;
    }

    private static bool TryParseNumber(string value, out int parsed)
    {
        return int.TryParse(value.Replace(",", string.Empty), out parsed);
    }

    private static string StripFormatting(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return Regex.Replace(value, "§.", string.Empty);
    }
}
