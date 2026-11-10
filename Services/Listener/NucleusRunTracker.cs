using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Tracks the Crystal Nucleus run of a player from chat (<see cref="NucleusChatParser"/>) on
/// <see cref="ExtractedInfo.NucleusRun"/>: a run starts with the first crystal found after the previous claim and ends with
/// the loot bundle claim, which remembers the run's start for attributing the hunt to it. Crystals stay found across
/// sessions, so a run is only believed while it is fresh (<see cref="MaxEvidenceGap"/>, <see cref="MaxRunLength"/>) and on
/// the profile it started on; a claim without such a run keeps no start (the attribution has to fall back to a bound).
/// </summary>
public static class NucleusRunTracker
{
    /// <summary>
    /// A run with no crystal found or placed for this long is abandoned. A full run takes 6 to 20 minutes (real log: first
    /// crystal 21:06:43, bundle 21:13:12, the next run's first crystal 3 minutes later), the crystals of a run are minutes
    /// apart; an hour without one means the player is doing something else and only kept the crystals.
    /// </summary>
    public static readonly TimeSpan MaxEvidenceGap = TimeSpan.FromHours(1);

    /// <summary>
    /// A run started longer ago than this is abandoned however often crystals turn up: commission grinders find crystals
    /// every now and then, and hours of their grinding are not one nucleus run. Also bounds how much Crystal Hollows play
    /// one claim can take over from the other tasks.
    /// </summary>
    public static readonly TimeSpan MaxRunLength = TimeSpan.FromHours(2);

    /// <summary>The bundle banner is printed once per claim; a second one this soon is the same line sent again.</summary>
    public static readonly TimeSpan DuplicateClaimWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Applies the nucleus chat lines of one batch, all received at <paramref name="at"/> (the message time, the clock of
    /// the run's evidence) and processed at <paramref name="processedAt"/> (default <paramref name="at"/>): the clock of the
    /// location fragments, which the claim is compared against to find its bundle loot (see <see cref="NucleusRunState.ClaimProcessedAt"/>).
    /// </summary>
    public static void Apply(ExtractedInfo info, IEnumerable<string> lines, DateTime at, DateTime? processedAt = null)
    {
        foreach (var line in lines)
        {
            var kind = NucleusChatParser.Parse(line, out var crystalsFound);
            if (kind == NucleusChatEvent.None)
                continue;
            info.NucleusRun ??= new NucleusRunState();
            Apply(info.NucleusRun, kind, crystalsFound, info.CurrentProfileName, at, processedAt ?? at);
        }
    }

    /// <summary>
    /// True while <paramref name="run"/> has a start that is recent (<see cref="MaxEvidenceGap"/>, <see cref="MaxRunLength"/>)
    /// and belongs to <paramref name="profile"/>.
    /// </summary>
    public static bool IsActive(NucleusRunState? run, string? profile, DateTime now)
    {
        if (run == null || run.Start == default)
            return false;
        if (now - run.LastEvidenceAt > MaxEvidenceGap || now - run.Start > MaxRunLength)
            return false;
        return SameProfile(run.Profile, profile);
    }

    /// <summary>
    /// Only two known, different names are a switch: the name is unknown for a while after a login, which must not
    /// abandon a run.
    /// </summary>
    internal static bool SameProfile(string? runProfile, string? current)
        => string.IsNullOrEmpty(runProfile) || string.IsNullOrEmpty(current)
            || string.Equals(runProfile, current, StringComparison.OrdinalIgnoreCase);

    private static void Apply(NucleusRunState run, NucleusChatEvent kind, int crystalsFound, string? profile, DateTime at, DateTime processedAt)
    {
        if (run.Start != default && !IsActive(run, profile, at))
            ResetRun(run);
        switch (kind)
        {
            case NucleusChatEvent.CrystalFound:
                OnCrystalFound(run, crystalsFound, profile, at);
                break;
            case NucleusChatEvent.CrystalPlaced:
                OnCrystalPlaced(run, at);
                break;
            case NucleusChatEvent.BundleClaimed:
                OnBundleClaimed(run, at, processedAt);
                break;
        }
    }

    /// <summary>
    /// The first crystal (1/5) always starts a run, a missed claim must not stretch the previous one. A later crystal
    /// without a run starts one too: the earlier crystals predate the tracking or the gap, so this is a lower bound.
    /// </summary>
    private static void OnCrystalFound(NucleusRunState run, int crystalsFound, string? profile, DateTime at)
    {
        if (crystalsFound == 1 || run.Start == default)
        {
            run.Start = at;
            run.Profile = profile;
        }
        run.CrystalsFound = crystalsFound;
        run.LastEvidenceAt = at;
    }

    /// <summary>Placing happens at the end of a run, so it keeps a run alive but never starts one.</summary>
    private static void OnCrystalPlaced(NucleusRunState run, DateTime at)
    {
        if (run.Start != default)
            run.LastEvidenceAt = at;
    }

    /// <summary>
    /// Remembers the claim with its run's start (default when unknown) and ends the run. The run's sessions are merged
    /// later, by <see cref="Tasks.NucleusRunRouter"/>, once the bundle loot is in.
    /// </summary>
    private static void OnBundleClaimed(NucleusRunState run, DateTime at, DateTime processedAt)
    {
        if (run.ClaimAt != default && at - run.ClaimAt < DuplicateClaimWindow)
            return;
        if (run.ClaimPending)
            AddPendingClaim(run, at, processedAt);
        else
            StartPendingClaim(run, at, processedAt);
        ResetRun(run);
    }

    private static void StartPendingClaim(NucleusRunState run, DateTime at, DateTime processedAt)
    {
        run.ClaimAt = at;
        run.ClaimProcessedAt = processedAt;
        run.ClaimedRunStart = run.Start;
        run.ClaimPending = true;
        run.PendingClaims = 1;
        run.BundleSeen = false;
    }

    /// <summary>
    /// A second claim before the first was closed (no fragment was processed in between): both runs are closed as one
    /// period counting two runs. The first run's start is kept, the anchor moves on so the second bundle is waited for too.
    /// </summary>
    private static void AddPendingClaim(NucleusRunState run, DateTime at, DateTime processedAt)
    {
        run.ClaimAt = at;
        run.ClaimProcessedAt = processedAt;
        run.PendingClaims = Math.Max(1, run.PendingClaims) + 1;
        run.BundleSeen = false;
    }

    private static void ResetRun(NucleusRunState run)
    {
        run.Start = default;
        run.Profile = null;
        run.LastEvidenceAt = default;
        run.CrystalsFound = 0;
    }
}
