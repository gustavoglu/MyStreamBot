namespace MyStreamBot.Infrastructure.Tts;

public sealed class OverlayTtsOptions
{
    public const string SectionName = "TtsOverlay";

    public string BaseUrl { get; set; } = "http://127.0.0.1:55846/";
}
