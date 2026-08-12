using MyStreamBot.Core.Enums;

namespace MyStreamBot.Core.Entities;

public sealed class PointTransaction
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public long Amount { get; set; }

    public PointTransactionType Type { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Identificador determinístico do evento que originou a recompensa.
    /// É único para impedir que o mesmo evento recebido novamente pelo AxelChat
    /// gere uma segunda recompensa depois de um restart/reconexão.
    /// </summary>
    public string? SourceMessageKey { get; set; }

    public StreamerUser? User { get; set; }
}
