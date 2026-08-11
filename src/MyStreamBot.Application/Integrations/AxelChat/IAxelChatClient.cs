namespace MyStreamBot.Application.Integrations.AxelChat;

public interface IAxelChatClient
{
    event Func<AxelChatEvent, CancellationToken, Task>? EventReceived;

    bool IsConnected { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
