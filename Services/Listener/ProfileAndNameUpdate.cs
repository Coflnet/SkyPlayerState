using System.Threading.Tasks;
using Coflnet.Sky.PlayerName.Client.Api;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Detects profile and minecraft name/account changes and updates them
/// </summary>
public class ProfileAndNameUpdate : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        var state = args.currentState;
        if (state.McInfo.Uuid == default)
        {
            var nameService = args.GetService<IPlayerNameApi>();
            var uuid = await nameService.PlayerNameUuidNameGetAsync(args.msg.PlayerId);
            if (uuid != null)
            {
                try
                {
                    state.McInfo.Uuid = Guid.Parse(uuid.Trim('"'));
                }
                catch (Exception)
                {
                    Console.WriteLine($"could not parse uuid for {args.msg.PlayerId}: '{uuid}'");
                    throw;
                }
                state.McInfo.Name = args.msg.PlayerId;
            }
            else
                Console.WriteLine($"could not find uuid for {args.msg.PlayerId}");
        }
        // TODO find profile
    }
}
