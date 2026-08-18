namespace MyStreamBot.Core.Entities;

public sealed class TtsBlockedViewer
{
    public long Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string PlatformUserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
