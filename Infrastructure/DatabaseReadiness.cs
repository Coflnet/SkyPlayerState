using System;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using ISession = Cassandra.ISession;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Coflnet.Sky.PlayerState.Infrastructure;

internal static class DatabaseReadiness
{
    public static Task CheckAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var session = services.GetRequiredService<ISession>();
            await session.ExecuteAsync(new SimpleStatement("SELECT key FROM system.local")
                .SetReadTimeoutMillis(3000).SetRetryPolicy(FallthroughRetryPolicy.Instance));
        }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);

    public static async Task HandleAsync(HttpContext context)
    {
        try
        {
            await CheckAsync(context.RequestServices, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (Exception)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }
}
