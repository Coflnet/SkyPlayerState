using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coflnet.Sky.PlayerState.Services;

public abstract class UpdateListener
{
    /// <summary>
    /// Logger for this listener, assigned when the listener is registered.
    /// Defaults to a no-op logger so it is always safe to use.
    /// </summary>
    protected ILogger Logger { get; private set; } = NullLogger.Instance;

    /// <summary>
    /// Assigns the logger used by this listener. Called once during registration.
    /// </summary>
    internal void SetLogger(ILogger logger) => Logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Optional listeners enrich the state but nothing downstream depends on them. When one throws
    /// its failure is logged and counted but the update keeps processing the remaining handlers and
    /// still persists the state - so a bug in an optional enrichment can never stop a player's whole
    /// state from being saved. Core listeners (default) keep the all-or-nothing retry/drop behaviour.
    /// </summary>
    public virtual bool Optional => false;

    /// <summary>
    /// Process an update
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public abstract Task Process(UpdateArgs args);
    /// <summary>
    /// Called when registering to do async loading stuff
    /// </summary>
    public virtual Task Load(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }
}
