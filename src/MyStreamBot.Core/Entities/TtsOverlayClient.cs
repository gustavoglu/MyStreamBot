namespace MyStreamBot.Core.Entities;

public sealed class TtsOverlayClient
{
    public long Id { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public long LastEventId { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
