using MyStreamBot.Application.Abstractions;
using MyStreamBot.Core.Enums;

namespace MyStreamBot.Application.Services;

public sealed class EconomyService(
    IUserRepository users)
{
    public async Task<MessageRewardResult> TryRewardMessageAsync(
        Platform platform,
        string platformUserId,
        string username,
        string? avatarUrl,
        string normalizedMessage,
        string sourceMessageKey,
        long amount,
        PointTransactionType type,
        string? description = null,
        CancellationToken ct = default)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "A quantidade de pontos deve ser maior que zero.");
        }

        var user = await users.GetOrCreateAsync(
            platform,
            platformUserId,
            username,
            avatarUrl,
            ct);

        return await users.TryRewardMessageAsync(
            user,
            normalizedMessage,
            sourceMessageKey,
            amount,
            type,
            description,
            ct);
    }

    public async Task<long> GetBalanceAsync(
        Platform platform,
        string platformUserId,
        string username,
        CancellationToken ct = default)
    {
        var user = await users.GetOrCreateAsync(
            platform,
            platformUserId,
            username,
            null,
            ct);

        return user.Points;
    }
}
