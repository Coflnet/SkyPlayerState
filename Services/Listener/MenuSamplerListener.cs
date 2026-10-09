using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>One menu slot as the service received it.</summary>
public class MenuSampleItem
{
    public int Slot { get; set; }
    public string? Tag { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// One sampled menu: what the service actually received when it was opened. This is what the API returns,
/// it carries no player identity (see <see cref="StoredMenuSample"/>).
/// </summary>
public class MenuSample
{
    /// <summary>The normalized chest name (see <see cref="MenuTitleNormalizer"/>), never a raw title.</summary>
    public string Title { get; set; } = "";
    public DateTime SampledAt { get; set; }
    /// <summary>Number of slots the received view had, menu part plus the player's 36 inventory slots.</summary>
    public int TotalSlots { get; set; }
    /// <summary>Only the menu's own slots (the player's inventory part is left out).</summary>
    public List<MenuSampleItem> Items { get; set; } = [];
}

/// <summary>A sample as stored: the sample plus an irreversible short hash of the player, only used to keep samples of different players apart.</summary>
public class StoredMenuSample
{
    public string PlayerKey { get; set; } = "";
    public MenuSample Sample { get; set; } = new();
}

/// <summary>A known chest name for the list route.</summary>
public class MenuNameInfo
{
    public string Name { get; set; } = "";
    public int Samples { get; set; }
    public DateTime LastSampledAt { get; set; }
}

public enum MenuAddOutcome
{
    Stored,
    /// <summary>The name is new and the store already holds the maximum number of names, nothing was written.</summary>
    NameCapReached
}

/// <param name="Outcome">What happened.</param>
/// <param name="Players">Number of distinct players the name has samples from now.</param>
public record MenuAddResult(MenuAddOutcome Outcome, int Players);

/// <summary>Keeps samples of a few different players per normalized chest name.</summary>
public interface IMenuSampleStore
{
    Task<MenuAddResult> Add(string name, string playerKey, MenuSample sample, int maxNames, int maxPlayers);
    /// <summary>Samples of one name, newest first, without player identity.</summary>
    Task<List<MenuSample>> Get(string name);
    /// <summary>Samples of several names at once (one round trip), names without samples are left out.</summary>
    Task<Dictionary<string, List<MenuSample>>> GetMany(IReadOnlyCollection<string> names);
    /// <summary>All known names with sample count and last sample time, most recently sampled first.</summary>
    Task<List<MenuNameInfo>> ListNames();
    /// <summary>How many sampling attempts were dropped because the name cap was reached.</summary>
    Task<long> GetDroppedNameCount();
}

/// <summary>
/// Redis backed store (the service already uses redis for small shared auxiliary data, see TaskActivityService).
/// Layout, deliberately the simplest that makes list and search workable:
/// <list type="bullet">
/// <item><c>menu_sample:index</c> hash, field = normalized name, value = "samples|lastSampledUnixSeconds" (the list route reads only this);</item>
/// <item><c>menu_sample:v2:{name}</c> string, JSON list of <see cref="StoredMenuSample"/> (at most one per player, newest first).</item>
/// <item><c>menu_sample:dropped</c> counter of attempts dropped by the name cap.</item>
/// </list>
/// Read-modify-write is not atomic, a sample may rarely be lost to a race, which is fine for diagnostics.
/// Values have no expiry (the number of names is capped, a re-sample refreshes them); the sizes are bounded by
/// names x players x slots x description length.
/// </summary>
public class RedisMenuSampleStore(IConnectionMultiplexer redis) : IMenuSampleStore
{
    private const string IndexKey = "menu_sample:index";
    private const string DroppedKey = "menu_sample:dropped";
    private static RedisKey KeyOf(string name) => "menu_sample:v2:" + name;

    public async Task<MenuAddResult> Add(string name, string playerKey, MenuSample sample, int maxNames, int maxPlayers)
    {
        var db = redis.GetDatabase();
        var json = await db.StringGetAsync(KeyOf(name));
        if (json.IsNullOrEmpty)
        {
            // new name: the only case where the cap matters
            if (await db.HashLengthAsync(IndexKey) >= maxNames && !await db.HashExistsAsync(IndexKey, name))
            {
                await db.StringIncrementAsync(DroppedKey);
                return new MenuAddResult(MenuAddOutcome.NameCapReached, 0);
            }
        }
        var existing = json.IsNullOrEmpty ? [] : JsonConvert.DeserializeObject<List<StoredMenuSample>>(json.ToString()) ?? [];
        var merged = Merge(existing, playerKey, sample, maxPlayers);
        await db.StringSetAsync(KeyOf(name), JsonConvert.SerializeObject(merged));
        await db.HashSetAsync(IndexKey, name, EncodeIndex(merged.Count, sample.SampledAt));
        return new MenuAddResult(MenuAddOutcome.Stored, merged.Count);
    }

    /// <summary>
    /// The new sample replaces the older one of the same player, the result is newest first and holds at most
    /// <paramref name="maxPlayers"/> samples (the oldest one is dropped for a further player).
    /// </summary>
    public static List<StoredMenuSample> Merge(IEnumerable<StoredMenuSample> existing, string playerKey, MenuSample sample, int maxPlayers)
        => existing.Where(e => e.PlayerKey != playerKey)
            .Prepend(new StoredMenuSample { PlayerKey = playerKey, Sample = sample })
            .OrderByDescending(e => e.Sample.SampledAt)
            .Take(maxPlayers).ToList();

    public async Task<List<MenuSample>> Get(string name)
        => (await GetMany([name])).GetValueOrDefault(name) ?? [];

    public async Task<Dictionary<string, List<MenuSample>>> GetMany(IReadOnlyCollection<string> names)
    {
        var list = names.ToList();
        var values = await redis.GetDatabase().StringGetAsync(list.Select(KeyOf).ToArray());
        var result = new Dictionary<string, List<MenuSample>>();
        for (var i = 0; i < list.Count; i++)
        {
            if (values[i].IsNullOrEmpty)
                continue;
            var stored = JsonConvert.DeserializeObject<List<StoredMenuSample>>(values[i].ToString()) ?? [];
            // the player key stays behind: only the samples are handed out
            result[list[i]] = stored.Select(s => s.Sample).ToList();
        }
        return result;
    }

    public async Task<List<MenuNameInfo>> ListNames()
    {
        var entries = await redis.GetDatabase().HashGetAllAsync(IndexKey);
        return entries.Select(e => DecodeIndex(e.Name.ToString(), e.Value.ToString()))
            .OrderByDescending(i => i.LastSampledAt).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<long> GetDroppedNameCount()
        => (long)(await redis.GetDatabase().StringGetAsync(DroppedKey));

    public static string EncodeIndex(int samples, DateTime sampledAt)
        => $"{samples}|{new DateTimeOffset(DateTime.SpecifyKind(sampledAt, DateTimeKind.Utc)).ToUnixTimeSeconds()}";

    public static MenuNameInfo DecodeIndex(string name, string value)
    {
        var parts = value.Split('|');
        return new MenuNameInfo
        {
            Name = name,
            Samples = parts.Length > 0 && int.TryParse(parts[0], out var c) ? c : 0,
            LastSampledAt = parts.Length > 1 && long.TryParse(parts[1], out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : default
        };
    }
}

/// <summary>Settings of the <see cref="MenuSamplerListener"/>; every value can be overridden by an environment variable.</summary>
public class MenuSamplerOptions
{
    /// <summary>Distinct normalized chest names kept (env <c>MENU_SAMPLER_MAX_NAMES</c>). New names beyond it are dropped, not evicting.</summary>
    public int MaxNames { get; set; } = 3000;
    /// <summary>Different players kept per name (env <c>MENU_SAMPLER_PLAYERS</c>).</summary>
    public int PlayersPerName { get; set; } = 3;
    /// <summary>A name is re-sampled (and a player re-sampled) at most this often per pod (env <c>MENU_SAMPLER_RESAMPLE_HOURS</c>).</summary>
    public TimeSpan ResampleInterval { get; set; } = TimeSpan.FromHours(6);
    /// <summary>A name dropped by the cap is not tried again before this passed.</summary>
    public TimeSpan DroppedRetry { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Backoff after the store failed, so a redis outage is not hit on every chest open.</summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Menu slots kept per sample (a chest has at most 54).</summary>
    public int MaxSlots { get; set; } = 54;
    public int MaxDescriptionLength { get; set; } = 2000;
    /// <summary>Normalized titles matching one of these are not sampled.</summary>
    public IReadOnlyList<Regex> Deny { get; set; } = DefaultDeny;

    /// <summary>
    /// Seeded with what is pointless or bulky only:
    /// player owned containers ("Chest", "Large Chest", barrels, hoppers, furnaces, shulker boxes, dispensers, droppers:
    /// their content is the player's own items, not a menu), Ender Chest and Backpack pages (54 slots of the player's items each,
    /// already stored by StorageListener), and trade windows (partner's items, private). Everything else is sampled.
    /// Add more with the env var <c>MENU_SAMPLER_DENY</c> (";" separated regexes on the normalized title).
    /// </summary>
    public static readonly IReadOnlyList<Regex> DefaultDeny =
    [
        new(@"^(Chest|Large Chest|Barrel|Hopper|Furnace|Dispenser|Dropper|Shulker Box)$", RegexOptions.Compiled),
        new(@"^Ender Chest", RegexOptions.Compiled),
        new(@"^Backpack", RegexOptions.Compiled),
        new(@"^Trade$", RegexOptions.Compiled),
    ];

    public bool IsDenied(string normalizedName) => Deny.Any(r => r.IsMatch(normalizedName));

    public static MenuSamplerOptions FromEnvironment()
    {
        var options = new MenuSamplerOptions();
        if (int.TryParse(Environment.GetEnvironmentVariable("MENU_SAMPLER_MAX_NAMES"), out var names) && names > 0)
            options.MaxNames = names;
        if (int.TryParse(Environment.GetEnvironmentVariable("MENU_SAMPLER_PLAYERS"), out var players) && players > 0)
            options.PlayersPerName = players;
        if (double.TryParse(Environment.GetEnvironmentVariable("MENU_SAMPLER_RESAMPLE_HOURS"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
            options.ResampleInterval = TimeSpan.FromHours(hours);
        var deny = Environment.GetEnvironmentVariable("MENU_SAMPLER_DENY");
        if (!string.IsNullOrWhiteSpace(deny))
            options.Deny = DefaultDeny.Concat(deny.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => new Regex(p, RegexOptions.Compiled))).ToList();
        return options;
    }
}

/// <summary>
/// Samples what the service receives when any chest/menu is opened (title, and per menu slot tag, display name
/// and lore) so parsers for menus can be built and checked against real data. Per normalized chest name
/// (<see cref="MenuTitleNormalizer"/>) the samples of up to <see cref="MenuSamplerOptions.PlayersPerName"/>
/// different players are kept, one per player. The player is only kept as a hash to tell players apart and is
/// never returned by the API. Never throws into the update pipeline.
/// <para>
/// Cost: this runs on every chest open. The in-memory gate (per pod) skips without touching redis for a name
/// that already has all its players until <see cref="MenuSamplerOptions.ResampleInterval"/> passed, and for
/// a player that was just stored. Steady state is therefore at most one redis read+write per sampled name per
/// interval per pod: with 3 pods, the 6 hour default and 3000 names at most ~9000 per 6h = 0.4 writes/s (a few hundred
/// names in practice, i.e. well under 0.1/s), plus a short burst while a new name fills up and a one-off
/// burst after a pod restart (memory is empty, each name is touched once).
/// Read the samples at <c>GET /MenuSample</c>, <c>/MenuSample/samples</c> and <c>/MenuSample/search</c>.
/// </para>
/// </summary>
public class MenuSamplerListener : UpdateListener
{
    private readonly MenuSamplerOptions options;
    private readonly Func<DateTime> clock;
    private readonly ConcurrentDictionary<string, NameState> states = new();
    private readonly ConcurrentDictionary<string, DateTime> dropped = new();
    private long droppedCount;
    private long storeCalls;
    private DateTime failedUntil = DateTime.MinValue;
    private const int MaxDroppedRemembered = 10_000;

    public MenuSamplerListener() : this(MenuSamplerOptions.FromEnvironment()) { }

    public MenuSamplerListener(MenuSamplerOptions options, Func<DateTime>? clock = null)
    {
        this.options = options;
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Diagnostics only, a failure must never stop the state from being saved.</summary>
    public override bool Optional => true;

    /// <summary>Sampling attempts dropped by the name cap in this pod.</summary>
    public long DroppedNames => Interlocked.Read(ref droppedCount);
    /// <summary>Store calls made by this pod (what the gate lets through).</summary>
    public long StoreCalls => Interlocked.Read(ref storeCalls);

    private class NameState
    {
        public readonly Dictionary<string, DateTime> LastStoredByPlayer = new();
        public bool Full;
        public DateTime FullUntil;
    }

    public override async Task Process(UpdateArgs args)
    {
        try
        {
            var chest = args.msg.Chest;
            if (chest?.Items == null || string.IsNullOrEmpty(chest.Name))
                return;
            var name = MenuTitleNormalizer.Normalize(chest.Name);
            if (name.Length == 0 || options.IsDenied(name))
                return;
            var playerKey = PlayerKeyOf(args);
            if (playerKey == null)
                return;
            var now = clock();
            if (!ShouldStore(name, playerKey, now))
                return;
            var sample = BuildSample(chest, name, args.msg.ReceivedAt, options);
            Interlocked.Increment(ref storeCalls);
            MenuAddResult result;
            try
            {
                result = await args.GetService<IMenuSampleStore>().Add(name, playerKey, sample, options.MaxNames, options.PlayersPerName);
            }
            catch
            {
                failedUntil = now + options.FailureBackoff;
                throw;
            }
            RecordResult(name, playerKey, now, result);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to sample menu {menu}", args.msg?.Chest?.Name);
        }
    }

    /// <summary>The gate: false means nothing needs to be (re)sampled for this name/player right now.</summary>
    private bool ShouldStore(string name, string playerKey, DateTime now)
    {
        if (now < failedUntil)
            return false;
        if (dropped.TryGetValue(name, out var retryAt) && now < retryAt)
            return false;
        if (!states.TryGetValue(name, out var state))
            return true;
        lock (state)
        {
            if (state.Full)
                return now >= state.FullUntil;
            return !(state.LastStoredByPlayer.TryGetValue(playerKey, out var last) && now - last < options.ResampleInterval);
        }
    }

    private void RecordResult(string name, string playerKey, DateTime now, MenuAddResult result)
    {
        if (result.Outcome == MenuAddOutcome.NameCapReached)
        {
            Interlocked.Increment(ref droppedCount);
            if (dropped.Count >= MaxDroppedRemembered)
                dropped.Clear();
            if (dropped.TryAdd(name, now + options.DroppedRetry) && droppedCount == 1)
                Logger.LogWarning("Menu sampler holds {max} chest names, new names are dropped (first dropped: {name})", options.MaxNames, name);
            return;
        }
        var state = states.GetOrAdd(name, _ => new NameState());
        lock (state)
        {
            state.LastStoredByPlayer[playerKey] = now;
            if (state.LastStoredByPlayer.Count > 2 * options.PlayersPerName + 2)
                state.LastStoredByPlayer.Remove(state.LastStoredByPlayer.MinBy(p => p.Value).Key);
            state.Full = result.Players >= options.PlayersPerName;
            state.FullUntil = now + options.ResampleInterval;
        }
    }

    private static string? PlayerKeyOf(UpdateArgs args)
    {
        var id = args.currentState?.McInfo?.Uuid is { } uuid && uuid != Guid.Empty ? uuid.ToString("N") : args.currentState?.PlayerId;
        if (string.IsNullOrEmpty(id))
            id = args.msg.PlayerId;
        return string.IsNullOrEmpty(id) ? null : HashPlayer(id);
    }

    /// <summary>Short irreversible stand-in for a player, enough to tell players apart.</summary>
    public static string HashPlayer(string playerId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(playerId.ToLowerInvariant())), 0, 8);

    /// <summary>
    /// The menu's own slots with content. The last 36 slots of every chest view are the player's inventory
    /// (see CollectionListener.AccessibleInventoryStart), not menu content.
    /// </summary>
    public static MenuSample BuildSample(ChestView chest, string name, DateTime receivedAt, MenuSamplerOptions options)
    {
        var menuSlots = Math.Min(options.MaxSlots, CollectionListener.AccessibleInventoryStart(chest.Items));
        var items = new List<MenuSampleItem>();
        for (var slot = 0; slot < menuSlots; slot++)
        {
            var item = chest.Items[slot];
            if (item == null || (item.Tag == null && item.ItemName == null && item.Description == null))
                continue;
            items.Add(new MenuSampleItem
            {
                Slot = slot,
                Tag = item.Tag,
                Name = item.ItemName,
                Description = item.Description is { } d && d.Length > options.MaxDescriptionLength ? d[..options.MaxDescriptionLength] : item.Description
            });
        }
        return new MenuSample
        {
            Title = name,
            SampledAt = receivedAt == default ? DateTime.UtcNow : receivedAt,
            TotalSlots = chest.Items.Count,
            Items = items
        };
    }

    public static string StripColors(string text) => MenuTitleNormalizer.StripColors(text);
}
