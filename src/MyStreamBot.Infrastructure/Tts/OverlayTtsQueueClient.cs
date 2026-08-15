using System.Net.Http.Json;
using MyStreamBot.Application.Abstractions;

namespace MyStreamBot.Infrastructure.Tts;

public sealed class OverlayTtsQueueClient(HttpClient httpClient) : ITtsOverlayClient
{
    public async Task PublishAsync(
        string username,
        string text,
        string audioFileName,
        CancellationToken ct = default)
    {
        var payload = new
        {
            username,
            text,
            audioFileName
        };

        using var response = await httpClient.PostAsJsonAsync(
            "api/tts",
            payload,
            ct);

        response.EnsureSuccessStatusCode();
    }
}
