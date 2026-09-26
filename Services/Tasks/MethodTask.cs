using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.MC;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Represents an expected item drop for formula-based profit estimation
/// </summary>
public record MethodDrop(string ItemTag, double RatePerHour);

/// <summary>
/// Base class for method-specific profit tasks that detect which activity
/// the player is doing based on items collected and location.
/// Falls back to formula-based estimation when no player data exists.
/// </summary>
public abstract class MethodTask : ProfitTask
{
    protected abstract string MethodName { get; }
    protected virtual HashSet<string> Locations => [];
    /// <summary>
    /// Items that must be present to attribute a period to this method.
    /// If empty, matching is location-only (plus shard flags).
    /// </summary>
    protected virtual HashSet<string> DetectionItems => [];
    /// <summary>
    /// When true, periods with any SHARD_ item are excluded.
    /// Used for non-hunting fishing variants.
    /// </summary>
    protected virtual bool ExcludeShardItems => false;
    /// <summary>
    /// When true, periods must contain at least one SHARD_ item.
    /// Used for hunting fishing variants where specific shard is unknown.
    /// </summary>
    protected virtual bool RequireShardItems => false;
    /// <summary>
    /// Expected item drops per hour for formula-based estimation
    /// when the player has no tracked data.
    /// </summary>
    protected virtual List<MethodDrop> FormulaDrops => [];
    /// <summary>
    /// Items consumed per hour by the method (e.g. gemstones fed into a Forge recipe), subtracted
    /// from <see cref="FormulaDrops"/> revenue in the formula-based estimate. Kept separate from
    /// <see cref="FormulaDrops"/> (rather than a negative rate on a drop) so drops always represent
    /// what the player gains and the plausibility tests can keep requiring positive drop rates.
    /// </summary>
    protected virtual List<MethodDrop> FormulaCosts => [];
    /// <summary>
    /// Name of another task whose detection this task shares/derives from (e.g. "Sludge Mining
    /// (Gem Mixture)" derives from "Sludge Mining": Gemstone Mixture is a Forge recipe, not a
    /// Jungle drop, so requiring its own DetectionItems to show up there would never fire - see
    /// TaskClassifier and TaskPeriodFolder). When set, this task is excluded from the classifier's
    /// own candidate list (it never wins a tie against its primary) but is still marked as "being
    /// done" whenever the primary is classified, and receives its own community/personal estimate
    /// converted from the primary's observed rate via <see cref="ConvertDerivedCounts"/>.
    /// Null (default) means this task detects independently.
    /// </summary>
    protected virtual string DerivedFrom => null;
    /// <summary>
    /// Public accessor for tests/classifier to read <see cref="DerivedFrom"/>.
    /// </summary>
    public string DerivedFromForTest => DerivedFrom;
    /// <summary>
    /// Converts item counts observed on the primary task (<see cref="DerivedFrom"/>) over
    /// <paramref name="hours"/> into this derived task's own item counts, so its community
    /// aggregate/personal stats can be folded without ever needing its own DetectionItems to be
    /// observed directly. Only invoked when <see cref="DerivedFrom"/> is set. Returning null/empty
    /// means no derived contribution is folded for this period.
    /// </summary>
    protected virtual Dictionary<string, double> ConvertDerivedCounts(Dictionary<string, double> primaryItemCounts, double hours) => null;
    /// <summary>
    /// Public accessor for tests/TaskPeriodFolder to call <see cref="ConvertDerivedCounts"/>.
    /// </summary>
    public Dictionary<string, double> ConvertDerivedCountsForTest(Dictionary<string, double> primaryItemCounts, double hours)
        => ConvertDerivedCounts(primaryItemCounts, hours);

