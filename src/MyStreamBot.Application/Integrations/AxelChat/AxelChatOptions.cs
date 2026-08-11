namespace MyStreamBot.Application.Integrations.AxelChat;

public sealed class AxelChatOptions
{
    public const string SectionName = "AxelChat";

    public string Url { get; set; } = "ws://127.0.0.1:8356";
    public int ReconnectDelaySeconds { get; set; } = 5;
    public int ReceiveBufferSize { get; set; } = 64 * 1024;
}
