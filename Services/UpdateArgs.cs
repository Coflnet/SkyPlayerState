using System;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

public class UpdateArgs : IDisposable
{
    public UpdateMessage msg;
    public StateObject currentState;
    public IPlayerStateService stateService;
    private AsyncServiceScope? scope;


    /// <summary>
    /// Send message to user
    /// </summary>
    /// <param name="text"></param>
    /// <param name="clickAction"></param>
    public virtual void SendMessage(string text, string? clickAction = null, string? source = null)
    {
        stateService.TryExecuteInScope(async provider =>
        {
            var messageService = provider.GetRequiredService<EventBroker.Client.Api.IMessageApi>();
            provider.GetRequiredService<ILogger<UpdateArgs>>().LogInformation("Sending message to {uuid} {message}", currentState.McInfo.Uuid, text);
            await messageService.MessageSendUserIdPostAsync(currentState.McInfo.Uuid.ToString("N"), new()
            {
                Message = text,
                Data = msg,
                SourceType = source ?? "StateEvents",
                Link = clickAction!
            });
        });
    }

    public void SendDebugMessage(string text, string? clickAction = null)
    {
        if(currentState.Settings.DebugEnabled)
        SendMessage(text, clickAction, "Debug");
    }

    public virtual T GetService<T>() where T : notnull
    {
        if (scope == null)
            scope = stateService.CreateAsyncScope();
        return scope.Value.ServiceProvider.GetRequiredService<T>();
    }

    public void Dispose()
    {
        scope?.Dispose();
    }
}
