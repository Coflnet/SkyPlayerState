using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Builds the default player facing description of a method task from data the task already declares:
/// its name, where it is done and its requirements. Drops are left out on purpose, the website shows
/// them with resolved item names. Tasks with a hand written description override
/// <see cref="ProfitTask.Description"/> and never reach this builder.
/// </summary>
public static class TaskDescriptionBuilder
{
    /// <summary>"(T1)", "(F3)", "(M7)": the tier tag scoreboard zones carry.</summary>
    private static readonly Regex TierTag = new(@"\s*\([A-Z]\d+\)", RegexOptions.Compiled);
    /// <summary>Any trailing "(...)", like "(Hunting)" or "(FD)" in task names.</summary>
    private static readonly Regex TrailingBrackets = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    public static string Build(string methodName, string where, string island, IEnumerable<TaskRequirement> requirements, string category = null)
    {
        var text = new StringBuilder(Summary(methodName, where, island, category));
        AppendList(text, "Requires:", requirements, hard: true);
        AppendList(text, "Recommended:", requirements, hard: false);
        return text.ToString();
    }

    private static string Summary(string methodName, string where, string island, string category)
    {
        var place = TierTag.Replace(where ?? "", "").Trim();
        var isTaskName = place == TrailingBrackets.Replace(methodName, "").Trim();
        if (place.Length > 0 && !isTaskName)
            return $"{methodName} at {place}{IslandSuffix(place, island)}.";
        var located = $"{methodName}{IslandSuffix(methodName + " " + place, island)}.";
        return located != $"{methodName}." || string.IsNullOrWhiteSpace(category) || category == "Other"
            ? located
            : $"{methodName}, a {category.ToLowerInvariant()} task.";
    }

    private static string IslandSuffix(string place, string island) =>
        string.IsNullOrWhiteSpace(island) || place.Contains(island) ? "" : $", {island}";

    private static void AppendList(StringBuilder text, string prefix, IEnumerable<TaskRequirement> requirements, bool hard)
    {
        var labels = (requirements ?? []).Where(r => r.Hard == hard && !string.IsNullOrEmpty(r.Label)).Select(r => r.Label).ToList();
        if (labels.Count == 0)
            return;
        text.Append($" {prefix} {string.Join(", ", labels)}.");
    }
}
