using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Cassandra implementation of Player Election voting data storage
/// </summary>
public class PlayerElectionService : IPlayerElectionService
{
    private readonly Table<PlayerElectionDbEntry> _electionTable;

    public PlayerElectionService(ISession session)
    {
        var mapping = new MappingConfiguration().Define(
            new Map<PlayerElectionDbEntry>()
                .TableName("player_election_votes")
                .PartitionKey(x => x.Day)
                .ClusteringKey(x => x.Timestamp)
                .Column(x => x.Day, cm => cm.WithName("day"))
                .Column(x => x.Timestamp, cm => cm.WithName("timestamp"))
                .Column(x => x.Votes, cm => cm.WithName("votes"))
        );
        _electionTable = new Table<PlayerElectionDbEntry>(session, mapping);
        _electionTable.CreateIfNotExists();
    }

    public async Task StoreVotes(PlayerElectionEntry entry)
    {
        var dbEntry = new PlayerElectionDbEntry
        {
            Day = entry.Timestamp.Date,
            Timestamp = entry.Timestamp,
            Votes = entry.Votes
        };
        await _electionTable.Insert(dbEntry).ExecuteAsync();
    }

    public async Task<PlayerElectionEntry[]> GetVotingHistory()
    {
        // Get last 30 days of data - use IN clause for partition key
        var days = Enumerable.Range(0, 30)
            .Select(i => DateTime.UtcNow.Date.AddDays(-i))
            .ToList();
        
        var entries = await _electionTable
            .Where(x => days.Contains(x.Day))
            .ExecuteAsync();
        
        return entries
            .Select(e => new PlayerElectionEntry
            {
                Timestamp = e.Timestamp,
                Votes = e.Votes ?? new Dictionary<string, int>()
            })
            .OrderBy(e => e.Timestamp)
            .ToArray();
    }
}

/// <summary>
/// Database entity for player election entries
/// </summary>
public class PlayerElectionDbEntry
{
    public DateTime Day { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, int>? Votes { get; set; }
}
