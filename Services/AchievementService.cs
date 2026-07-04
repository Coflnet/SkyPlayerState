using System;
using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;
using Prometheus;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Handles unlocking achievements on the player state.
/// The storage is the <see cref="StateObject.UnlockedAchievements"/> set which is persisted with the
/// rest of the player state, so unlocking simply mutates the (live) state object. Unlocks always run
/// on the instance that owns the players live state (routed there through the update pipeline), so the
/// change is never lost to a concurrent save on another replica.
/// </summary>
public interface IAchievementService
{
    /// <summary>
    /// Adds the achievement to the players unlocked set.
    /// </summary>
    /// <returns><c>true</c> if it was newly unlocked, <c>false</c> if it was already unlocked.</returns>
    bool Unlock(StateObject state, Achievement achievement);
    /// <summary>
    /// Returns the achievements the player has unlocked (never null).
    /// </summary>
    IReadOnlyCollection<Achievement> GetUnlocked(StateObject state);
}

/// <inheritdoc/>
public class AchievementService : IAchievementService
{
    private static readonly Counter unlockedCount = Metrics.CreateCounter(
        "sky_playerstate_achievement_unlocked", "How many achievements were newly unlocked, by achievement.",
        new CounterConfiguration { LabelNames = new[] { "achievement" } });

    /// <inheritdoc/>
    public bool Unlock(StateObject state, Achievement achievement)
    {
        if (state == null)
            return false;
        // states persisted before the UnlockedAchievements field existed deserialize it as null
        state.UnlockedAchievements ??= new HashSet<Achievement>();
        var added = state.UnlockedAchievements.Add(achievement);
        if (added)
            unlockedCount.WithLabels(achievement.ToString()).Inc();
        return added;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<Achievement> GetUnlocked(StateObject state)
    {
        return state?.UnlockedAchievements ?? (IReadOnlyCollection<Achievement>)Array.Empty<Achievement>();
    }
}
