using MyStreamBot.Core.Enums;

namespace MyStreamBot.Core.Entities;

public sealed class StreamerUser
{
    public long Id { get; set; }

    public Platform Platform { get; set; }

    public string PlatformUserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public long Points { get; set; }

    public long Xp { get; set; }

    public int Level { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Última mensagem normalizada recebida deste usuário.
    /// É persistida para que o controle de mensagens repetidas
    /// continue funcionando depois que o Worker for reiniciado.
    /// </summary>
    public string? LastChatMessageNormalized { get; set; }
}
