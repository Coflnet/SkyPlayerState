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
