namespace MyStreamBot.Infrastructure.Tts;

public sealed class ElevenLabsOptions
{
    public const string SectionName = "ElevenLabs";

    public string ApiKey { get; set; } = "";

    public string DefaultVoiceId { get; set; } = "";

    public string ModelId { get; set; } = "eleven_flash_v2_5";

    public string OutputFormat { get; set; } = "mp3_44100_128";

    public string BaseUrl { get; set; } = "https://api.elevenlabs.io/v1/";

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxCharacters { get; set; } = 300;
}
