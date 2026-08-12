using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStreamBot.Application.Integrations.AxelChat;
using MyStreamBot.Core.Enums;

namespace MyStreamBot.Infrastructure.AxelChat;

public sealed class AxelChatWebSocketClient(
    IOptions<AxelChatOptions> options,
    ILogger<AxelChatWebSocketClient> logger) : IAxelChatClient
{
    private readonly AxelChatOptions _options = options.Value;

    public event Func<
        AxelChatEvent,
        CancellationToken,
        Task>? EventReceived;

    public bool IsConnected { get; private set; }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Erro na conexão WebSocket com AxelChat.");
            }
            finally
            {
                IsConnected = false;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Reconectando ao AxelChat em {Seconds}s...",
                    _options.ReconnectDelaySeconds);

                await Task.Delay(
                    TimeSpan.FromSeconds(
                        _options.ReconnectDelaySeconds),
                    cancellationToken);
            }
        }
    }

    private async Task ConnectAndReceiveAsync(
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();

        socket.Options.KeepAliveInterval =
            TimeSpan.FromSeconds(20);

        logger.LogInformation(
            "Conectando ao AxelChat: {Url}",
            _options.Url);

        await socket.ConnectAsync(
            new Uri(_options.Url),
            cancellationToken);

        IsConnected = true;

        logger.LogInformation(
            "WebSocket do AxelChat conectado.");

        var buffer = new byte[
            Math.Max(
                8 * 1024,
                _options.ReceiveBufferSize)];

        using var messageBuffer = new MemoryStream();

        try
        {
            while (
                socket.State == WebSocketState.Open &&
                !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(
                    buffer,
                    cancellationToken);

                if (result.MessageType ==
                    WebSocketMessageType.Close)
                {
                    logger.LogWarning(
                        "AxelChat encerrou a conexão WebSocket: {Status} {Description}",
                        result.CloseStatus,
                        result.CloseStatusDescription);

                    return;
                }

                messageBuffer.Write(
                    buffer,
                    0,
                    result.Count);

                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(
                    messageBuffer.GetBuffer(),
                    0,
                    checked(
                        (int)messageBuffer.Length));

                messageBuffer.SetLength(0);

                await HandleMessageAsync(
                    json,
                    cancellationToken);
            }
        }
        finally
        {
            IsConnected = false;
        }
    }

    private async Task HandleMessageAsync(
        string json,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document =
                JsonDocument.Parse(json);

            var root = document.RootElement;

            var type =
                root.TryGetProperty(
                    "type",
                    out var typeElement)
                    ? typeElement.GetString() ?? "UNKNOWN"
                    : "UNKNOWN";

            switch (type)
            {
                case "HELLO":

                    logger.LogInformation(
                        "AxelChat HELLO recebido.");

                    break;

                case "SERVER_ALIVE":

                    logger.LogTrace(
                        "AxelChat SERVER_ALIVE recebido.");

                    break;

                case "STATES_CHANGED":

                    await PublishAsync(
                        new AxelChatEvent(
                            type,
                            [],
                            ParseState(root),
                            json),
                        cancellationToken);

                    break;

                case "NEW_MESSAGES_RECEIVED":

                case "MESSAGES_CHANGED":
                    {
                        var messages =
                            ParseMessages(root);

                        if (messages.Count == 0)
                            break;

                        // A deduplicação definitiva é feita no SQLite, e não apenas
                        // em memória. Assim ela continua funcionando após restart.
                        await PublishAsync(
                            new AxelChatEvent(
                                type,
                                messages,
                                null,
                                json),
                            cancellationToken);

                        break;
                    }

                case "CLEAR_MESSAGES":

                    await PublishAsync(
                        new AxelChatEvent(
                            type,
                            [],
                            null,
                            json),
                        cancellationToken);

                    break;

                default:

                    logger.LogDebug(
                        "Evento AxelChat desconhecido recebido: {Type}",
                        type);

                    await PublishAsync(
                        new AxelChatEvent(
                            type,
                            [],
                            null,
                            json),
                        cancellationToken);

                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Mensagem JSON inválida recebida do AxelChat: {Json}",
                json);
        }
    }

    private async Task PublishAsync(
        AxelChatEvent @event,
        CancellationToken cancellationToken)
    {
        var handlers = EventReceived;

        if (handlers is null)
            return;

        foreach (
            var handler in handlers
                .GetInvocationList()
                .Cast<Func<
                    AxelChatEvent,
                    CancellationToken,
                    Task>>())
        {
            await handler(
                @event,
                cancellationToken);
        }
    }

    private List<AxelChatMessage> ParseMessages(
        JsonElement root)
    {
        var result =
            new List<AxelChatMessage>();

        if (!root.TryGetProperty(
                "data",
                out var data))
        {
            return result;
        }

        if (!data.TryGetProperty(
                "messages",
                out var messages))
        {
            return result;
        }

        if (messages.ValueKind !=
            JsonValueKind.Array)
        {
            return result;
        }

        foreach (
            var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty(
                    "author",
                    out var author))
            {
                continue;
            }

            var messageId =
                GetString(
                    message,
                    "id")
                ?? string.Empty;

            var eventType =
                GetString(
                    message,
                    "eventType")
                ?? "Message";

            var userId =
                GetString(
                    author,
                    "id")
                ?? string.Empty;

            var username =
                GetString(
                    author,
                    "name")
                ?? string.Empty;

            // O AxelChat envia a imagem do usuário no objeto author.
            // Mantemos alguns nomes de propriedade como fallback para
            // versões/serviços diferentes.
            var avatarUrl =
                GetString(author, "avatar")
                ?? GetString(author, "avatarUrl")
                ?? GetString(author, "profileImageUrl");

            var serviceId =
                GetString(
                    author,
                    "serviceId")
                ??
                GetString(
                    message,
                    "serviceId")
                ??
                string.Empty;

            var text =
                ExtractPlainText(message);

            var deleted =
                GetBoolean(
                    message,
                    "deleted")
                ||
                GetBoolean(
                    message,
                    "markedAsDeleted");

            var publishedAt =
                ParseDateTime(
                    message,
                    "publishedAt");

            var platform =
                MapPlatform(serviceId);

            /*
             * Preserva o JSON da mensagem individual.
             *
             * Isso é útil para futuras interações,
             * emotes, stickers, gifts, etc.
             */
            var rawJson =
                message.GetRawText();

            logger.LogInformation(
                "[AXELCHAT] MessageId={MessageId} | EventType='{EventType}' | ServiceId='{ServiceId}' | Platform={Platform} | UserId={UserId} | Username='{Username}' | AvatarUrl='{AvatarUrl}' | Message='{Message}' | Deleted={Deleted}",
                messageId,
                eventType,
                serviceId,
                platform,
                userId,
                username,
                avatarUrl,
                text,
                deleted);

            result.Add(
                new AxelChatMessage(
                    messageId,
                    userId,
                    username,
                    avatarUrl,
                    platform,
                    text,
                    publishedAt,
                    deleted,
                    eventType,
                    rawJson));
        }

        return result;
    }

    private static AxelChatStateChanged? ParseState(
        JsonElement root)
    {
        if (!root.TryGetProperty(
                "data",
                out var data))
        {
            return null;
        }

        if (data.ValueKind !=
            JsonValueKind.Object)
        {
            return null;
        }

        var viewers =
            GetInt(
                data,
                "viewers");

        var services =
            new List<AxelChatServiceState>();

        if (
            data.TryGetProperty(
                "services",
                out var serviceArray)
            &&
            serviceArray.ValueKind ==
                JsonValueKind.Array)
        {
            foreach (
                var service in
                serviceArray.EnumerateArray())
            {
                services.Add(
                    new AxelChatServiceState(
                        GetString(
                            service,
                            "type_id")
                        ?? string.Empty,

                        GetString(
                            service,
                            "connection_state")
                        ?? string.Empty,

                        GetBoolean(
                            service,
                            "enabled"),

                        GetInt(
                            service,
                            "viewers"),

                        GetInt(
                            service,
                            "followers")));
            }
        }

        return new AxelChatStateChanged(
            services,
            viewers);
    }

    private static string ExtractPlainText(
        JsonElement message)
    {
        /*
         * Primeiro tenta usar o texto já
         * preparado pelo próprio AxelChat.
         */
        if (
            message.TryGetProperty(
                "contentsAsPlainText",
                out var plainText)
            &&
            plainText.ValueKind ==
                JsonValueKind.String)
        {
            return plainText.GetString()
                ?? string.Empty;
        }

        /*
         * Fallback para contents.
         */
        if (
            !message.TryGetProperty(
                "contents",
                out var contents)
            ||
            contents.ValueKind !=
                JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder =
            new StringBuilder();

        foreach (
            var content in
            contents.EnumerateArray())
        {
            if (!content.TryGetProperty(
                    "type",
                    out var type))
            {
                continue;
            }

            if (
                type.GetString() == "text"
                &&
                content.TryGetProperty(
                    "data",
                    out var data))
            {
                var text =
                    GetString(
                        data,
                        "text");

                if (!string.IsNullOrEmpty(text))
                {
                    builder.Append(text);
                }
            }
        }

        return builder.ToString();
    }

    private static Platform MapPlatform(
        string serviceId)
    {
        if (string.IsNullOrWhiteSpace(
                serviceId))
        {
            return Platform.Unknown;
        }

        return serviceId
            .Trim()
            .ToLowerInvariant()
            switch
        {
            "youtube" =>
                Platform.YouTube,

            "youtubeshorts" =>
                Platform.YouTubeShorts,

            "youtube_shorts" =>
                Platform.YouTubeShorts,

            "youtube-live" =>
                Platform.YouTube,

            "youtube_live" =>
                Platform.YouTube,

            "tiktok" =>
                Platform.TikTok,

            "tiktok-live" =>
                Platform.TikTok,

            "tiktok_live" =>
                Platform.TikTok,

            "kick" =>
                Platform.Kick,

            "kick-live" =>
                Platform.Kick,

            "kick_live" =>
                Platform.Kick,

            "twitch" =>
                Platform.Twitch,

            "odysee" =>
                Platform.Odysee,

            "trovo" =>
                Platform.Trovo,

            "rumble" =>
                Platform.Rumble,

            "loco" =>
                Platform.Loco,

            "chzzk" =>
                Platform.Chzzk,

            "facebook" =>
                Platform.Facebook,

            "discord" =>
                Platform.Discord,

            "telegram" =>
                Platform.Telegram,

            "dlive" =>
                Platform.DLive,

            "bigolive" =>
                Platform.BIGO,

            "bigo" =>
                Platform.BIGO,

            "boosty" =>
                Platform.Boosty,

            _ =>
                Platform.Unknown
        };
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(
                property,
                out var value)
            &&
            value.ValueKind ==
                JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBoolean(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(
                property,
                out var value)
            &&
            value.ValueKind is
                JsonValueKind.True
                or
                JsonValueKind.False
            &&
            value.GetBoolean();
    }

    private static int GetInt(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(
                property,
                out var value)
            &&
            value.TryGetInt32(
                out var number)
            ? number
            : 0;
    }

    private static DateTime? ParseDateTime(
        JsonElement element,
        string property)
    {
        var value =
            GetString(
                element,
                property);

        return DateTime.TryParse(
            value,
            out var date)
            ? date
            : null;
    }
}