using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Application.Integrations.AxelChat;
using MyStreamBot.Application.Services;
using MyStreamBot.Core.Enums;

namespace MyStreamBot.Infrastructure.Tts;

public sealed class TtsCommandHostedService(
    IAxelChatClient axelChatClient,
    ITtsService ttsService,
    ITtsOverlayClient overlayClient,
    IServiceScopeFactory scopeFactory,
    IOptions<ElevenLabsOptions> options,
    ILogger<TtsCommandHostedService> logger) : BackgroundService
{
    private const string TestCommand = "!testevoz";
    private const string VoiceCommand = "!voz";
    private const long VoiceCost = 100;
    private const string TestUsername = "AxelChat";
    private readonly ElevenLabsOptions _options = options.Value;
    private readonly Channel<VoiceRequest> _queue = Channel.CreateUnbounded<VoiceRequest>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        axelChatClient.EventReceived += OnAxelChatEventAsync;
        logger.LogInformation("[TTS] SERVICE_STARTED. {TestCommand} é gratuito; {VoiceCommand} custa {Cost} pontos; usuário de teste {TestUsername} está liberado.", TestCommand, VoiceCommand, VoiceCost, TestUsername);
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                logger.LogInformation("[TTS] QUEUE_DEQUEUE MessageId={MessageId} User={Username} Command={Command}", request.MessageId, request.Username, request.Command);
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            axelChatClient.EventReceived -= OnAxelChatEventAsync;
            logger.LogInformation("[TTS] SERVICE_STOPPED");
        }
    }

    private Task OnAxelChatEventAsync(AxelChatEvent @event, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        if (!string.Equals(@event.Type, "NEW_MESSAGES_RECEIVED", StringComparison.OrdinalIgnoreCase) && !string.Equals(@event.Type, "MESSAGES_CHANGED", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        logger.LogDebug("[TTS] AXELCHAT_EVENT Type={Type} Messages={Count}", @event.Type, @event.Messages.Count);
        foreach (var message in @event.Messages)
        {
            if (message.Deleted || string.IsNullOrWhiteSpace(message.Text)) continue;
            if (!TryExtractCommandText(message.Text, out var text, out var command)) continue;
            if (!string.IsNullOrWhiteSpace(message.MessageId) && !_processedMessageIds.TryAdd(message.MessageId, 0))
            {
                logger.LogDebug("[TTS] DUPLICATE_IGNORED MessageId={MessageId}", message.MessageId);
                continue;
            }

            var request = new VoiceRequest(message.MessageId, message.UserId, message.Username, message.AvatarUrl, message.Platform, text, command);
            if (!_queue.Writer.TryWrite(request))
            {
                logger.LogError("[TTS] QUEUE_WRITE_FAILED MessageId={MessageId} User={Username}", message.MessageId, message.Username);
                continue;
            }
            logger.LogInformation("[TTS] COMMAND_RECEIVED MessageId={MessageId} User={Username} Command={Command} Text={Text} Platform={Platform}", message.MessageId, message.Username, command, text, message.Platform);
        }
        return Task.CompletedTask;
    }

    private async Task ProcessAsync(VoiceRequest request, CancellationToken cancellationToken)
    {
        string? filePath = null;
        try
        {
            var isTestUser = string.Equals(request.Username, TestUsername, StringComparison.OrdinalIgnoreCase);
            var isPaidCommand = string.Equals(request.Command, VoiceCommand, StringComparison.OrdinalIgnoreCase);
            var isTestCommand = string.Equals(request.Command, TestCommand, StringComparison.OrdinalIgnoreCase);

            if (!isPaidCommand && !isTestCommand)
            {
                logger.LogWarning("[TTS] UNKNOWN_COMMAND Command={Command} User={Username}", request.Command, request.Username);
                return;
            }

            // O AxelChat é um usuário de teste local: !voz não exige pontos para permitir testes do pipeline completo.
            if (isPaidCommand && !isTestUser)
            {
                await using var balanceScope = scopeFactory.CreateAsyncScope();
                var economy = balanceScope.ServiceProvider.GetRequiredService<EconomyService>();
                var balance = await economy.GetBalanceAsync(request.Platform, request.PlatformUserId, request.Username, cancellationToken);
                logger.LogInformation("[TTS] BALANCE_CHECK User={Username} Balance={Balance} Cost={Cost}", request.Username, balance, VoiceCost);
                if (balance < VoiceCost)
                {
                    logger.LogInformation("[TTS] REJECTED_INSUFFICIENT_BALANCE User={Username} Balance={Balance} Cost={Cost} ElevenLabsNotCalled=True", request.Username, balance, VoiceCost);
                    return;
                }
                logger.LogInformation("[TTS] BALANCE_OK User={Username} Balance={Balance} Cost={Cost}", request.Username, balance, VoiceCost);
            }
            else if (isPaidCommand)
            {
                logger.LogInformation("[TTS] TEST_USER_BYPASS User={Username} Cost={Cost} PointsRequired=False", request.Username, VoiceCost);
            }

            var voiceId = SelectVoiceId();
            logger.LogInformation("[TTS] GENERATING_AUDIO User={Username} Command={Command} VoiceId={VoiceId} Text={Text}", request.Username, request.Command, voiceId, request.Text);
            var result = await ttsService.GenerateAsync(request.Text, voiceId, cancellationToken);
            logger.LogInformation("[TTS] AUDIO_GENERATED User={Username} Bytes={Bytes} Characters={Characters} RequestId={RequestId}", request.Username, result.Audio.Length, result.CharacterCount, result.RequestId ?? "n/a");

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyStreamBot", "tts-test");
            Directory.CreateDirectory(directory);
            var safeUsername = SanitizeFileName(request.Username);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"{timestamp}_{safeUsername}.{result.FileExtension}";
            filePath = Path.Combine(directory, fileName);
            await File.WriteAllBytesAsync(filePath, result.Audio, cancellationToken);
            logger.LogInformation("[TTS] AUDIO_SAVED File={FilePath} Exists={Exists} Bytes={Bytes}", filePath, File.Exists(filePath), result.Audio.Length);

            if (isPaidCommand && !isTestUser)
            {
                await using var spendScope = scopeFactory.CreateAsyncScope();
                var economy = spendScope.ServiceProvider.GetRequiredService<EconomyService>();
                var spend = await economy.TrySpendAsync(request.Platform, request.PlatformUserId, request.Username, request.AvatarUrl, $"tts:{request.MessageId}", VoiceCost, PointTransactionType.Tts, $"Leitura por voz: {request.Text}", cancellationToken);
                if (!spend.Spent)
                {
                    logger.LogWarning("[TTS] SPEND_FAILED User={Username} InsufficientBalance={InsufficientBalance} Balance={Balance} ElevenLabsWasAlreadyCalled=True", request.Username, spend.InsufficientBalance, spend.Balance);
                    TryDelete(filePath);
                    return;
                }
                logger.LogInformation("[TTS] POINTS_CHARGED User={Username} Cost={Cost} Balance={Balance}", request.Username, VoiceCost, spend.Balance);
            }
            else if (isPaidCommand)
            {
                logger.LogInformation("[TTS] POINTS_NOT_CHARGED User={Username} Reason=TEST_USER_BYPASS", request.Username);
            }

            logger.LogInformation("[TTS] PUBLISHING_OVERLAY User={Username} File={FileName}", request.Username, fileName);
            await overlayClient.PublishAsync(request.Username, request.AvatarUrl, request.Text, fileName, cancellationToken);
            logger.LogInformation("[TTS] OVERLAY_PUBLISHED User={Username} File={FileName} MessageId={MessageId} Command={Command}", request.Username, fileName, request.MessageId, request.Command);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            TryDelete(filePath);
            logger.LogError(ex, "[TTS] PIPELINE_ERROR User={Username} MessageId={MessageId} Command={Command}", request.Username, request.MessageId, request.Command);
        }
    }

    private string SelectVoiceId()
    {
        var voices = _options.VoiceIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (voices.Length > 0) return voices[Random.Shared.Next(voices.Length)];
        if (!string.IsNullOrWhiteSpace(_options.DefaultVoiceId)) return _options.DefaultVoiceId.Trim();
        throw new InvalidOperationException("Nenhuma voz configurada em ElevenLabs:VoiceIds e ElevenLabs:DefaultVoiceId também está vazio.");
    }

    private static bool TryExtractCommandText(string message, out string text, out string command)
    {
        text = string.Empty; command = string.Empty; var trimmed = message.Trim();
        foreach (var candidate in new[] { TestCommand, VoiceCommand })
        {
            if (!trimmed.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.Length == candidate.Length || !char.IsWhiteSpace(trimmed[candidate.Length])) continue;
            text = trimmed[candidate.Length..].Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;
            command = candidate; return true;
        }
        return false;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "viewer" : sanitized;
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record VoiceRequest(string MessageId, string PlatformUserId, string Username, string? AvatarUrl, Platform Platform, string Text, string Command);
}
