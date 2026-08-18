namespace MyStreamBot.Core.Entities;

public sealed class TtsOverlayEvent
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Text { get; set; } = string.Empty;
    public string AudioFileName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsRepeat { get; set; }
}
