using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Coflnet.Sky.PlayerState.Models;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Stores one row per accessory bag page
/// </summary>
public class AccessoryBagService
{
    private readonly Table<BagPageRow> table;

    public AccessoryBagService(ISession session)
    {
        var mapping = new MappingConfiguration().Define(new Map<BagPageRow>()
            .PartitionKey(t => t.PlayerUuid)
            .ClusteringKey(t => t.ProfileId)
            .ClusteringKey(t => t.Page)
            .Column(t => t.PlayerUuid, cm => cm.WithName("player_uuid"))
            .Column(t => t.ProfileId, cm => cm.WithName("profile_id"))
            .Column(t => t.Page, cm => cm.WithName("page"))
            .Column(t => t.PageCount, cm => cm.WithName("page_count"))
            .Column(t => t.UpdatedAt, cm => cm.WithName("updated_at"))
            .Column(t => t.Accessories, cm => cm.WithName("accessories"))
        );
        table = new Table<BagPageRow>(session, mapping, "accessory_bag");
        table.CreateIfNotExists();
    }

    /// <summary>
    /// Saves a page. Stale pages beyond a shrunken bag are not deleted (that would write a range tombstone on
    /// every page open), <see cref="Assemble"/> ignores them via the newest page count.
    /// </summary>
    public async Task SavePage(Guid playerUuid, Guid profileId, int page, int pageCount, List<BagAccessory> accessories)
    {
        await table.Insert(new BagPageRow
        {
            PlayerUuid = playerUuid,
            ProfileId = profileId,
            Page = page,
            PageCount = pageCount,
            UpdatedAt = DateTime.UtcNow,
            Accessories = JsonConvert.SerializeObject(accessories)
        }).ExecuteAsync();
    }

    /// <summary>
    /// Gets the stored bag of a profile, or of the most recently opened one when no profile is given.
    /// Never throws for missing data.
    /// </summary>
    public async Task<AccessoryBag> GetBag(Guid playerUuid, Guid? profileId)
    {
        var rows = (await table.Where(r => r.PlayerUuid == playerUuid).ExecuteAsync()).ToList();
        return Assemble(SelectProfile(rows, profileId));
    }

    /// <summary>
    /// Picks the rows of one profile: the requested one, else the unknown profile (Guid.Empty) rows,
    /// and without a request the profile whose bag was opened most recently.
    /// </summary>
    public static List<BagPageRow> SelectProfile(List<BagPageRow> rows, Guid? profileId)
    {
        var groups = rows.GroupBy(r => r.ProfileId).ToList();
        if (profileId == null)
            return groups.OrderByDescending(g => g.Max(r => r.UpdatedAt)).FirstOrDefault()?.ToList() ?? new();
        var requested = groups.FirstOrDefault(g => g.Key == profileId);
        return (requested ?? groups.FirstOrDefault(g => g.Key == Guid.Empty))?.ToList() ?? new();
    }

    /// <summary>
    /// Combines page rows into a bag, pages beyond the most recent page count are ignored
    /// </summary>
    public static AccessoryBag Assemble(List<BagPageRow> rows)
    {
        if (rows == null || rows.Count == 0)
            return new AccessoryBag();
        var newest = rows.OrderByDescending(r => r.UpdatedAt).First();
        var valid = rows.Where(r => r.Page <= newest.PageCount).OrderBy(r => r.Page).ToList();
        return new AccessoryBag
        {
            ProfileId = newest.ProfileId,
            PageCount = newest.PageCount,
            UpdatedAt = newest.UpdatedAt,
            SeenPages = valid.Select(r => r.Page).ToList(),
            Accessories = valid.SelectMany(r => JsonConvert.DeserializeObject<List<BagAccessory>>(r.Accessories) ?? new()).ToList()
        };
    }

    /// <summary>
    /// Removes the stored bags of all profiles of a player
    /// </summary>
    public async Task DeleteBag(Guid playerUuid)
    {
        await table.Where(r => r.PlayerUuid == playerUuid).Delete().ExecuteAsync();
    }

    public class BagPageRow
    {
        public Guid PlayerUuid { get; set; }
        public Guid ProfileId { get; set; }
        public int Page { get; set; }
        public int PageCount { get; set; }
        public DateTime UpdatedAt { get; set; }
        /// <summary>JSON of the page's <see cref="BagAccessory"/> list</summary>
        public string Accessories { get; set; }
    }
}
