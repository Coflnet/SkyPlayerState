using System;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Provides formatting methods for prices and times used in task messages. Kept as an interface
/// (see <see cref="SimpleTaskFormatProvider"/>) so tests and SkyModCommands' richer
/// MinecraftSocketFormatProvider can supply their own implementation.
/// </summary>
public interface ITaskFormatProvider
{
    string FormatPrice(double price);
    string FormatTime(TimeSpan time);
}
