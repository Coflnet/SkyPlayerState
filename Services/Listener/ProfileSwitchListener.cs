using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Tracks which SkyBlock profile the player is on, from the "Profile ID" / "You are playing on profile" chat lines
/// (printed on every server join) and the "Profile: &lt;Name&gt;" tab line. When only a name is known and it maps to
/// no known profile the id is reset to Guid.Empty so per profile data is never saved to the wrong profile.
/// </summary>
public class ProfileSwitchListener : UpdateListener
{
    private static readonly Regex ColorCodes = new("§.", RegexOptions.Compiled);
    private static readonly Regex IdLine = new(@"^Profile ID:\s*([0-9a-fA-F-]{32,36})\s*$", RegexOptions.Compiled);
    // names are fruits, a trailing symbol (ironman, stranded, bingo) is not part of the name
    private static readonly Regex ChatNameLine = new(@"^You are playing on profile:\s*([A-Za-z]+)", RegexOptions.Compiled);
    private static readonly Regex TabNameLine = new(@"^Profile:\s*([A-Za-z]+)", RegexOptions.Compiled);

    /// <summary>Enrichment only, a failure must not drop the update</summary>
    public override bool Optional => true;

    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        var state = args.currentState;
        if (args.msg.Kind == UpdateMessage.UpdateKind.CHAT)
            ApplyChat(state, args.msg.ChatBatch ?? []);
        else if (args.msg.Kind == UpdateMessage.UpdateKind.Tab)
            ApplyTabName(state, ParseTabProfileName(args.msg.Tab ?? []));
        return Task.CompletedTask;
    }

    /// <summary>Applies chat lines in order, the name line precedes the id line of the same join</summary>
    public static void ApplyChat(StateObject state, IEnumerable<string> lines)
    {
        string? pendingName = null;
        foreach (var line in lines)
        {
            var name = ParseChatProfileName(line);
            if (name != null)
            {
                pendingName = name;
                ApplyTabName(state, name);
                continue;
            }
            var id = ParseProfileId(line);
            if (id != null)
                ApplyProfileId(state, id.Value, pendingName);
        }
    }

    private static void ApplyProfileId(StateObject state, Guid id, string? name)
    {
        var info = state.ExtractedInfo;
        name ??= state.Profiles.FirstOrDefault(p => p.Uuid == id)?.Name;
        name ??= info.CurrentProfileId == id ? info.CurrentProfileName : null;
        info.CurrentProfileId = id;
        info.CurrentProfileName = name;
        if (!string.IsNullOrEmpty(name))
            UpsertProfile(state.Profiles, id, name);
    }

    /// <summary>Updates the name of a known profile or adds it</summary>
    public static void UpsertProfile(List<Profile> profiles, Guid id, string name)
    {
        var existing = profiles.FirstOrDefault(p => p.Uuid == id);
        if (existing == null)
            profiles.Add(new Profile { Uuid = id, Name = name });
        else
            existing.Name = name;
    }

    private static void ApplyTabName(StateObject state, string? tabName)
    {
        var info = state.ExtractedInfo;
        if (AdoptNameForKnownId(state, tabName))
            return;
        var switched = DecideSwitch(info.CurrentProfileName, tabName, state.Profiles);
        if (switched == null)
            return;
        info.CurrentProfileId = switched.Value.id;
        info.CurrentProfileName = switched.Value.name;
    }

    /// <summary>
    /// The id is known from chat but its name is not yet: the first reported name belongs to that profile
    /// (unless another known profile has that name), so it must not reset the id
    /// </summary>
    private static bool AdoptNameForKnownId(StateObject state, string? reportedName)
    {
        var info = state.ExtractedInfo;
        if (string.IsNullOrEmpty(reportedName) || info.CurrentProfileId == Guid.Empty || !string.IsNullOrEmpty(info.CurrentProfileName))
            return false;
        var otherHasName = state.Profiles.Any(p => p.Uuid != info.CurrentProfileId
            && string.Equals(p.Name, reportedName, StringComparison.OrdinalIgnoreCase));
        if (otherHasName)
            return false;
        info.CurrentProfileName = reportedName;
        UpsertProfile(state.Profiles, info.CurrentProfileId, reportedName);
        return true;
    }

    /// <summary>
    /// Decides whether a reported profile name means a switch. Null if nothing changes, otherwise the new
    /// (id, name): the id of the known profile with that name or Guid.Empty when it is unknown.
    /// </summary>
    public static (Guid id, string name)? DecideSwitch(string? currentName, string? reportedName, IEnumerable<Profile> known)
    {
        if (string.IsNullOrEmpty(reportedName)
            || string.Equals(reportedName, currentName, StringComparison.OrdinalIgnoreCase))
            return null;
        var match = known.FirstOrDefault(p => string.Equals(p.Name, reportedName, StringComparison.OrdinalIgnoreCase));
        return (match?.Uuid ?? Guid.Empty, reportedName);
    }

    /// <summary>Parses "Profile ID: &lt;uuid&gt;" (color codes allowed), null for anything else</summary>
    public static Guid? ParseProfileId(string? line)
    {
        var match = IdLine.Match(Clean(line));
        if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out var id) || id == Guid.Empty)
            return null;
        return id;
    }

    /// <summary>Parses "You are playing on profile: Banana" (also with a trailing symbol), null otherwise</summary>
    public static string? ParseChatProfileName(string? line)
    {
        var match = ChatNameLine.Match(Clean(line));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Finds the "Profile: Banana" tab line, null if there is none</summary>
    public static string? ParseTabProfileName(IEnumerable<string?> tab)
    {
        foreach (var line in tab)
        {
            var match = TabNameLine.Match(Clean(line));
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    private static string Clean(string? line) => line == null ? "" : ColorCodes.Replace(line, "").Trim();
}
