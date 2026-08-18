namespace MyStreamBot.Core.Entities;

public sealed class TtsAdminSettings
{
    public int Id { get; set; }
    public bool AcceptCommands { get; set; } = true;
    public bool PublishEnabled { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
