using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Application.Integrations.AxelChat;

namespace MyStreamBot.Infrastructure.Tts;

public sealed class TtsCommandHostedService(
    IAxelChatClient axelChatClient,
    ITtsService ttsService,
    ITtsOverlayClient overlayClient,
    IOptions<ElevenLabsOptions> options,
    ILogger<TtsCommandHostedService> logger) : BackgroundService
{
    private const string TestCommand = "!testevoz";
    private const string VoiceCommand = "!voz";
    private readonly ElevenLabsOptions _options = options.Value;

    private readonly Channel<VoiceRequest> _queue =
        Channel.CreateUnbounded<VoiceRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        axelChatClient.EventReceived += OnAxelChatEventAsync;
        logger.LogInformation("TTS Command Service iniciado. Comandos disponíveis: {TestCommand} e {VoiceCommand}", TestCommand, VoiceCommand);

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            axelChatClient.EventReceived -= OnAxelChatEventAsync;
        }
    }

    private Task OnAxelChatEventAsync(AxelChatEvent @event, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        if (!string.Equals(@event.Type, "NEW_MESSAGES_RECEIVED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(@event.Type, "MESSAGES_CHANGED", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        foreach (var message in @event.Messages)
        {
            if (message.Deleted || string.IsNullOrWhiteSpace(message.Text))
                continue;

            if (!TryExtractCommandText(message.Text, out var text, out var command))
                continue;

            if (!string.IsNullOrWhiteSpace(message.MessageId) &&
                !_processedMessageIds.TryAdd(message.MessageId, 0))
                continue;

            _queue.Writer.TryWrite(new VoiceRequest(message.MessageId, message.Username, text, command));
        }

        return Task.CompletedTask;
    }

    private async Task ProcessAsync(VoiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var voiceId = SelectVoiceId();
            logger.LogInformation("[TTS] {Username} solicitou {Command}: {Text} | VoiceId={VoiceId}", request.Username, request.Command, request.Text, voiceId);

            var result = await ttsService.GenerateAsync(request.Text, voiceId, cancellationToken);

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyStreamBot",
                "tts-test");

            Directory.CreateDirectory(directory);

            var safeUsername = SanitizeFileName(request.Username);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"{timestamp}_{safeUsername}.{result.FileExtension}";
            var filePath = Path.Combine(directory, fileName);

            await File.WriteAllBytesAsync(filePath, result.Audio, cancellationToken);

            await overlayClient.PublishAsync(
                request.Username,
                request.Text,
                fileName,
                cancellationToken);

            logger.LogInformation(
                "[TTS] Áudio gerado e publicado no overlay para {Username}. Arquivo={FilePath} | Caracteres={Characters} | VoiceId={VoiceId} | RequestId={RequestId}",
                request.Username,
                filePath,
                result.CharacterCount,
                voiceId,
                result.RequestId ?? "n/a");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TTS] Falha ao gerar/publicar voz para {Username}.", request.Username);
        }
    }

    private string SelectVoiceId()
    {
        var voices = _options.VoiceIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (voices.Length > 0)
            return voices[Random.Shared.Next(voices.Length)];

        if (!string.IsNullOrWhiteSpace(_options.DefaultVoiceId))
            return _options.DefaultVoiceId.Trim();

        throw new InvalidOperationException("Nenhuma voz configurada em ElevenLabs:VoiceIds e ElevenLabs:DefaultVoiceId também está vazio.");
    }

    private static bool TryExtractCommandText(string message, out string text, out string command)
    {
        text = string.Empty;
        command = string.Empty;
        var trimmed = message.Trim();

        foreach (var candidate in new[] { TestCommand, VoiceCommand })
        {
            if (!trimmed.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.Length == candidate.Length)
                return false;
            if (!char.IsWhiteSpace(trimmed[candidate.Length]))
                continue;

            text = trimmed[candidate.Length..].Trim();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            command = candidate;
            return true;
        }

        return false;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "viewer" : sanitized;
    }

    private sealed record VoiceRequest(string MessageId, string Username, string Text, string Command);
}
