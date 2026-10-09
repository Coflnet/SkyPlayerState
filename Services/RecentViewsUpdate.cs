using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

public class RecentViewsUpdate : UpdateListener
{
    /// <summary>How many of the latest chest views are kept in <see cref="Models.StateObject.RecentViews"/>.</summary>
    public const int MaxRecentViews = 3;

    /// <inheritdoc/>
    public override Task Process(UpdateArgs args)
    {
        var recentViews = args.currentState.RecentViews;
        recentViews.Enqueue(args.msg.Chest);
        while (recentViews.Count > MaxRecentViews)
            recentViews.TryDequeue(out _);

        return Task.CompletedTask;
    }
}
