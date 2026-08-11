using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyStreamBot.Application.Integrations.AxelChat;
using MyStreamBot.Application.Services;
using MyStreamBot.Core.Enums;

namespace MyStreamBot.Infrastructure.AxelChat;

public sealed class AxelChatHostedService(
    IAxelChatClient axelChatClient,
    IServiceScopeFactory scopeFactory,
    ChatHistoryLogger historyLogger,
    ILogger<AxelChatHostedService> logger) : BackgroundService
{
    private const long MessageReward = 10;


    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "MyStreamBot AxelChat HostedService iniciado.");

        await historyLogger.WriteSystemAsync(
            "WORKER_STARTED",
            "MyStreamBot AxelChat HostedService iniciado.",
            stoppingToken);

        axelChatClient.EventReceived +=
            OnAxelChatEventAsync;

        try
        {
            await axelChatClient.RunAsync(
                stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "AxelChat HostedService encerrado.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Erro fatal no AxelChat HostedService.");

            await historyLogger.WriteSystemAsync(
                "FATAL_ERROR",
                ex.ToString(),
                CancellationToken.None);
        }
        finally
        {
            axelChatClient.EventReceived -=
                OnAxelChatEventAsync;

            try
            {
                await historyLogger.WriteSystemAsync(
                    "WORKER_STOPPED",
                    "MyStreamBot AxelChat HostedService encerrado.",
                    CancellationToken.None);
            }
            catch
            {
                // Nunca impedir o encerramento do Worker por falha de log.
            }
        }
    }

    private async Task OnAxelChatEventAsync(
        AxelChatEvent @event,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        // Mantemos eventos brutos no histórico para facilitar a análise dos testes.
        try
        {
            await historyLogger.WriteEventAsync(
                @event.Type,
                @event.RawJson,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Falha ao gravar evento do AxelChat no histórico local.");
        }

        switch (@event.Type)
        {
            case "STATES_CHANGED":
                HandleStateChanged(@event);
                break;

            case "NEW_MESSAGES_RECEIVED":
            case "MESSAGES_CHANGED":
                await HandleMessagesAsync(
                    @event,
                    cancellationToken);
                break;

            case "CLEAR_MESSAGES":
                logger.LogInformation(
                    "AxelChat limpou as mensagens.");
                break;

            default:
                logger.LogDebug(
                    "Evento AxelChat recebido: {Type}",
                    @event.Type);
                break;
        }
    }

    private void HandleStateChanged(
        AxelChatEvent @event)
    {
        if (@event.State is null)
            return;

        logger.LogInformation(
            "AxelChat estado: {Viewers} viewers; serviços: {Services}",
            @event.State.Viewers,
            @event.State.Services.Count);
    }

    private async Task HandleMessagesAsync(
        AxelChatEvent @event,
        CancellationToken cancellationToken)
    {
        await using var scope =
            scopeFactory.CreateAsyncScope();

        var economy = scope.ServiceProvider
            .GetRequiredService<EconomyService>();

        foreach (var message in @event.Messages)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (message.Deleted)
            {
                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    null,
                    null,
                    "Mensagem marcada como deletada.",
                    cancellationToken);

                continue;
            }

            if (string.IsNullOrWhiteSpace(message.UserId))
            {
                logger.LogWarning(
                    "Mensagem sem UserId ignorada. MessageId={MessageId}",
                    message.MessageId);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    null,
                    null,
                    "Mensagem sem UserId.",
                    cancellationToken);

                continue;
            }

            if (string.IsNullOrWhiteSpace(message.Text))
            {
                logger.LogDebug(
                    "Mensagem vazia não recompensada. UserId={UserId}",
                    message.UserId);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    null,
                    "Mensagem vazia.",
                    cancellationToken);

                continue;
            }

            var normalizedText = NormalizeMessage(message.Text);
            var sourceMessageKey = BuildSourceMessageKey(
                message,
                normalizedText);

            var result = await economy.TryRewardMessageAsync(
                message.Platform,
                message.UserId,
                message.Username,
                message.AvatarUrl,
                normalizedText,
                sourceMessageKey,
                MessageReward,
                PointTransactionType.MessageReward,
                "Mensagem no chat",
                cancellationToken);

            if (result.AlreadyProcessed)
            {
                logger.LogInformation(
                    "[POINTS] {Username} não ganhou pontos: mensagem/evento já processado anteriormente. MessageId={MessageId} | Platform={Platform}",
                    message.Username,
                    message.MessageId,
                    message.Platform);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    result.Balance,
                    "Mensagem/evento já processado anteriormente; nenhuma recompensa foi criada.",
                    cancellationToken,
                    sourceMessageKey);

                continue;
            }

            if (result.RepeatedMessage)
            {
                logger.LogInformation(
                    "[POINTS] {Username} não ganhou pontos: mensagem repetida consecutivamente. Platform={Platform}",
                    message.Username,
                    message.Platform);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    result.Balance,
                    "Mensagem igual à mensagem anterior do mesmo usuário.",
                    cancellationToken,
                    sourceMessageKey);

                continue;
            }

            logger.LogInformation(
                "[POINTS] {Username} ganhou +{Points} pontos | Saldo={Balance} | Platform={Platform}",
                message.Username,
                MessageReward,
                result.Balance,
                message.Platform);

            await LogMessageSafelyAsync(
                message,
                "REWARDED",
                MessageReward,
                result.Balance,
                "Mensagem diferente da anterior.",
                cancellationToken,
                sourceMessageKey);
        }
    }

    private async Task LogMessageSafelyAsync(
        AxelChatMessage message,
        string result,
        long? pointsAwarded,
        long? balance,
        string reason,
        CancellationToken cancellationToken,
        string? sourceMessageKey = null)
    {
        try
        {
            await historyLogger.WriteMessageAsync(
                message,
                result,
                pointsAwarded,
                balance,
                reason,
                cancellationToken,
                sourceMessageKey);
        }
        catch (Exception ex)
        {
            // O histórico nunca deve derrubar o processamento do chat.
            logger.LogError(
                ex,
                "Falha ao gravar mensagem {MessageId} no histórico local.",
                message.MessageId);
        }
    }

    private static string BuildSourceMessageKey(
        AxelChatMessage message,
        string normalizedMessage)
    {
        var platform = message.Platform.ToString();
        var userId = message.UserId.Trim();

        // O MessageId é a melhor identidade porque é fornecido pelo AxelChat
        // e permanece igual quando o evento é reenviado após um restart.
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return $"message-id:{platform}:{userId}:{message.MessageId.Trim()}";
        }

        // Fallback: plataforma + usuário + instante de publicação + texto.
        // Assim, a mesma mensagem com a mesma data/hora não é recompensada
        // novamente mesmo que o AxelChat não forneça MessageId.
        if (message.PublishedAt is DateTime publishedAt)
        {
            var utc = publishedAt.Kind == DateTimeKind.Utc
                ? publishedAt
                : publishedAt.ToUniversalTime();

            return $"published:{platform}:{userId}:{utc.Ticks}:{normalizedMessage}";
        }

        // Último fallback para payloads que não fornecem MessageId nem data.
        // O hash evita uma chave gigantesca no SQLite.
        var raw = $"{platform}|{userId}|{normalizedMessage}|{message.RawJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        return $"raw:{Convert.ToHexString(hash)}";
    }

    private static string NormalizeMessage(string text)
    {
        // Não removemos acentos. Apenas normalizamos caixa e espaços.
        return string.Join(
            ' ',
            text.Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }
}
