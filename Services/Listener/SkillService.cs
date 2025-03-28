using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;


public class SkillService
{
    private readonly ILogger<SkillService> logger;
    private readonly Table<Skill> skillTable;

    public SkillService(ISession session, ILogger<SkillService> logger)
    {
        var mapping = new MappingConfiguration().Define(
            new Map<Skill>()
                .TableName("skills")
                .PartitionKey(x => x.Player)
                .ClusteringKey(x => x.Name)
                .Column(x => x.Player, cm => cm.WithName("player"))
                .Column(x => x.Name, cm => cm.WithName("name"))
                .Column(x => x.Level, cm => cm.WithName("level"))
        );
        skillTable = new Table<Skill>(session, mapping);
        skillTable.CreateIfNotExists();
        this.logger = logger;
    }
    public async Task StoreSkill(Guid playerId, string skillName, int level)
    {
        logger.LogInformation("Storing skill {skill} with level {level} for player {player}", skillName, level, playerId);
        await skillTable.Insert(new Skill { Player = playerId, Name = skillName, Level = level }).ExecuteAsync();
    }

    public async Task<Skill[]> GetSkills(Guid playerId)
    {
        return (await skillTable.Where(x => x.Player == playerId).ExecuteAsync()).ToArray();
    }

    public class Skill
    {
        public Guid Player { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
    }
}
