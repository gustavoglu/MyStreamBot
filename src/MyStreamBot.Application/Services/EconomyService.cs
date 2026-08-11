using MyStreamBot.Application.Abstractions;
using MyStreamBot.Core.Enums;

namespace MyStreamBot.Application.Services;

public sealed class EconomyService(
    IUserRepository users)
{
    public async Task<RewardResult> TryRewardAsync(
        Platform platform,
        string platformUserId,
        string username,
        string? avatarUrl,
        string? normalizedMessage,
        string sourceEventKey,
        long amount,
        PointTransactionType type,
        string? description = null,
        bool enforceRepeatedMessageCheck = false,
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

        return await users.TryRewardAsync(
            user,
            normalizedMessage,
            sourceEventKey,
            amount,
            type,
            description,
            enforceRepeatedMessageCheck,
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
