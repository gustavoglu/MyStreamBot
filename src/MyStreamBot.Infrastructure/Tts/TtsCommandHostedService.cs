using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Application.Integrations.AxelChat;

namespace MyStreamBot.Infrastructure.Tts;

/// <summary>
/// Processa comandos de TTS fora do fluxo principal de pontos.
/// O evento do AxelChat apenas coloca o pedido na fila para não bloquear
/// o processamento das mensagens/economia enquanto a API ElevenLabs responde.
/// </summary>
public sealed class TtsCommandHostedService(
    IAxelChatClient axelChatClient,
    ITtsService ttsService,
    ILogger<TtsCommandHostedService> logger) : BackgroundService
{
    private const string Command = "!testevoz";

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

        logger.LogInformation(
            "TTS Command Service iniciado. Comando disponível: {Command}",
            Command);

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }
        finally
        {
            axelChatClient.EventReceived -= OnAxelChatEventAsync;
        }
    }

    private Task OnAxelChatEventAsync(
        AxelChatEvent @event,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        if (!string.Equals(@event.Type, "NEW_MESSAGES_RECEIVED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(@event.Type, "MESSAGES_CHANGED", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        foreach (var message in @event.Messages)
        {
            if (message.Deleted || string.IsNullOrWhiteSpace(message.Text))
                continue;

            if (!TryExtractCommandText(message.Text, out var text))
                continue;

            // O AxelChat pode reenviar a mesma mensagem em eventos diferentes.
            // Não devemos gerar o mesmo áudio duas vezes.
            if (!string.IsNullOrWhiteSpace(message.MessageId) &&
                !_processedMessageIds.TryAdd(message.MessageId, 0))
            {
                continue;
            }

            _queue.Writer.TryWrite(
                new VoiceRequest(
                    message.MessageId,
                    message.Username,
                    text));
        }

        return Task.CompletedTask;
    }

    private async Task ProcessAsync(
        VoiceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "[TTS] {Username} solicitou teste de voz: {Text}",
                request.Username,
                request.Text);

            var result = await ttsService.GenerateAsync(
                request.Text,
                null,
                cancellationToken);

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyStreamBot",
                "tts-test");

            Directory.CreateDirectory(directory);

            var safeUsername = SanitizeFileName(request.Username);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"{timestamp}_{safeUsername}.{result.FileExtension}";
            var filePath = Path.Combine(directory, fileName);

            await File.WriteAllBytesAsync(
                filePath,
                result.Audio,
                cancellationToken);

            logger.LogInformation(
                "[TTS] Áudio gerado com sucesso para {Username}. Arquivo={FilePath} | Caracteres={Characters} | RequestId={RequestId}",
                request.Username,
                filePath,
                result.CharacterCount,
                result.RequestId ?? "n/a");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[TTS] Falha ao gerar voz para {Username}.",
                request.Username);
        }
    }

    private static bool TryExtractCommandText(
        string message,
        out string text)
    {
        text = string.Empty;

        var trimmed = message.Trim();

        if (!trimmed.StartsWith(Command, StringComparison.OrdinalIgnoreCase))
            return false;

        if (trimmed.Length == Command.Length)
            return false;

        var separator = trimmed[Command.Length];

        if (!char.IsWhiteSpace(separator))
            return false;

        text = trimmed[Command.Length..].Trim();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());

        return string.IsNullOrWhiteSpace(sanitized)
            ? "viewer"
            : sanitized;
    }

    private sealed record VoiceRequest(
        string MessageId,
        string Username,
        string Text);
}
