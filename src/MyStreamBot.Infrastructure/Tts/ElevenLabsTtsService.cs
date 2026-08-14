using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyStreamBot.Application.Abstractions;

namespace MyStreamBot.Infrastructure.Tts;

public sealed class ElevenLabsTtsService : ITtsService
{
    private readonly HttpClient _httpClient;
    private readonly ElevenLabsOptions _options;

    public ElevenLabsTtsService(
        HttpClient httpClient,
        IOptions<ElevenLabsOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<TtsResult> GenerateAsync(
        string text,
        string? voiceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("ElevenLabs:ApiKey não configurada.");

        text = text.Trim();

        if (text.Length == 0)
            throw new ArgumentException("O texto para TTS não pode ser vazio.", nameof(text));

        if (text.Length > _options.MaxCharacters)
            throw new ArgumentException(
                $"O texto excede o limite de {_options.MaxCharacters} caracteres.",
                nameof(text));

        var selectedVoiceId = string.IsNullOrWhiteSpace(voiceId)
            ? _options.DefaultVoiceId
            : voiceId.Trim();

        if (string.IsNullOrWhiteSpace(selectedVoiceId))
            throw new InvalidOperationException("ElevenLabs:DefaultVoiceId não configurado e nenhum voiceId foi informado.");

        var endpoint = $"text-to-speech/{Uri.EscapeDataString(selectedVoiceId)}?output_format={Uri.EscapeDataString(_options.OutputFormat)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("xi-api-key", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        var payload = new
        {
            text,
            model_id = _options.ModelId
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        var requestId = response.Headers.TryGetValues("request-id", out var values)
            ? values.FirstOrDefault()
            : null;

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"ElevenLabs retornou {(int)response.StatusCode} ({response.ReasonPhrase}). {error}");
        }

        var audio = await response.Content.ReadAsByteArrayAsync(ct);

        return new TtsResult(
            audio,
            response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg",
            GetExtension(_options.OutputFormat),
            text.Length,
            requestId);
    }

    private static string GetExtension(string outputFormat)
    {
        return outputFormat.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
            ? "pcm"
            : "mp3";
    }
}
