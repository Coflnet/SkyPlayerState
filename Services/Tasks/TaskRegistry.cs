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
        byName = tasks.ToDictionary(t => t.Name, t => t, System.StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProfitTask> Tasks => tasks;
    public IEnumerable<MethodTask> MethodTasks => tasks.OfType<MethodTask>();

    public ProfitTask GetByName(string name)
    {
        return byName.GetValueOrDefault(name);
    }
}
