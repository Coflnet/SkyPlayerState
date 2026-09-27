using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Singleton holding all task definitions. Source of truth for
/// detection signatures, estimation metadata and task execution.
/// </summary>
public class TaskRegistry
{
    private readonly List<ProfitTask> tasks;
    private readonly Dictionary<string, ProfitTask> byName;

    public TaskRegistry()
    {
        tasks = TaskCatalog.Create().Values.Distinct().ToList();
        byName = tasks.ToDictionary(t => t is MethodTask method ? method.GetDetectionSignature().MethodName : t.Name,
            t => t, System.StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProfitTask> Tasks => tasks;
    public IEnumerable<MethodTask> MethodTasks => tasks.OfType<MethodTask>();

    /// <summary>
    /// Every registered task except hidden <see cref="MethodTask"/>s (see <see cref="MethodTask.Hidden"/>) -
    /// what every user-facing listing (<c>/Task/{player}/results</c> via <see cref="TaskExecutionService.ExecuteAll(string,System.Threading.CancellationToken)"/>)
    /// should enumerate instead of <see cref="Tasks"/>. Non-MethodTask tasks (Kat/Forge/Composter/...)
    /// have no hidden concept and always pass through.
    /// </summary>
    public IEnumerable<ProfitTask> PublicTasks => tasks.Where(t => t is not MethodTask method || !method.IsHidden);

    /// <summary>
    /// Every registered <see cref="MethodTask"/> except hidden ones - what <c>/Task/methods</c> and
    /// <see cref="TaskEstimator.EstimateAll"/> should enumerate instead of <see cref="MethodTasks"/>.
    /// </summary>
    public IEnumerable<MethodTask> PublicMethodTasks => MethodTasks.Where(t => !t.IsHidden);

    public ProfitTask GetByName(string name)
    {
        return byName.GetValueOrDefault(name);
    }
}