    /// <summary>
    /// This task's <see cref="FormulaCosts"/> ingredient cost, scaled to how much conversion
    /// activity <paramref name="itemCounts"/> (absolute counts observed/assumed over
    /// <paramref name="hours"/>) actually represents relative to this task's own design-time
    /// <see cref="FormulaDrops"/> rate for the same item - so cost is charged proportional to what
    /// was actually produced instead of always the full per-hour formula cost. The reference item is
    /// the first <see cref="FormulaDrops"/> entry also present in <paramref name="itemCounts"/> (for
    /// <see cref="SludgeMiningGemMixtureTask"/> that is GEMSTONE_MIXTURE - FormulaCosts was written
    /// against its MixtureRate). <paramref name="priceOf"/> lets each caller use its own price
    /// lookup (<see cref="CoinValueRegistry"/>'s drift-corrected value for folds, the raw live price
    /// dict for the formula tiers) so this stays a pure rate/scale calculation.
    /// <para>
    /// Shared by <see cref="TaskPeriodFolder.FoldDerivedTasks"/> (a derived task's real, per-window
    /// <see cref="ConvertDerivedCounts"/> output) and <see cref="TaskEstimator"/>'s formula baseline
    /// (itemCounts = FormulaDrops themselves, hours = 1, so scale is always 1) - <see
    /// cref="ComputeFromFormula"/> already nets its own itemized cost breakdown inline and is left
    /// as is, but is mathematically the scale-1 case of this same idea.
    /// </para>
    /// </summary>
    public double ScaledFormulaCost(Dictionary<string, double> itemCounts, double hours, Func<string, double> priceOf)
    {
        if (FormulaCosts.Count == 0 || hours <= 0 || itemCounts == null || itemCounts.Count == 0)
            return 0;
        var reference = FormulaDrops.FirstOrDefault(d => d.RatePerHour > 0 && itemCounts.ContainsKey(d.ItemTag));
        if (reference == null)
            return 0;
        var scale = (itemCounts[reference.ItemTag] / hours) / reference.RatePerHour;
        return FormulaCosts.Sum(c => priceOf(c.ItemTag) * c.RatePerHour) * scale * hours;
    }

    protected override string WarpCommand => null;
    /// <summary>
    /// Preferred time window in hours for averaging.
    /// Data is searched within this window first, then extended
    /// progressively up to 96h if too few samples exist.
    /// </summary>
    protected virtual double PreferredWindowHours => 3;

    // ── Extended method metadata ──

    /// <summary>
    /// Step-by-step explanation of how to do this method
    /// </summary>
    protected virtual string HowTo => $"Go to the relevant location and perform {MethodName}.";
    /// <summary>
    /// Items required to get started with this method (tag, name, reason)
    /// </summary>
    protected virtual List<RequiredItem> RequiredItems => [];
    /// <summary>
    /// Estimated actions per hour (kills, catches, mines, etc.)
    /// </summary>
    protected virtual double ActionsPerHour => 0;
    /// <summary>
    /// Name of the action unit (kills, catches, runs, mines)
    /// </summary>
    protected virtual string ActionUnit => "actions";
    /// <summary>
    /// Equipment/effects that can improve drop rates or speed
    /// </summary>
    protected virtual List<DropEffect> Effects => [];
    /// <summary>
    /// Category for grouping in the API (Fishing, Mining, Slayer, etc.)
    /// </summary>
    protected virtual string Category => "Other";
    /// <summary>
    /// Task type: Active (default), Passive (setup+wait), Limited (daily/cooldown)
    /// </summary>
    protected virtual TaskType TaskType => TaskType.Active;
    /// <summary>
    /// Check whether this task is currently accessible. Return null if accessible,
    /// or a reason string if not (e.g. "Only available during Diana mayor", "Rain Slimes only spawn :00-:20")
    /// </summary>
    protected virtual string CheckAccessibility(TaskParams parameters) => null;
    protected virtual DateTime? GetNextAvailableAt(TaskParams parameters) => null;

    // ── "Where do I go / what do I click" metadata (helpful task list) ──
    // WikiUrl/Island/WhereWikiUrl/EffectiveWarpCommand/PopulateGuidanceFields are inherited as-is
    // from ProfitTask; only Where and Steps get MethodTask-specific (Locations/HowTo-aware) defaults.

    /// <summary>
    /// The exact zone to stand in to do this method. Defaults to the first declared Location; some
    /// tasks (mainly the top earners) override this with a more specific spot the default can't know
    /// (e.g. "at the Forge" for a crafting step).
    /// </summary>
    protected override string Where => Locations.FirstOrDefault();
    /// <summary>
    /// Falls back to the island's own wiki page (always resolvable once Where/Island do - see
    /// SkyblockZones.IslandInfo) so every task has *some* wiki link even without per-task curation;
    /// specific tasks (Sludge, gemstones, ...) override this with a page about the method/item itself.
    /// </summary>
    protected override string WikiUrl => WhereWikiUrl;

