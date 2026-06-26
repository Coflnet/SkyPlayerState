using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class KatChatListener : UpdateListener
{
    /*
    [NPC] Kat: I'll get your Bee upgraded to UNCOMMON in no time!
    [NPC] Kat: Come back in 1 hour to pick it up!
    [NPC] Kat: I'm currently taking care of your Bee!
    [NPC] Kat: You can pick it up in 59 minutes 22 seconds
    */
    public override async Task Process(UpdateArgs args)
    {

        foreach (var line in args.msg.ChatBatch)
        {
            if (!line.StartsWith("[NPC] Kat:"))
                continue;
            if (line.Contains("I'll get your"))
            {
                var match = Regex.Match(line, @"\[NPC\] Kat: I'll get your (?<item>.+?) upgraded to (?<rarity>.+?) in no time!");
                if (match.Success)
                {
                    args.currentState.ExtractedInfo.KatStatus ??= new();
                    args.currentState.ExtractedInfo.KatStatus.ItemName = match.Groups["item"].Value;
                    args.currentState.ExtractedInfo.KatStatus.IsKatActive = true;
                    args.currentState.ExtractedInfo.KatStatus.KatEnd = DateTime.Now.AddHours(1);
                }
            }
            else if (line.Contains("Come back in"))
            {
                var match = Regex.Match(line, @"\[NPC\] Kat: Come back in (?<time>.+) to pick it up!");
                if (match.Success)
                {
                    var timeString = match.Groups["time"].Value;
                    if (timeString.Contains("hour"))
                    {
                        Logger.LogWarning("Time string contains 'hour': {timeString}", timeString);
                        return;
                    }
                    args.currentState.ExtractedInfo.KatStatus.KatEnd = DateTime.Now.Add(ParseTime(timeString));
                }
            }
            else if (line.Contains("I'm currently taking care of your"))
            {
                var match = Regex.Match(line, @"\[NPC\] Kat: I'm currently taking care of your (?<item>.+?)!");
                if (match.Success)
                {
                    args.currentState.ExtractedInfo.KatStatus ??= new();
                    args.currentState.ExtractedInfo.KatStatus.ItemName = match.Groups["item"].Value;
                    Logger.LogInformation("Found Kat item in line: {line} - {item}", line, args.currentState.ExtractedInfo.KatStatus.ItemName);
                    args.currentState.ExtractedInfo.KatStatus.IsKatActive = true;
                }
            }
            else if (line.Contains("You can pick it up in"))
            {
                var match = Regex.Match(line, @"\[NPC\] Kat: You can pick it up in (?<time>.+)\.");
                if (match.Success)
                {
                    Logger.LogInformation("Found Kat end time in line: {line}", line);
                    var timeString = match.Groups["time"].Value;
                    args.currentState.ExtractedInfo.KatStatus.KatEnd = DateTime.Now.Add(ParseTime(timeString));
                }
                else
                {
                    Logger.LogWarning("Failed to parse time from line: {line}", line);
                }
            }
            if (line.StartsWith("[NPC] Kat: If you have any other pets you'd like to upgrade")) // done & collect message
            {
                args.currentState.ExtractedInfo.KatStatus ??= new();
                args.currentState.ExtractedInfo.KatStatus.IsKatActive = false;
                Logger.LogInformation("Kat upgrade completed for player {player}", args.currentState.PlayerId);
            }
        }

        TimeSpan ParseTime(string timeString)
        {
            var timeSpan = TimeSpan.Zero;
            var dayMatch = Regex.Match(timeString, @"(\d+)\s*day");
            var hourMatch = Regex.Match(timeString, @"(\d+)\s*hour");
            var minMatch = Regex.Match(timeString, @"(\d+)\s*minute");
            var secMatch = Regex.Match(timeString, @"(\d+)\s*second");
            Logger.LogDebug("Parsing time string: {timeString}", timeString);
            if (dayMatch.Success)
                timeSpan = timeSpan.Add(TimeSpan.FromDays(int.Parse(dayMatch.Groups[1].Value)));
            if (hourMatch.Success)
                timeSpan = timeSpan.Add(TimeSpan.FromHours(int.Parse(hourMatch.Groups[1].Value)));
            if (minMatch.Success)
            {
                Logger.LogDebug("Found minutes in time string: {timeString} - {minutes}", timeString, minMatch.Groups[1].Value);
                timeSpan = timeSpan.Add(TimeSpan.FromMinutes(int.Parse(minMatch.Groups[1].Value)));
            }
            if (secMatch.Success)
                timeSpan = timeSpan.Add(TimeSpan.FromSeconds(int.Parse(secMatch.Groups[1].Value)));
            return timeSpan;
        }
    }
}