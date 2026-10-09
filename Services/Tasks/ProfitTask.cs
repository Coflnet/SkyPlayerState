using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public abstract class ProfitTask
{
    public abstract Task<TaskResult> Execute(TaskParams parameters);

    /// <summary>
    /// What the player must have before this task is doable (declared in order of progression: the first
    /// known unmet hard one is named in the inaccessible reason). Island and zone requirements come
    /// automatically from <see cref="AreaRequirements"/> and need not be repeated here.
    /// </summary>
    protected virtual List<TaskRequirement> Requirements => [];
    /// <summary>The zones the task is done in, used for the automatic area requirements. Defaults to <see cref="Where"/>.</summary>
    protected virtual IEnumerable<string> AreaZones => string.IsNullOrEmpty(Where) ? [] : [Where];

    /// <summary>Area requirements first (get there), then the task's own, duplicates merged.</summary>
    public List<TaskRequirement> AllRequirements =>
        RequirementEvaluator.Merge(AreaRequirements.ForZones(AreaZones).Concat(Requirements));

    /// <summary>
    /// <see cref="Execute"/> plus the requirement check against <see cref="TaskParams.ProfileSnapshot"/>: fills
    /// <see cref="MethodBreakdown.Requirements"/> and marks the task inaccessible when a hard requirement is
    /// known unmet. This is the entry point the task list uses.
    /// </summary>
    public async Task<TaskResult> ExecuteWithRequirements(TaskParams parameters)
    {
        var result = await Execute(parameters);
        var requirements = AllRequirements;
        // results without data ("No X tracked yet", formula without prices) have no breakdown to carry them
        if (requirements.Count > 0 && result.Breakdown == null)
            result.Breakdown = NewGuidanceBreakdown();
        RequirementEvaluator.Apply(result, requirements, parameters);
        return result;
    }
    /// <summary>
    /// Rewards that arrive after the work that earned them (Galatea contest coupons, a slayer boss's
    /// drops): the tags, the time earning them takes and a display label; null (default) = none. A
    /// period holding only these items is not work time: when it follows this task's work (see
    /// <see cref="LateRewardAttribution"/>, stored as the period's DetectedTask) its value is added to the
    /// task without its own seconds; when a method task detects it itself (a slayer boss-only period) it counts
    /// for at least <see cref="LateRewardSpec.EarnDuration"/>, never for the raw seconds. Works for every
    /// task base class (method tasks and island trackers alike).
    /// </summary>
    protected virtual LateRewardSpec LateReward => null;
    /// <summary>Public accessor for the listener/tests - see <see cref="LateReward"/>.</summary>
    public LateRewardSpec LateRewardDeclaration => LateReward;

    /// <summary>The name this task is registered (and stored as a period's DetectedTask) under.</summary>
    public virtual string RegistryName => Name;

    /// <summary>True when the period holds nothing but this task's <see cref="LateReward"/> items.</summary>
    public bool IsLateRewardOnly(Period period) => LateReward?.IsOnlyReward(period.ItemsCollected) == true;

    /// <summary>
    /// A reward-only period the listener credited to this task (DetectedTask set, see
    /// <see cref="LateRewardAttribution"/>). It adds value but no time.
    /// </summary>
    public virtual bool IsAttributedLateReward(Period period)
        => IsLateRewardOnly(period)
           && string.Equals(period.DetectedTask, RegistryName, StringComparison.OrdinalIgnoreCase);

    public abstract string Description { get; }
    public virtual string Name => GetType().Name.Replace("Task", "");

    // ── "Where do I go / what do I click" metadata shared by every task in /cofl task, not just
    // MethodTask (IslandTask, IndividualSlayerTask, Kat/Forge/Composter/crafting tasks all inherit
    // this directly from ProfitTask). MethodTask overrides Where/WarpCommand/Steps with its own
    // richer, DetectionItems/FormulaDrops-aware defaults - see MethodTask.cs.

    /// <summary>
    /// Wiki page explaining the method or its main item (e.g. Sludge Mining -&gt;
    /// https://hypixelskyblock.minecraft.wiki/w/Sludge_Juice). Curate and verify (curl -sIL,
    /// expect 200) per task; defaults to null (no page emitted) rather than guessing a slug that
    /// might not resolve. See SkyblockZones.WikiPageUrl for the URL builder.
    /// </summary>
    protected virtual string WikiUrl => null;
    public string WikiUrlForTest => WikiUrl;
    /// <summary>
    /// The exact zone to stand in to do this task. Null by default (no fixed location, e.g. Kat/Forge);
    /// IslandTask/MethodTask/IndividualSlayerTask override with their own location list.
    /// </summary>
    protected virtual string Where => null;
    public string WhereForTest => Where;
    /// <summary>The SkyBlock island <see cref="Where"/> belongs to, or null if unknown (see SkyblockZones).</summary>
    protected string Island => SkyblockZones.IslandOf(Where);
    public string IslandForTest => Island;
    /// <summary>Wiki page for <see cref="Island"/> (has the island's map), or null if unknown.</summary>
    protected string WhereWikiUrl => SkyblockZones.IslandWikiUrl(Island);
    public string WhereWikiUrlForTest => WhereWikiUrl;
    /// <summary>This task's own declared warp, if any - see <see cref="EffectiveWarpCommand"/>.</summary>
    protected virtual string WarpCommand => null;
    /// <summary>This task's own <see cref="WarpCommand"/> if set, else the island's warp, else null.</summary>
    protected string EffectiveWarpCommand => WarpCommand ?? SkyblockZones.WarpCommandFor(Island);
    public string EffectiveWarpCommandForTest => EffectiveWarpCommand;

    /// <summary>
    /// Ordered, followable steps a total beginner ("could a 6 year old follow this?") can click
    /// through to actually do the task. The base default is a minimal warp/where/description
    /// fallback for tasks with no richer structured data (Kat, Forge, Composter, crafting);
    /// MethodTask/IslandTask/IndividualSlayerTask override with data-aware defaults, and specific
    /// top-earner tasks override further with hand-written steps.
    /// </summary>
    protected virtual List<TaskStep> Steps => BuildGenericDefaultSteps();
    public List<TaskStep> StepsForTest => Steps;

    private List<TaskStep> BuildGenericDefaultSteps()
    {
        var steps = new List<TaskStep>();
        void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });

        var warp = EffectiveWarpCommand;
        if (warp != null)
            Add($"Type {warp} to get close.", warp);
        if (Where != null)
        {
            var islandLabel = Island != null && Island != Where ? $" in {Island}" : "";
            Add($"Go to {Where}{islandLabel}.", WhereWikiUrl);
        }
        Add(Description);
        Add("Check /cofl task again to see your real coins/hour.");
        return steps;
    }

    /// <summary>Fills in the Where/Island/WikiUrl/Warp/Steps fields shared by every result path.</summary>
    protected void PopulateGuidanceFields(MethodBreakdown breakdown)
    {
        breakdown.WikiUrl = WikiUrl;
        breakdown.Where = Where;
        breakdown.Island = Island;
        breakdown.WhereWikiUrl = WhereWikiUrl;
        breakdown.Warp = EffectiveWarpCommand;
        breakdown.Steps = Steps;
    }

    /// <summary>
    /// Convenience for tasks that don't build their own MethodBreakdown (Kat/Forge/Composter/
    /// crafting/slayer/island tasks) - a fresh breakdown with just the guidance fields filled in,
    /// so every returned TaskResult carries Where/WikiUrl/Steps even for their "no data yet" /
    /// "not possible right now" early-return paths.
    /// </summary>
    protected MethodBreakdown NewGuidanceBreakdown(string category = "Other", string source = "formula")
    {
        var breakdown = new MethodBreakdown { Category = category, Source = source };
        PopulateGuidanceFields(breakdown);
        return breakdown;
    }

    protected async Task<T> GetOrUpdateCache<T>(TaskParams parameters, Func<Task<T>> factory, float maxageMinutes = 5) where T : new()
    {
        if (parameters.Cache.TryGetValue(typeof(T), out var cache) && cache.LastUpdated > DateTime.UtcNow.AddMinutes(-maxageMinutes))
        {
            return (T)cache.Data;
        }
        var newData = await factory();
        parameters.Cache[typeof(T)] = new TaskParams.CalculationCache { Data = newData, LastUpdated = DateTime.UtcNow };
        return newData;
    }
}
