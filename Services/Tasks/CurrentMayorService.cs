using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Provides the current mayor name (lowercase) for mayor gated task accessibility,
/// refreshed at most once per hour from the mayor service.
/// </summary>
public class CurrentMayorService
{
    private readonly Mayor.Client.Api.IMayorApiApi mayorApi;
    private readonly ILogger<CurrentMayorService> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string currentMayor;
    private DateTime lastUpdate = DateTime.MinValue;

    public CurrentMayorService(Mayor.Client.Api.IMayorApiApi mayorApi, ILogger<CurrentMayorService> logger)
    {
        this.mayorApi = mayorApi;
        this.logger = logger;
    }

    /// <summary>
    /// Current mayor name in lowercase, or null if it could not be loaded yet.
    /// </summary>
    public async Task<string> GetCurrentMayor()
    {
        if (DateTime.UtcNow - lastUpdate < TimeSpan.FromHours(1))
            return currentMayor;
        await refreshLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - lastUpdate < TimeSpan.FromHours(1))
                return currentMayor;
            var mayor = await mayorApi.MayorCurrentGetAsync();
            currentMayor = mayor?.Name?.ToLowerInvariant() ?? currentMayor;
            lastUpdate = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            // serve the stale value and retry in 5 minutes instead of hammering a failing service
            lastUpdate = DateTime.UtcNow - TimeSpan.FromMinutes(55);
            logger.LogError(e, "could not load current mayor, keeping {mayor}", currentMayor);
        }
        finally
        {
            refreshLock.Release();
        }
        return currentMayor;
    }
}
