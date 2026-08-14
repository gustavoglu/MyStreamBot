namespace MyStreamBot.Application.Abstractions;

public interface ITtsService
{
    Task<TtsResult> GenerateAsync(
        string text,
        string? voiceId = null,
        CancellationToken ct = default);
}

public sealed record TtsResult(
    byte[] Audio,
    string ContentType,
    string FileExtension,
    int CharacterCount,
    string? RequestId);
