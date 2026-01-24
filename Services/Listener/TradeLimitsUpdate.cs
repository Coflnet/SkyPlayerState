using System.Linq;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Legacy listener - replaced by CoinCounterListener
/// This is kept for backwards compatibility but does nothing
/// </summary>
public class TradeLimitsUpdate : UpdateListener
{
    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        // This listener has been replaced by CoinCounterListener
        // Keeping empty implementation for backwards compatibility
        return Task.CompletedTask;
    }
}
