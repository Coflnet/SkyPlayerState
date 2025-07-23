using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.PlayerState.Services;

public class AttributeMenuListener : UpdateListener
{
    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        var chestView = args.msg.Chest;
        if (chestView.Name != "Attribute Menu")
            return;
        var shards = chestView.Items.Take(54).Where(i => i?.Description?.Contains("Enabled:") ?? false).ToList();
        var count = shards.Select(s =>
        {
            var fullName = s.ItemName;
            var levelRomanString = fullName!.Split(' ').Last();
            var name = fullName.Substring(2, fullName.Length - levelRomanString.Length - 3).Trim();
            var parsed = Roman.From(levelRomanString);
            return (name, parsed);
        });
        foreach (var item in count)
        {
            args.currentState.ExtractedInfo.AttributeLevel ??= [];
            args.currentState.ExtractedInfo.AttributeLevel[item.name] = item.parsed;
        }
    }
}