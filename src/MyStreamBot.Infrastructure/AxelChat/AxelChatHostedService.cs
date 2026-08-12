using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Application.Integrations.AxelChat;
using MyStreamBot.Application.Services;
using MyStreamBot.Core.Enums;
using MyStreamBot.Infrastructure.Economy;
using System.Security.Cryptography;
using System.Text;

namespace MyStreamBot.Infrastructure.AxelChat;

public sealed class AxelChatHostedService(
    IAxelChatClient axelChatClient,
    IServiceScopeFactory scopeFactory,
    ChatHistoryLogger historyLogger,
    IOptions<EconomyOptions> economyOptions,
    ILogger<AxelChatHostedService> logger) : BackgroundService
{
    private readonly EconomyOptions _economy = economyOptions.Value;

    private readonly HashSet<string> _excludedUsernames =
        economyOptions.Value.ExcludedUsernames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "MyStreamBot AxelChat HostedService iniciado.");

        await historyLogger.WriteSystemAsync(
            "WORKER_STARTED",
            "MyStreamBot AxelChat HostedService iniciado.",
            stoppingToken);

        if (_excludedUsernames.Count > 0)
        {
            logger.LogInformation(
                "Usuários excluídos da economia: {Users}",
                string.Join(", ", _excludedUsernames));
        }

        logger.LogInformation(
            "Economia: Message={Message}; Follow={Follow}; Subscription={Subscription}; Gift={Gift}; Donation={Donation}",
            _economy.MessageReward,
            _economy.FollowReward,
            _economy.SubscriptionReward,
            _economy.GiftReward,
            _economy.DonationReward);

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
                    "Evento sem UserId ignorado. MessageId={MessageId} | EventType={EventType}",
                    message.MessageId,
                    message.EventType);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    null,
                    null,
                    "Evento sem UserId.",
                    cancellationToken);

                continue;
            }

            if (_excludedUsernames.Contains(message.Username.Trim()))
            {
                logger.LogInformation(
                    "[POINTS] {Username} ignorado: usuário excluído da economia. EventType={EventType} | Platform={Platform}",
                    message.Username,
                    message.EventType,
                    message.Platform);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    null,
                    "Usuário excluído da economia.",
                    cancellationToken);

                continue;
            }

            var reward = GetRewardDefinition(message.EventType);

            if (reward is null)
            {
                logger.LogInformation(
                    "[POINTS] Evento não pontuável recebido: EventType={EventType} | Username={Username} | Platform={Platform}",
                    message.EventType,
                    message.Username,
                    message.Platform);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    null,
                    $"Tipo de evento não pontuável: {message.EventType}.",
                    cancellationToken);

                continue;
            }

            if (reward.Value.Amount <= 0)
            {
                logger.LogInformation(
                    "[POINTS] {Username}: evento {EventType} reconhecido, mas a recompensa está desabilitada (0 pontos). Platform={Platform}",
                    message.Username,
                    reward.Value.EventType,
                    message.Platform);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    null,
                    $"Evento reconhecido, porém recompensa desabilitada: {reward.Value.EventType}.",
                    cancellationToken);

                continue;
            }

            var normalizedText =
                reward.Value.EnforceRepeatedMessageCheck
                    ? NormalizeMessage(message.Text)
                    : null;

            if (reward.Value.EnforceRepeatedMessageCheck &&
                string.IsNullOrWhiteSpace(normalizedText))
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

            var sourceEventKey = BuildSourceEventKey(
                message,
                normalizedText);

            RewardResult result;

            try
            {
                result = await economy.TryRewardAsync(
                    message.Platform,
                    message.UserId,
                    message.Username,
                    message.AvatarUrl,
                    normalizedText,
                    sourceEventKey,
                    reward.Value.Amount,
                    reward.Value.TransactionType,
                    reward.Value.Description,
                    reward.Value.EnforceRepeatedMessageCheck,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Falha ao processar recompensa. EventType={EventType} | MessageId={MessageId} | User={Username}",
                    message.EventType,
                    message.MessageId,
                    message.Username);

                await LogMessageSafelyAsync(
                    message,
                    "ERROR",
                    null,
                    null,
                    $"Erro ao processar recompensa: {ex.Message}",
                    cancellationToken,
                    sourceEventKey);

                continue;
            }

            if (result.AlreadyProcessed)
            {
                logger.LogInformation(
                    "[POINTS] {Username} não ganhou pontos: evento já processado anteriormente. EventType={EventType} | MessageId={MessageId} | Platform={Platform}",
                    message.Username,
                    reward.Value.EventType,
                    message.MessageId,
                    message.Platform);

                await LogMessageSafelyAsync(
                    message,
                    "IGNORED",
                    0,
                    result.Balance,
                    "Evento já processado anteriormente; nenhuma recompensa foi criada.",
                    cancellationToken,
                    sourceEventKey);

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
                    sourceEventKey);

                continue;
            }

            logger.LogInformation(
                "[POINTS] {Username} ganhou +{Points} pontos | EventType={EventType} | Saldo={Balance} | Platform={Platform}",
                message.Username,
                reward.Value.Amount,
                reward.Value.EventType,
                result.Balance,
                message.Platform);

            await LogMessageSafelyAsync(
                message,
                "REWARDED",
                reward.Value.Amount,
                result.Balance,
                reward.Value.Description,
                cancellationToken,
                sourceEventKey);
        }
    }

    private (string EventType, long Amount, PointTransactionType TransactionType, string Description, bool EnforceRepeatedMessageCheck)?
        GetRewardDefinition(string? eventType)
    {
        var normalized = NormalizeEventType(eventType);

        return normalized switch
        {
            "message" => (
                "Message",
                _economy.MessageReward,
                PointTransactionType.MessageReward,
                "Mensagem no chat",
                true),

            "follow" or "followed" => (
                "Follow",
                _economy.FollowReward,
                PointTransactionType.Follow,
                "Follow",
                false),

            "subscription" or "subscribe" or "subscribed" or "sub" => (
                "Subscription",
                _economy.SubscriptionReward,
                PointTransactionType.Subscription,
                "Inscrição",
                false),

            "gift" or "giftreceived" or "giftsent" => (
                "Gift",
                _economy.GiftReward,
                PointTransactionType.Gift,
                "Gift",
                false),

            "donation" or "donated" or "donationreceived" => (
                "Donation",
                _economy.DonationReward,
                PointTransactionType.Donation,
                "Doação",
                false),

            _ => null
        };
    }

    private async Task LogMessageSafelyAsync(
        AxelChatMessage message,
        string result,
        long? pointsAwarded,
        long? balance,
        string reason,
        CancellationToken cancellationToken,
        string? sourceEventKey = null)
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
                sourceEventKey);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Falha ao gravar mensagem {MessageId} no histórico local.",
                message.MessageId);
        }
    }

    private static string BuildSourceEventKey(
        AxelChatMessage message,
        string? normalizedMessage)
    {
        var platform = message.Platform.ToString();
        var userId = message.UserId.Trim();
        var eventType = NormalizeEventType(message.EventType);

        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return $"event-id:{platform}:{userId}:{eventType}:{message.MessageId.Trim()}";
        }

        if (message.PublishedAt is DateTime publishedAt)
        {
            var utc = publishedAt.Kind == DateTimeKind.Utc
                ? publishedAt
                : publishedAt.ToUniversalTime();

            return $"published:{platform}:{userId}:{eventType}:{utc.Ticks}:{normalizedMessage}";
        }

        var raw =
            $"{platform}|{userId}|{eventType}|{normalizedMessage}|{message.RawJson}";

        var hash =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(raw));

        return $"raw:{Convert.ToHexString(hash)}";
    }

    private static string NormalizeEventType(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return string.Empty;

        return new string(
                eventType
                    .Trim()
                    .ToLowerInvariant()
                    .Where(char.IsLetterOrDigit)
                    .ToArray());
    }

    private static string NormalizeMessage(string text)
    {
        // Não removemos acentos. Apenas normalizamos caixa e espaços.
        return string.Join(
            ' ',
            text.Trim()
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }
}
