using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

public partial class MayorAuraListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Mayor Aura")
            return;

        var fundraisingItem = args.msg.Chest.Items
            .FirstOrDefault(i => i.ItemName?.Contains("Fundraising") ?? false);

        if (fundraisingItem?.Description == null)
            return;

        var coinsRaised = ParseTotalCoinsRaised(fundraisingItem.Description);
        if (coinsRaised == null)
            return;

        var timestamp = new DateTime(
            args.msg.ReceivedAt.Year,
            args.msg.ReceivedAt.Month,
            args.msg.ReceivedAt.Day,
            args.msg.ReceivedAt.Hour,
            args.msg.ReceivedAt.Minute,
            0,
            DateTimeKind.Utc);

        var service = args.GetService<IMayorAuraService>();
        await service.StoreFundraising(new FundraisingEntry
        {
            Timestamp = timestamp,
            TotalCoinsRaised = coinsRaised.Value
        });
    }

    /// <summary>
    /// Parses the total coins raised from the fundraising item description
    /// </summary>
    /// <param name="description">Item description containing "Total Coins Raised: §6X,XXX,XXX,XXX"</param>
    /// <returns>The parsed amount or null if not found</returns>
    public static long? ParseTotalCoinsRaised(string description)
    {
        var match = TotalCoinsRaisedRegex().Match(description);
        if (!match.Success)
            return null;

        var amountString = match.Groups[1].Value.Replace(",", "");
        if (long.TryParse(amountString, out var amount))
            return amount;

        return null;
    }

    [GeneratedRegex(@"Total Coins Raised: §6([,\d]+)")]
    private static partial Regex TotalCoinsRaisedRegex();
}

/// <summary>
/// Represents a fundraising entry with timestamp rounded to minute
/// </summary>
public class FundraisingEntry
{
    public DateTime Timestamp { get; set; }
    public long TotalCoinsRaised { get; set; }
}

/// <summary>
/// Service for storing and retrieving Mayor Aura fundraising data
/// </summary>
public interface IMayorAuraService
{
    Task StoreFundraising(FundraisingEntry entry);
    Task<FundraisingEntry[]> GetFundraisingHistory();
}
