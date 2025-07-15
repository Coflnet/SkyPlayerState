using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

namespace Coflnet.Sky.PlayerState.Services;

public class TrackedProfitService
{
    Table<Period> locationPeriods;
    Table<Period> historyPeriods;
    private readonly ISession session;

    public TrackedProfitService(ISession session)
    {
        this.session = session;
    }

    private async Task Setup()
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<Period>()
                .PartitionKey(t => t.PlayerUuid)
                .ClusteringKey(t => t.Location)
            );
        var historyMapping = new MappingConfiguration()
            .Define(new Map<Period>()
                .PartitionKey(t => t.PlayerUuid)
                .ClusteringKey(t => t.EndTime, SortOrder.Descending)
            );
        locationPeriods = new Table<Period>(session, mapping, "locationPeriods");
        historyPeriods = new Table<Period>(session, mapping, "historyPeriods");
        var TABLE_NAME_HISTORY = "historyPeriods";
        var keeptime = 1209600 / 2; // 1 week in seconds
        var compactionResult = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT compaction, default_time_to_live FROM system_schema.tables WHERE keyspace_name = '{session.Keyspace}' AND table_name = '{TABLE_NAME_HISTORY.ToLower()}';"));
        var row = compactionResult.FirstOrDefault();
        bool needsUpdate = true;
        if (row != null)
        {
            var compaction = row.GetValue<IDictionary<string, string>>("compaction");
            var ttl = row.GetValue<int?>("default_time_to_live");
            if (compaction != null && compaction.TryGetValue("class", out var compactionClass))
            {
                if (compactionClass.Contains("TimeWindowCompactionStrategy") && ttl == keeptime)
                {
                    needsUpdate = false;
                }
            }
        }
        if (needsUpdate)
        {
            await session.ExecuteAsync(new SimpleStatement(
                $"ALTER TABLE {TABLE_NAME_HISTORY} WITH compaction = {{'class': 'TimeWindowCompactionStrategy', 'compaction_window_size': '1', 'compaction_window_unit': 'DAYS'}} AND default_time_to_live = {keeptime};"));
        }
    }

    public async Task AddPeriod(Period period)
    {
        if (locationPeriods == null || historyPeriods == null)
            await Setup();
        period.StartTime = period.StartTime.ToUniversalTime();
        period.EndTime = period.EndTime.ToUniversalTime();
        await locationPeriods!.Insert(period).ExecuteAsync();
        await historyPeriods!.Insert(period).ExecuteAsync();
    }

    public async Task<List<Period>> GetPeriodsForPlayer(string playerUuid, string location)
    {
        if (locationPeriods == null || historyPeriods == null)
            await Setup();
        return (await locationPeriods.Where(p => p.PlayerUuid == playerUuid && p.Location == location).ExecuteAsync()).ToList();
    }

    public async Task<List<Period>> GetHistoryForPlayer(string playerUuid, DateTime? before = null, int count = 50)
    {
        if (locationPeriods == null || historyPeriods == null)
            await Setup();
        if (before.HasValue)
        return (await historyPeriods.Where(p => p.PlayerUuid == playerUuid && p.EndTime < before.Value.ToUniversalTime())
            .Take(count)
            .ExecuteAsync()).ToList();
        else
        {
            return (await historyPeriods.Where(p => p.PlayerUuid == playerUuid)
                .Take(count)
                .ExecuteAsync()).ToList();
        }
    }


    public class Period
    {
        public string PlayerUuid { get; set; }
        public string Server { get; set; }
        public string Location { get; set; }
        public long Profit { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public Dictionary<string, int> ItemsCollected { get; set; } = new();
    }
}
