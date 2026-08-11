using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStreamBot.Application.Integrations.AxelChat;

namespace MyStreamBot.Infrastructure.AxelChat;

public sealed class AxelChatMessageSender(
    HttpClient httpClient,
    IOptions<AxelChatOptions> options,
    ILogger<AxelChatMessageSender> logger)
{
    private readonly AxelChatOptions _options = options.Value;

    public async Task SendAsync(
        string text,
        string? replyToMessageId = null,
        string? replyToUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var messageId = $"mystreambot_{Guid.NewGuid():N}";
        var authorId = "software_mystreambot";

        var payload = new[]
        {
            new
            {
                id = messageId,
                eventType = "Message",
                author = new
                {
                    id = authorId,
                    name = "MyStreamBot",
                    serviceId = "software",
                    avatar = ""
                },
                publishedAt = DateTime.UtcNow,
                receivedAt = DateTime.UtcNow,
                visible = new
                {
                    @private = true,
                    @public = true
                },
                reply = replyToMessageId is null
                    ? null
                    : new
                    {
                        messageId = replyToMessageId,
                        userId = replyToUserId ?? string.Empty,
                        name = ""
                    },
                contents = new[]
                {
                    new
                    {
                        type = "text",
                        data = new { text }
                    }
                }
            }
        };

        var endpoint = new Uri(
            new Uri(_options.Url.TrimEnd('/') + "/"),
            "api/v1/receive-events");

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                endpoint,
                payload,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            logger.LogInformation(
                "[BOT] Resposta enviada ao AxelChat: {Text}",
                text);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Falha ao enviar resposta ao AxelChat.");
        }
    }
}
