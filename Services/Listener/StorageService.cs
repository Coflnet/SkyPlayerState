using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using Cassandra.Data.Linq;
using Cassandra.Mapping;
using Coflnet.Sky.PlayerState.Models;
using MessagePack;
using Newtonsoft.Json;

namespace Coflnet.Sky.PlayerState.Services;

public class StorageService
{
    static MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
    private Table<StorageItem> storageTable;

    public StorageService(ISession session)
    {
        var mapping = new MappingConfiguration().Define(new Map<StorageItem>()
            .PartitionKey(t => t.PlayerId, t => t.ProfileId)
            .ClusteringKey(t => t.ChestName)
            .ClusteringKey(t => t.Position)
            .Column(t => t.ChestName, cm => cm.WithName("chest_name"))
            .Column(t => t.SerializedItems, cm => cm.WithName("serialized_items"))
            .Column(t => t.SerializedPosition, cm => cm.WithName("serialized_position"))
            .Column(t => t.OpenedAt, cm => cm.WithName("opened_at"))
            .Column(t => t.Items, cm => cm.Ignore())
            .Column(t => t.Position, cm => cm.Ignore())
        );
        storageTable = new Table<StorageItem>(session, mapping, "player_storage");
        storageTable.CreateIfNotExists();
    }

    public async Task SaveStorageItem(StorageItem item)
    {
        await storageTable.Insert(item).ExecuteAsync();
    }

    public async Task<List<StorageItem>> GetStorageItems(Guid playerId, Guid profileId)
    {
        return (await storageTable.Where(i => i.PlayerId == playerId && i.ProfileId == profileId).ExecuteAsync()).ToList();
    }

    public class StorageItem
    {
        public Guid PlayerId { get; set; }
        public Guid ProfileId { get; set; }
        public string ChestName { get; set; }
        [JsonIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public byte[] SerializedItems { get; set; }
        [JsonIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SerializedPosition { get; set; }
        public List<Item> Items
        {
            get => MessagePackSerializer.Deserialize<List<Item>>(SerializedItems, options);
            set => SerializedItems = MessagePackSerializer.Serialize(value, options);
        }
        public Core.BlockPos? Position
        {
            get => string.IsNullOrEmpty(SerializedPosition) ? null : JsonConvert.DeserializeObject<Core.BlockPos>(SerializedPosition);
            set => SerializedPosition = value == null ? null : JsonConvert.SerializeObject(value);
        }
        public DateTime OpenedAt { get; set; }
    }
}
