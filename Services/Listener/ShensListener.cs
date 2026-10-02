using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public partial class ShensListener : UpdateListener
{
    private readonly ILogger<ShensListener> logger;

    public ShensListener(ILogger<ShensListener> logger)
    {
        this.logger = logger;
    }

    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest.Name == null || !args.msg.Chest.Name.StartsWith("Shen's"))
            return;
        var items = args.msg.Chest.Items.Take(6*9).Where(i => i.Tag != null).Select(i =>
        {
            var tag = i.Tag;
            var description = i.Description;
            var answer = description?.Split("\n").Skip(1).Select(line =>
            {
                var match = ShenBidLine().Match(line);
                if (!match.Success)
                    return null;
                return new { key = match.Groups[1].Value, value = ParseBid(match.Groups[2].Value) };
            }).Where(x => x?.key != null).ToDictionary(u => u!.key, u => u?.value);
            return (tag, JsonConvert.SerializeObject(answer));
        }).ToList();

        var shenHistory = new ShenHistory
        {
            Year = CurrentMinecraftYear(args.msg.ReceivedAt),
            Reporter = args.msg.PlayerId,
            ReportTime = args.msg.ReceivedAt,
            Offers = items.GroupBy(x => x.tag).ToDictionary(g => g.Key, g => g.Last().Item2)
        };
        await args.GetService<IShenStorage>().Store(shenHistory);
    }


    /// <summary>
    /// Shen's shows bids beyond the 64 bit range (production 2026-10-01:
    /// "9,223,372,036,854,776,000 Coins", long.MaxValue rounded up for display). Those are clamped
    /// to <see cref="long.MaxValue"/> instead of throwing, which dropped the whole inventory update.
    /// </summary>
    internal static long ParseBid(string value)
    {
        var digits = value.Replace(",", "");
        if (long.TryParse(digits, out var parsed))
            return parsed;
        return System.Numerics.BigInteger.TryParse(digits, out var big) && big > long.MaxValue ? long.MaxValue : 0;
    }

    private static int CurrentMinecraftYear(DateTime time)
    {
        return (int)((time - new DateTime(2019, 6, 13)).TotalDays / (TimeSpan.FromDays(5) + TimeSpan.FromHours(4)).TotalDays);
    }

    [GeneratedRegex(@"§e§l\d+\. (.*?) §8- §6([,\d]*) ")]
    private static partial Regex ShenBidLine();
}

public interface IShenStorage
{
    Task Store(ShenHistory shenHistory);
    Task<ShenHistory[]> Get(int year);
}

public class ShenHistory
{
    public int Year { get; set; }
    public string Reporter { get; set; }
    public DateTime ReportTime { get; set; }
    public Dictionary<string, string> Offers { get; set; }
}
