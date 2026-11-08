using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services.Gear;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Reads the rate relevant stats, the pest count and the pet from the tab list widgets (see <see cref="TabStatsParser"/>)
/// into <see cref="ExtractedInfo.LiveStats"/> and <see cref="ExtractedInfo.LivePet"/>, and adds each reading to the
/// running task session's stat sums, a secondary source: observations use <see cref="ExtractedInfo.BaseStats"/> plus the
/// session's gear when a "Stats &amp; Equipment" read exists. Registered before
/// <see cref="CollectionListener"/>, so a session flushed by this tab update already contains the sample.
/// Stores no player names: only whitelisted stat names are kept.
/// </summary>
public class TabStatsListener : UpdateListener
{
    /// <summary>Optional enrichment, a failure must not stop the state save.</summary>
    public override bool Optional => true;

    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Tab == null)
            return Task.CompletedTask;
        var info = args.currentState.ExtractedInfo;
        var now = args.msg.ReceivedAt == default ? DateTime.UtcNow : args.msg.ReceivedAt;
        var reading = TabStatsParser.Parse(args.msg.Tab, info.CurrentSession?.DetectedTask ?? info.CurrentTask);
        ApplyPet(info, reading.Pet, reading.PetName, now);
        var values = TabStatsParser.SampleValues(reading);
        if (values.Count == 0)
            return Task.CompletedTask;
        info.LiveStats = values;
        info.LiveStatsAt = now;
        GearSessionTracker.AddStatSample(info.CurrentSession, values, now);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The tab pet confirms the gear's pet (refreshing <see cref="GearSnapshot.PetAt"/>) or replaces it after a swap. The tab
    /// only has the display name: a Pets menu entry of the same level and name supplies the real petInfo type, the tier
    /// and the held item, and a gear pet whose type spells the display name is confirmed, not replaced.
    /// </summary>
    internal static void ApplyPet(ExtractedInfo info, GearPet? live, string? displayName, DateTime now)
    {
        if (live == null)
            return;
        var pet = ResolveFromPetsMenu(info, live, displayName);
        info.LivePet = pet;
        var gear = info.CurrentGear ??= new GearSnapshot();
        if (gear.Pet != null && IsSamePet(gear.Pet, pet, displayName))
            Confirm(gear.Pet, pet);
        else
            gear.Pet = new GearPet(pet);
        gear.PetAt = now;
    }

    private static void Confirm(GearPet known, GearPet live)
    {
        known.Level = live.Level;
        known.Tier ??= live.Tier;
        known.HeldItem ??= live.HeldItem;
    }

    private static bool IsSamePet(GearPet known, GearPet live, string? displayName)
        => known.Type == live.Type || (displayName != null && Letters(known.Type) == Letters(displayName));

    private static string Letters(string text) => new(text.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());

    private static GearPet ResolveFromPetsMenu(ExtractedInfo info, GearPet live, string? displayName)
    {
        var known = info.Pets?.FirstOrDefault(p => p.Level == live.Level && MatchesName(p, live, displayName));
        if (known?.Type == null)
            return live;
        return new GearPet { Type = known.Type.ToUpperInvariant(), Level = live.Level, Tier = known.Tier?.ToUpperInvariant(), HeldItem = known.HeldItem };
    }

    private static bool MatchesName(ExtractedInfo.PetState menu, GearPet live, string? displayName)
    {
        if (string.Equals(menu.Type, live.Type, StringComparison.OrdinalIgnoreCase))
            return true;
        return displayName != null && menu.Name != null && menu.Name.EndsWith(displayName, StringComparison.OrdinalIgnoreCase);
    }
}
