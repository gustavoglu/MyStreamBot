using MyStreamBot.Core.Entities;
using MyStreamBot.Core.Enums;

namespace MyStreamBot.Application.Abstractions;

public interface IUserRepository
{
    Task<StreamerUser> GetOrCreateAsync(
        Platform platform,
        string platformUserId,
        string username,
        string? avatarUrl,
        CancellationToken ct = default);

    Task<MessageRewardResult> TryRewardMessageAsync(
        StreamerUser user,
        string normalizedMessage,
        string sourceMessageKey,
        long amount,
        PointTransactionType type,
        string? description,
        CancellationToken ct = default);

    Task SaveAsync(
        StreamerUser user,
        CancellationToken ct = default);

    Task<IReadOnlyList<StreamerUser>> GetTopAsync(
        int count,
        CancellationToken ct = default);
}

public sealed record MessageRewardResult(
    bool Rewarded,
    bool AlreadyProcessed,
    bool RepeatedMessage,
    long Balance);