    /// <summary>
    /// Ordered, followable steps a total beginner ("could a 6 year old follow this?") can click
    /// through to actually do the method. Every task gets a reasonable default built from the
    /// structured data above (gear, warp, where, the HowTo text, what to collect, what to sell);
    /// override for the top earners and anything where the generic wording reads wrong.
    /// </summary>
    protected override List<TaskStep> Steps => BuildDefaultSteps();

    private List<TaskStep> BuildDefaultSteps()
    {
        var steps = new List<TaskStep>();
        void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });

        if (RequiredItems.Count > 0)
        {
            // One step per item (not one combined sentence) so each item can carry its own
            // buy click and price - see SkyModCommands' TaskDetailsCommand.BuildStepByStep, which
            // matches a step's text against RequiredItems to attach a bazaar/AH click.
            foreach (var req in RequiredItems.Take(3))
            {
                var label = string.IsNullOrEmpty(req.Name) ? req.ItemTag : req.Name;
                var reasonPart = string.IsNullOrEmpty(req.Reason) ? "" : $" ({req.Reason})";
                Add($"Get {label} first{reasonPart}.");
            }
        }

        var warp = EffectiveWarpCommand;
        if (warp != null)
            Add($"Type {warp} to get close.", warp);

        if (Where != null)
        {
            var islandLabel = Island != null && Island != Where ? $" in {Island}" : "";
            Add($"Go to {Where}{islandLabel}.", WhereWikiUrl);
        }

        foreach (var sentence in SplitIntoSentences(HowTo))
            Add(sentence);

        if (DetectionItems.Count > 0)
            Add($"You're doing it right when you start collecting: {string.Join(", ", DetectionItems.Take(4))}. That's what we track for your coins/hour.");

        if (FormulaDrops.Count > 0)
        {
            var sellItems = string.Join(", ", FormulaDrops.OrderByDescending(d => d.RatePerHour).Take(3).Select(d => d.ItemTag));
            Add($"Sell {sellItems} on the Bazaar or Auction House.");
        }

        Add("Check /cofl task again to see your real coins/hour once you've done this a bit.");
        return steps;
    }

    private static IEnumerable<string> SplitIntoSentences(string howTo)
    {
        if (string.IsNullOrWhiteSpace(howTo))
            yield break;
        foreach (var raw in howTo.Split('.'))
        {
            var sentence = raw.Trim();
            if (sentence.Length > 0)
                yield return sentence + ".";
        }
    }

    /// <summary>
    /// Public accessor for tests to check if this task has formula drops
    /// </summary>
    public List<MethodDrop> FormulaDropsForTest => FormulaDrops;
    /// <summary>
    /// Public accessor for tests to check if this task has formula costs
    /// </summary>
    public List<MethodDrop> FormulaCostsForTest => FormulaCosts;

    /// <summary>
    /// Tie break priority when multiple tasks match the same location and items,
    /// higher wins. Only relevant for classification.
    /// </summary>
    protected virtual int Priority => 0;

    /// <summary>
    /// Which player stats affect the rates of this task. Used to group data from
    /// players with similar stats. Empty means rates are not stat conditioned and
    /// all data lands in the unknown bucket.
    /// </summary>
    public virtual List<StatFactor> StatFactors => [];

    /// <summary>
    /// Detection signature used by the classifier to attribute periods to this task.
    /// Exposes the same rules <see cref="FindMatchingPeriods"/> applies.
    /// </summary>
    public DetectionSignature GetDetectionSignature() => new(
        MethodName, Locations, DetectionItems, RequireShardItems, ExcludeShardItems, Priority, Category, DerivedFrom);

    /// <summary>
    /// Estimated multipliers from the declared effects, used to seed the
    /// per stat-bucket prior in the estimator.
    /// </summary>
    public List<double> EffectMultipliersForEstimate =>
        Effects?.Select(e => e.EstimatedMultiplier).Where(m => m > 0).ToList() ?? [];

    public override string Description => $"Calculates profit for {MethodName}";

    public override async Task<TaskResult> Execute(TaskParams parameters)
    {
        var accessibilityIssue = CheckAccessibility(parameters);

        var matchedPeriods = FindMatchingPeriodsWindowed(parameters);

        // Looked up once (MethodName, falling back to the class-derived Name - see
        // InfernoDemonlordUsesLegacyBurningsoulServerEstimate) so both the server-estimate branch
        // and the formula fallback below can see it: TaskEstimator.EstimateAll returns an estimate
        // for every task, including Source == "formula" ones whenever their FormulaDrops have
        // prices, so a plain CoinsPerHour > 0 check used to route those into
        // ComputeFromServerEstimate too - which has no per-drop/per-cost breakdown and mislabels a
        // pure formula number as "community_estimate"/"(estimated)". A formula-sourced estimate must
        // fall through to ComputeFromFormula instead, which renders the full drops+costs breakdown -
        // see the stat-aware rescale in ComputeFromFormula for how its own per-player accuracy is
        // still preserved.
        TaskEstimate serverEstimate = null;
        parameters.ServerEstimates?.TryGetValue(MethodName, out serverEstimate);
        if (serverEstimate == null)
            parameters.ServerEstimates?.TryGetValue(Name, out serverEstimate);

        TaskResult result;
        if (matchedPeriods.Count > 0)
            result = await ComputeFromPlayerData(parameters, matchedPeriods);
        else if (serverEstimate != null && serverEstimate.CoinsPerHour > 0 && serverEstimate.Source != "formula")
            result = await ComputeFromServerEstimate(parameters, serverEstimate);
        else if (parameters.GlobalAverageDrops != null
                 && (parameters.GlobalAverageDrops.TryGetValue(MethodName, out var avgDrops)
                     || parameters.GlobalAverageDrops.TryGetValue(Name, out avgDrops))
                 && avgDrops.Count > 0)
            result = await ComputeFromGlobalAverage(parameters, avgDrops);
        else if (FormulaDrops.Count > 0)
            result = await ComputeFromFormula(parameters, serverEstimate?.Source == "formula" ? serverEstimate : null);
        else
            result = new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"No {MethodName} data tracked yet.",
                Details = $"Do some {MethodName} so we can calculate profit.",
                Name = MethodName
            };

        result.Type = TaskType;
        result.MostlyPassive = TaskType == TaskType.Passive;
        result.NextAvailableAt ??= GetNextAvailableAt(parameters);
        if (accessibilityIssue != null)
        {
            result.IsAccessible = false;
            result.InaccessibleReason = accessibilityIssue;
        }
        return result;
    }

    /// <summary>
    /// Find matching periods using progressive time windows.
    /// Starts at PreferredWindowHours and extends to 96h if insufficient data.
    /// </summary>
    protected List<Period> FindMatchingPeriodsWindowed(TaskParams parameters)
    {
        var allMatched = FindMatchingPeriods(parameters);
        if (allMatched.Count == 0)
            return allMatched;

        var now = parameters.TestTime;
        double[] windows = [PreferredWindowHours, 12, 24, 48, 96];

        foreach (var windowHours in windows)
        {
            var cutoff = now.AddHours(-windowHours);
            var inWindow = allMatched.Where(p => p.EndTime >= cutoff).ToList();
            var totalHours = inWindow.Sum(p => (p.EndTime - p.StartTime).TotalHours);
            // Need at least 1 period and 5 minutes of data
            if (inWindow.Count >= 1 && totalHours >= 5.0 / 60)
                return inWindow;
        }

        // Fall back to all available data
        return allMatched;
    }

    /// <summary>
    /// Public accessor for aggregation — returns all matching periods without windowing.
    /// </summary>
    public List<Period> FindMatchingPeriodsForAggregation(TaskParams parameters)
        => FindMatchingPeriods(parameters);

    protected List<Period> FindMatchingPeriods(TaskParams parameters)
    {
        IEnumerable<Period> candidates = Locations.Count > 0
            ? parameters.LocationProfit
                .Where(lp => SkyblockZones.Matches(Locations, lp.Key))
                .SelectMany(lp => lp.Value)
            : parameters.LocationProfit.SelectMany(lp => lp.Value);

        if (DetectionItems.Count > 0)
            candidates = candidates.Where(p =>
                p.ItemsCollected != null &&
                p.ItemsCollected.Keys.Any(k => DetectionItems.Contains(k)));

        if (RequireShardItems)
            candidates = candidates.Where(p =>
                p.ItemsCollected != null &&
                p.ItemsCollected.Keys.Any(k => k.StartsWith("SHARD_")));

        if (ExcludeShardItems)
            candidates = candidates.Where(p =>
                p.ItemsCollected == null ||
                !p.ItemsCollected.Keys.Any(k => k.StartsWith("SHARD_")));

        return candidates.ToList();
    }

    protected Task<TaskResult> ComputeFromPlayerData(TaskParams parameters, List<Period> periods)
    {
        var totalProfit = periods.Sum(p => (double)p.Profit);
        var totalHours = periods.Sum(p => (p.EndTime - p.StartTime).TotalHours);

        if (totalHours <= 0)
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"{MethodName} sessions too short to estimate.",
                Name = MethodName
            });

        var perHour = totalProfit / totalHours;
        var items = periods
            .Where(p => p.ItemsCollected != null)
            .SelectMany(p => p.ItemsCollected)
            .GroupBy(i => i.Key)
            .ToDictionary(g => g.Key, g => (long)g.Sum(v => v.Value))
            .OrderByDescending(i => i.Value);

        var fmt = parameters.Formatter;
        var formattedDuration = fmt.FormatTime(TimeSpan.FromHours(totalHours));
        var itemCount = items.Where(i => i.Value > 0).Sum(i => i.Value);
        var itemBreakDown = items.Take(20)
            .Select(i => $"{McColorCodes.YELLOW}{i.Key} {McColorCodes.GRAY}x{i.Value}")
            .DefaultIfEmpty("No items tracked")
            .Aggregate((a, b) => a + "\n" + b);

        var prices = parameters.GetPrices();
        var drops = items.Take(20).Select(i => new DropInfo
        {
            ItemTag = i.Key,
            Name = parameters.Names.GetValueOrDefault(i.Key, i.Key),
            RatePerHour = totalHours > 0 ? i.Value / totalHours : 0,
            PriceEach = prices.GetValueOrDefault(i.Key, 0),
            ContributionPerHour = totalHours > 0 ? i.Value / totalHours * prices.GetValueOrDefault(i.Key, 0) : 0
        }).ToList();

        var reqItems = BuildRequiredItems(parameters);

        var result = new TaskResult
        {
            ProfitPerHour = (int)perHour,
            Message = $"{MethodName} with {McColorCodes.AQUA}{fmt.FormatPrice(totalProfit)} {McColorCodes.GRAY}({McColorCodes.GREEN}{itemCount} items{McColorCodes.GRAY}) over {formattedDuration}.",
            Details = $"Time tracked: {formattedDuration}\nItems collected:\n{itemBreakDown}",
            Name = MethodName,
            OnClick = WarpCommand,
            Breakdown = new MethodBreakdown
            {
                HowTo = HowTo,
                RequiredItems = reqItems,
                Drops = drops,
                ActionsPerHour = ActionsPerHour > 0 ? ActionsPerHour : (totalHours > 0 ? itemCount / totalHours : 0),
                ActionUnit = ActionUnit,
                Effects = Effects,
                Source = "player_data",
                TrackedHours = totalHours,
                Category = Category,
                Type = TaskType
            }
        };
        PopulateGuidanceFields(result.Breakdown);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Cold-start estimate from this task's own declared <see cref="FormulaDrops"/>/<see
    /// cref="FormulaCosts"/> at current prices, with the full itemized drops+costs breakdown.
    /// <paramref name="statAdjustedEstimate"/> is the matching <see cref="TaskEstimate"/> when
    /// <see cref="TaskEstimator"/> also priced this task's formula tier (<c>Source == "formula"</c> -
    /// see <see cref="Execute"/>): that figure applies a stat-bucket prior seeded from this task's
    /// own <see cref="Effects"/> (mining speed/fortune, gauntlet, hotm tier, ...) plus live
    /// saturation - real per-player adjustments this flat recipe math has no way to know about on
    /// its own - so whenever it is available, drops/costs/totals are rescaled together (keeping the
    /// recipe's cost:output ratio intact) to match it instead of the flat design-time numbers.
    /// </summary>
    protected Task<TaskResult> ComputeFromFormula(TaskParams parameters, TaskEstimate statAdjustedEstimate = null)
    {
        var prices = parameters.GetPrices();
        var fmt = parameters.Formatter;
        var totalPerHour = 0.0;
        var drops = new List<DropInfo>();

        foreach (var drop in FormulaDrops)
        {
            var price = prices.GetValueOrDefault(drop.ItemTag, 0);
            if (price <= 0) continue;
            var contribution = drop.RatePerHour * price;
            totalPerHour += contribution;
            drops.Add(new DropInfo
            {
                ItemTag = drop.ItemTag,
                Name = parameters.Names.GetValueOrDefault(drop.ItemTag, drop.ItemTag),
                RatePerHour = drop.RatePerHour,
                PriceEach = price,
                ContributionPerHour = contribution
            });
        }

        if (totalPerHour <= 0)
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"{MethodName} - price data unavailable.",
                Name = MethodName
            });

        var costs = new List<DropInfo>();
        var totalCostPerHour = 0.0;
        foreach (var cost in FormulaCosts)
        {
            var price = prices.GetValueOrDefault(cost.ItemTag, 0);
            if (price <= 0) continue;
            var contribution = cost.RatePerHour * price;
            totalCostPerHour += contribution;
            costs.Add(new DropInfo
            {
                ItemTag = cost.ItemTag,
                Name = parameters.Names.GetValueOrDefault(cost.ItemTag, cost.ItemTag),
                RatePerHour = cost.RatePerHour,
                PriceEach = price,
                ContributionPerHour = -contribution
            });
        }
        var netPerHour = totalPerHour - totalCostPerHour;

        if (statAdjustedEstimate != null && statAdjustedEstimate.CoinsPerHour > 0 && netPerHour > 0)
        {
            var scale = statAdjustedEstimate.CoinsPerHour / netPerHour;
            foreach (var drop in drops) { drop.RatePerHour *= scale; drop.ContributionPerHour *= scale; }
            foreach (var cost in costs) { cost.RatePerHour *= scale; cost.ContributionPerHour *= scale; }
            netPerHour = statAdjustedEstimate.CoinsPerHour;
        }

        var breakdown = drops.Select(d =>
            $"{McColorCodes.YELLOW}{d.Name} {McColorCodes.GRAY}x{d.RatePerHour:F0}/h = {McColorCodes.AQUA}{fmt.FormatPrice((long)d.ContributionPerHour)}");
        var costBreakdown = costs.Select(c =>
            // c.ContributionPerHour is already negative (see the doc comment on MethodBreakdown.Costs) -
            // FormatPrice adds its own "-" for a negative value, so no separate "-" prefix here.
            $"{McColorCodes.YELLOW}{c.Name} {McColorCodes.GRAY}x{c.RatePerHour:F1}/h = {McColorCodes.RED}{fmt.FormatPrice((long)c.ContributionPerHour)}").ToList();

        var reqItems = BuildRequiredItems(parameters);
        var costSection = costBreakdown.Count > 0
            ? $"\nEstimated ingredient costs per hour:\n{string.Join("\n", costBreakdown)}"
            : "";

        var formulaResult = new TaskResult
        {
            ProfitPerHour = (int)netPerHour,
            Message = $"{MethodName} ~{McColorCodes.AQUA}{fmt.FormatPrice((long)netPerHour)}/h {McColorCodes.GRAY}(estimated)",
            Details = $"Estimated drops per hour:\n{string.Join("\n", breakdown)}{costSection}\n{McColorCodes.DARK_GRAY}(Do this method for personalized tracking)",
            Name = MethodName,
            OnClick = WarpCommand,
            Breakdown = new MethodBreakdown
            {
                HowTo = HowTo,
                RequiredItems = reqItems,
                Drops = drops,
                Costs = costs,
                ActionsPerHour = ActionsPerHour,
                ActionUnit = ActionUnit,
                Effects = Effects,
                Source = "formula",
                TrackedHours = 0,
                Category = Category,
                Type = TaskType
            }
        };
        PopulateGuidanceFields(formulaResult.Breakdown);
        return Task.FromResult(formulaResult);
    }

    protected Task<TaskResult> ComputeFromGlobalAverage(TaskParams parameters, List<AverageDrop> avgDrops)
    {
        var prices = parameters.GetPrices();
        var totalPerHour = 0.0;
        var breakdown = new List<string>();
        var drops = new List<DropInfo>();
        var fmt = parameters.Formatter;
        var totalSamples = avgDrops.Max(d => d.SampleCount);

        foreach (var drop in avgDrops)
        {
            var price = prices.GetValueOrDefault(drop.ItemTag, 0);
            if (price <= 0) continue;
            var contribution = drop.RatePerHour * price;
            totalPerHour += contribution;
            var name = parameters.Names.GetValueOrDefault(drop.ItemTag, drop.ItemTag);
            breakdown.Add($"{McColorCodes.YELLOW}{name} {McColorCodes.GRAY}x{drop.RatePerHour:F0}/h = {McColorCodes.AQUA}{fmt.FormatPrice((long)contribution)}");
            drops.Add(new DropInfo
            {
                ItemTag = drop.ItemTag,
                Name = name,
                RatePerHour = drop.RatePerHour,
                PriceEach = price,
                ContributionPerHour = contribution
            });
        }

        if (totalPerHour <= 0)
            return Task.FromResult(new TaskResult
            {
                ProfitPerHour = 0,
                Message = $"{MethodName} - price data unavailable.",
                Name = MethodName
            });

        var reqItems = BuildRequiredItems(parameters);

        var avgResult = new TaskResult
        {
            ProfitPerHour = (int)totalPerHour,
            Message = $"{MethodName} ~{McColorCodes.AQUA}{fmt.FormatPrice((long)totalPerHour)}/h {McColorCodes.GRAY}(avg of {totalSamples} players)",
            Details = $"Average drops per hour (community data):\n{string.Join("\n", breakdown)}\n{McColorCodes.DARK_GRAY}(Do this method for personalized tracking)",
            Name = MethodName,
            OnClick = WarpCommand,
            Breakdown = new MethodBreakdown
            {
                HowTo = HowTo,
                RequiredItems = reqItems,
                Drops = drops,
                ActionsPerHour = ActionsPerHour,
                ActionUnit = ActionUnit,
                Effects = Effects,
                Source = "global_average",
                TrackedHours = 0,
                Category = Category,
                Type = TaskType
            }
        };
        PopulateGuidanceFields(avgResult.Breakdown);
        return Task.FromResult(avgResult);
    }

    /// <summary>
    /// Build a result from the SkyUserState stat aware estimate. The heavy lifting (personal
    /// blending, stat buckets, saturation, price drift, AND ingredient cost netting for recipe
    /// tasks like Sludge Mining (Gem Mixture) - see TaskEstimator.RateFromAggregate/
    /// RateFromPlayerStat/FormulaRate) already happened server side, so estimate.CoinsPerHour
    /// arrives here already net; render it as-is. Never called for a Source == "formula" estimate -
    /// see Execute(), which falls through to ComputeFromFormula for those instead.
    /// </summary>
    protected Task<TaskResult> ComputeFromServerEstimate(TaskParams parameters, TaskEstimate estimate)
    {
        var fmt = parameters.Formatter;
        var drops = (estimate.Drops ?? []).Select(d => new DropInfo
        {
            ItemTag = d.ItemTag,
            Name = parameters.Names.GetValueOrDefault(d.ItemTag, d.ItemTag),
            RatePerHour = d.RatePerHour,
            PriceEach = d.PriceEach,
            ContributionPerHour = d.ContributionPerHour
        }).ToList();

        // estimate.CoinsPerHour is already net of ingredient cost (netted once, upstream, in
        // TaskEstimator - see the doc comment above). Build a Costs list purely for display, scaled
        // to THIS estimate's own observed drop rates (not the design-time FormulaDrops rate) so a
        // player/community producing more or less than the formula assumes sees a proportional
        // figure - but do NOT subtract it again: gross drops (above) minus these display costs is
        // already what CoinsPerHour represents.
        var (costs, _) = BuildScaledCosts(
            drops.ToDictionary(d => d.ItemTag, d => d.RatePerHour, StringComparer.OrdinalIgnoreCase), parameters);
        var netCoinsPerHour = estimate.CoinsPerHour;

        var sourceLabel = estimate.Source switch
        {
            "personal" => "your tracked data",
            "stat_bucket" => "players with similar stats",
            "global" => "community data",
            _ => "estimated"
        };
        var saturationNote = estimate.CurrentDoers > 1
            ? $" {McColorCodes.GRAY}({estimate.CurrentDoers} doing this now)"
            : "";
        var costNote = costs.Count > 0
            ? $"\nEstimated ingredient costs per hour:\n{string.Join("\n", costs.Select(c =>
                $"{McColorCodes.YELLOW}{c.Name} {McColorCodes.GRAY}x{c.RatePerHour:F1}/h = {McColorCodes.RED}{fmt.FormatPrice((long)c.ContributionPerHour)}"))}\n"
            : "";

        var serverResult = new TaskResult
        {
            ProfitPerHour = (int)netCoinsPerHour,
            Message = $"{MethodName} ~{McColorCodes.AQUA}{fmt.FormatPrice(netCoinsPerHour)}/h {McColorCodes.GRAY}({sourceLabel}){saturationNote}",
            Details = $"Based on {sourceLabel}.\n"
                + (estimate.PersonalTrackedMinutes > 0 ? $"Your tracked time: {fmt.FormatTime(TimeSpan.FromMinutes(estimate.PersonalTrackedMinutes))}\n" : "")
                + (estimate.Contributors > 0 ? $"{McColorCodes.DARK_GRAY}{estimate.Contributors} players contributed data\n" : "")
                + costNote
                + (estimate.TraceId != null ? $"{McColorCodes.DARK_GRAY}ref {estimate.TraceId}" : ""),
            Name = MethodName,
            OnClick = WarpCommand,
            Breakdown = new MethodBreakdown
            {
                HowTo = HowTo,
                RequiredItems = BuildRequiredItems(parameters),
                Drops = drops,
                Costs = costs,
                ActionsPerHour = ActionsPerHour,
                ActionUnit = ActionUnit,
                Effects = Effects,
                Source = estimate.Source == "personal" ? "player_data" : "community_estimate",
                TrackedHours = estimate.CommunityTrackedHours,
                Category = Category,
                Type = TaskType
            }
        };
        PopulateGuidanceFields(serverResult.Breakdown);
        return Task.FromResult(serverResult);
    }

    /// <summary>
    /// This task's <see cref="FormulaCosts"/> scaled to <paramref name="itemRatesPerHour"/> - the
    /// task's own observed/estimated per-hour output (e.g. a server estimate's drops) rather than
    /// the design-time <see cref="FormulaDrops"/> rate, so a player/community producing more or less
    /// than the formula assumes sees a proportional figure (same idea as <see
    /// cref="ScaledFormulaCost"/>, which this reuses - hours = 1 since <paramref
    /// name="itemRatesPerHour"/> are already per-hour rates, not absolute counts). Used by <see
    /// cref="ComputeFromServerEstimate"/> for DISPLAY ONLY: the estimate's CoinsPerHour is already
    /// net of this cost (see TaskEstimator), so the returned <c>TotalPerHour</c> must not be
    /// subtracted from it again there. Returns an empty list/0 when this task has no <see
    /// cref="FormulaCosts"/> or no priced reference drop.
    /// </summary>
    private (List<DropInfo> Costs, double TotalPerHour) BuildScaledCosts(Dictionary<string, double> itemRatesPerHour, TaskParams parameters)
    {
        var costs = new List<DropInfo>();
        if (FormulaCosts.Count == 0 || itemRatesPerHour == null || itemRatesPerHour.Count == 0)
            return (costs, 0);
        var prices = parameters.GetPrices();
        double PriceOf(string tag) => prices.GetValueOrDefault(tag, 0);
        var totalPerHour = ScaledFormulaCost(itemRatesPerHour, 1.0, PriceOf);
        if (totalPerHour <= 0)
            return (costs, 0);
        var reference = FormulaDrops.FirstOrDefault(d => d.RatePerHour > 0 && itemRatesPerHour.ContainsKey(d.ItemTag));
        var scale = reference == null ? 0 : itemRatesPerHour[reference.ItemTag] / reference.RatePerHour;
        foreach (var cost in FormulaCosts)
        {
            var price = PriceOf(cost.ItemTag);
            if (price <= 0) continue;
            var rate = cost.RatePerHour * scale;
            costs.Add(new DropInfo
            {
                ItemTag = cost.ItemTag,
                Name = parameters.Names.GetValueOrDefault(cost.ItemTag, cost.ItemTag),
                RatePerHour = rate,
                PriceEach = price,
                ContributionPerHour = -(rate * price)
            });
        }
        return (costs, totalPerHour);
    }

    private List<RequiredItem> BuildRequiredItems(TaskParams parameters)
    {
        var prices = parameters.GetPrices();
        return RequiredItems.Select(r => new RequiredItem
        {
            ItemTag = r.ItemTag,
            Name = string.IsNullOrEmpty(r.Name) ? parameters.Names.GetValueOrDefault(r.ItemTag, r.ItemTag) : r.Name,
            Reason = r.Reason,
            EstimatedPrice = (long)prices.GetValueOrDefault(r.ItemTag, 0)
        }).ToList();
    }
}
