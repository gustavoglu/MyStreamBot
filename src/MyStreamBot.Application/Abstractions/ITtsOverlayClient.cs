namespace MyStreamBot.Application.Abstractions;

public interface ITtsOverlayClient
{
    Task PublishAsync(
        string username,
        string text,
        string audioFileName,
        CancellationToken ct = default);
}
