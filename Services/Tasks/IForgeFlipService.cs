using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.Crafts.Client.Model;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Resolves the forge flips available to a player. <see cref="ForgeTask"/> and
/// <see cref="ForgeCraftTask"/> resolve this via <see cref="TaskParams.GetService{T}"/> rather than
/// depending on <see cref="ForgeFlipService"/> directly, so tests can substitute a mock.
/// </summary>
public interface IForgeFlipService
{
    Task<IEnumerable<ForgeFlip>> GetForgeFlips(TaskParams parameters);
}
