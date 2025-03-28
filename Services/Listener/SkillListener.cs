using System.Linq;
using System.Threading.Tasks;
using Cassandra.Data.Linq;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.PlayerState.Services;

public class SkillListener : UpdateListener
{
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Chest.Name == null || args.msg.Chest.Name != "Your Skills")
            return;
        var items = args.msg.Chest.Items;
        System.Collections.Generic.List<SkillService.Skill> skills = ParseSkills(items);
        foreach (var skill in skills)
        {
            await args.GetService<SkillService>().StoreSkill(args.currentState.McInfo.Uuid, skill.Name, skill.Level);
        }
    }

    public System.Collections.Generic.List<SkillService.Skill> ParseSkills(System.Collections.Generic.List<Models.Item> items)
    {
        return items.Skip(2 * 9).Take(2 * 9).Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).Select(i => ParseSkill(i)).ToList();
    }

    private SkillService.Skill ParseSkill(Models.Item item)
    {
        var parts = item.ItemName.Split(' ');
        if (parts.Length < 2)
            return new SkillService.Skill() { Name = item.ItemName.Replace("§a", ""), Level = 0 };
        var level = Roman.From(parts.Last());
        var name = string.Join(' ', parts.Take(parts.Length - 1)).Replace("§a", "");
        return new() { Name = name, Level = level };
    }
}
