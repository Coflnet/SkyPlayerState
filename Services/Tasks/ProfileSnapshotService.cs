using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Loads a player's <see cref="PlayerProfileSnapshot"/> from the profile service
/// (<c>{PROFILE_BASE_URL}/api/profile/{uuid}/current/data/full</c>, the raw member JSON) and keeps it in memory
/// for a short time. The profile service itself serves data cached for up to 3 hours, so a few minutes here only
/// saves repeated downloads of the large JSON when a player opens the task list several times.
/// <para>
/// The in-flight request is cached, not just the result: a caller that gives up waiting (see the optional-read
/// timeout in <see cref="TaskExecutionService"/>) leaves the download running, and the next request finds it done.
/// A failed or unreadable response is remembered briefly so a down profile service is not hammered per request.
/// Failures never throw, they yield a null snapshot.
/// </para>
/// </summary>
public class ProfileSnapshotService
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 2000;

    private readonly RestClient profileClient;
    private readonly ILogger<ProfileSnapshotService> logger;
    private readonly ConcurrentDictionary<string, (Task<PlayerProfileSnapshot> Task, DateTime At)> cache = new();

    public ProfileSnapshotService(IConfiguration config, ILogger<ProfileSnapshotService> logger)
    {
        profileClient = new RestClient(config["PROFILE_BASE_URL"] ?? "http://localhost:5014");
        this.logger = logger;
    }

    /// <summary>For tests that replace <see cref="Get"/>.</summary>
    protected ProfileSnapshotService()
    {
    }

    /// <summary>The snapshot for a player uuid (any format <see cref="Guid.TryParse(string, out Guid)"/> accepts), null when unavailable.</summary>
    public virtual Task<PlayerProfileSnapshot> Get(string uuid)
    {
        if (!Guid.TryParse(uuid, out var parsed))
            return Task.FromResult<PlayerProfileSnapshot>(null);
        var key = parsed.ToString("N");
        var now = DateTime.UtcNow;
        if (cache.TryGetValue(key, out var entry) && !IsExpired(entry, now))
            return entry.Task;
        if (cache.Count > MaxEntries) // eviction safety net, well above the live player cap
            cache.Clear();
        var task = Load(key);
        cache[key] = (task, now);
        return task;
    }

    private static bool IsExpired((Task<PlayerProfileSnapshot> Task, DateTime At) entry, DateTime now)
    {
        if (!entry.Task.IsCompleted)
            return false;
        var ttl = entry.Task.IsCompletedSuccessfully && entry.Task.Result != null ? SuccessTtl : FailureTtl;
        return now - entry.At > ttl;
    }

    private async Task<PlayerProfileSnapshot> Load(string uuid)
    {
        try
        {
            var request = new RestRequest($"api/profile/{uuid}/current/data/full", Method.Get) { Timeout = TimeSpan.FromSeconds(15) };
            var response = await profileClient.ExecuteAsync(request);
            if (!response.IsSuccessful)
            {
                logger.LogWarning("profile for {uuid} unavailable for task requirements: {status}", uuid, response.StatusCode);
                return null;
            }
            var snapshot = PlayerProfileSnapshot.FromMemberJson(response.Content);
            if (snapshot == null)
                logger.LogWarning("profile for {uuid} was not a member object, ignoring it for task requirements", uuid);
            return snapshot;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "failed to load profile for {uuid} for task requirements", uuid);
            return null;
        }
    }
}
