using Coflnet.Sky.PlayerState.Services;

namespace Coflnet.Sky.PlayerState.Bazaar;

/// <summary>Publishes per-order bazaar lifecycle observations. Implementations must never throw.</summary>
public interface IBazaarOrderLifecycleProducer
{
    void Emit(UpdateArgs args, Offer offer, string source, string state, string itemTagOverride = null);
}
