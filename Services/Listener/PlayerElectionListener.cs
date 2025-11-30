using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

public partial class PlayerElectionListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Player Election")
            return;

        var items = args.msg.Chest.Items;
        if (items == null)
            return;

        // Parse the top 14 players and their votes
        var votes = ParseVotes(items);
        
        if (votes.Count > 0)
        {
            var timestamp = new DateTime(
                args.msg.ReceivedAt.Year,
                args.msg.ReceivedAt.Month,
                args.msg.ReceivedAt.Day,
                args.msg.ReceivedAt.Hour,
                args.msg.ReceivedAt.Minute,
                0,
                DateTimeKind.Utc);

            var service = args.GetService<IPlayerElectionService>();
            await service.StoreVotes(new PlayerElectionEntry
            {
                Timestamp = timestamp,
                Votes = votes
            });
        }

        // Parse the user's current vote
        var userVote = ParseUserVote(items);
        if (userVote != null)
        {
            args.currentState.ExtractedInfo ??= new ExtractedInfo();
            args.currentState.ExtractedInfo.PlayerElectionVote = userVote;
        }
    }

    /// <summary>
    /// Parses votes from the Player Election inventory items
    /// </summary>
    /// <param name="items">Items from the chest</param>
    /// <returns>Dictionary of player name to vote count</returns>
    public static Dictionary<string, int> ParseVotes(IEnumerable<Item> items)
    {
        var votes = new Dictionary<string, int>();
        
        foreach (var item in items)
        {
            if (item?.ItemName == null || item.Description == null)
                continue;

            // Skip navigation items, empty items, and UI elements
            if (item.ItemName.Contains("-->") || 
                item.ItemName.Contains("<--") || 
                item.ItemName == " " ||
                item.ItemName.Contains("Loading") ||
                item.ItemName.Contains("Close") ||
                item.ItemName.Contains("Minister Election") ||
                item.ItemName.Contains("Your current vote"))
                continue;

            // Check if this is a player entry with votes
            var voteMatch = VotesRegex().Match(item.Description);
            if (!voteMatch.Success)
                continue;

            var voteString = voteMatch.Groups[1].Value.Replace(",", "");
            if (!int.TryParse(voteString, out var voteCount))
                continue;

            // Extract player name (remove color codes)
            var playerName = ExtractPlayerName(item.ItemName);
            if (!string.IsNullOrEmpty(playerName))
            {
                votes[playerName] = voteCount;
            }
        }

        return votes;
    }

    /// <summary>
    /// Extracts the player name from a colored item name
    /// </summary>
    /// <param name="itemName">Item name with color codes like "§c[§fYOUTUBE§c] thirtyvirus"</param>
    /// <returns>Clean player name like "thirtyvirus"</returns>
    public static string ExtractPlayerName(string itemName)
    {
        if (string.IsNullOrEmpty(itemName))
            return string.Empty;

        // Remove all color codes (§ followed by any character)
        var cleaned = ColorCodeRegex().Replace(itemName, "");
        
        // The player name is typically after the rank in brackets
        // Examples: "[YOUTUBE] thirtyvirus", "[MVP++] 2nfg", "[MVP+] hannibal2"
        var bracketMatch = BracketRegex().Match(cleaned);
        if (bracketMatch.Success)
        {
            return bracketMatch.Groups[1].Value.Trim();
        }

        // If no bracket format, return the cleaned name
        return cleaned.Trim();
    }

    /// <summary>
    /// Parses the user's current vote from the "Your current vote" item
    /// </summary>
    /// <param name="items">Items from the chest</param>
    /// <returns>PlayerElectionVote with the voted player name and vote count, or null if not found</returns>
    public static PlayerElectionVote? ParseUserVote(IEnumerable<Item> items)
    {
        var voteItem = items.FirstOrDefault(i => 
            i?.ItemName?.Contains("Your current vote") ?? false);

        if (voteItem?.Description == null)
            return null;

        // Description format: "§7You have allocated §b1 §7votes towards\n§7§b[MVP§5+§b] hannibal2§7."
        var match = UserVoteRegex().Match(voteItem.Description);
        if (!match.Success)
            return null;

        var voteCount = int.Parse(match.Groups[1].Value);
        var playerNameWithColors = match.Groups[2].Value;
        var playerName = ExtractPlayerName(playerNameWithColors);

        return new PlayerElectionVote
        {
            VotedFor = playerName,
            VoteCount = voteCount
        };
    }

    [GeneratedRegex(@"Votes: §b([,\d]+)")]
    private static partial Regex VotesRegex();

    [GeneratedRegex(@"§.")]
    private static partial Regex ColorCodeRegex();

    [GeneratedRegex(@"\[[^\]]+\]\s*(.+)")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"You have allocated §b(\d+) §7votes towards\n§7(.+?)§7\.")]
    private static partial Regex UserVoteRegex();
}

/// <summary>
/// Represents a player election entry with timestamp rounded to minute
/// </summary>
public class PlayerElectionEntry
{
    public DateTime Timestamp { get; set; }
    public Dictionary<string, int> Votes { get; set; } = new();
}

/// <summary>
/// Service for storing and retrieving Player Election voting data
/// </summary>
public interface IPlayerElectionService
{
    Task StoreVotes(PlayerElectionEntry entry);
    Task<PlayerElectionEntry[]> GetVotingHistory();
}
