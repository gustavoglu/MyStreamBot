using Microsoft.EntityFrameworkCore;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Core.Entities;
using MyStreamBot.Core.Enums;
using MyStreamBot.Infrastructure.Persistence;

namespace MyStreamBot.Infrastructure.Repositories;

public sealed class SqliteUserRepository(MyStreamBotDbContext db) : IUserRepository
{
    public async Task<StreamerUser> GetOrCreateAsync(Platform platform, string platformUserId, string username, string? avatarUrl, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Platform == platform && x.PlatformUserId == platformUserId, ct);
        if (user is not null)
        {
            user.Username = username;
            if (!string.IsNullOrWhiteSpace(avatarUrl)) user.AvatarUrl = avatarUrl;
            user.LastSeenAtUtc = DateTime.UtcNow;
            return user;
        }

        user = new StreamerUser
        {
            Platform = platform,
            PlatformUserId = platformUserId,
            Username = username,
            AvatarUrl = avatarUrl,
            Points = 0,
            Xp = 0,
            Level = 1,
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<RewardResult> TryRewardAsync(StreamerUser user, string? normalizedMessage, string sourceEventKey, long amount, PointTransactionType type, string? description, bool enforceRepeatedMessageCheck, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(sourceEventKey)) throw new ArgumentException("A chave da mensagem não pode ser vazia.", nameof(sourceEventKey));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var alreadyProcessed = await db.PointTransactions.AnyAsync(x => x.SourceMessageKey == sourceEventKey, ct);
        if (alreadyProcessed)
        {
            if (enforceRepeatedMessageCheck && normalizedMessage is not null) user.LastChatMessageNormalized = normalizedMessage;
            user.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new RewardResult(false, true, false, user.Points);
        }

        var repeatedMessage = enforceRepeatedMessageCheck && normalizedMessage is not null && string.Equals(user.LastChatMessageNormalized, normalizedMessage, StringComparison.Ordinal);
        if (enforceRepeatedMessageCheck && normalizedMessage is not null) user.LastChatMessageNormalized = normalizedMessage;
        user.LastSeenAtUtc = DateTime.UtcNow;
        if (repeatedMessage)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new RewardResult(false, false, true, user.Points);
        }

        checked { user.Points += amount; user.Xp += amount; }
        db.PointTransactions.Add(new PointTransaction { UserId = user.Id, Amount = amount, Type = type, Description = description, CreatedAtUtc = DateTime.UtcNow, SourceMessageKey = sourceEventKey });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new RewardResult(true, false, false, user.Points);
    }

    public async Task<SpendResult> TrySpendAsync(StreamerUser user, string sourceEventKey, long amount, PointTransactionType type, string? description, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(sourceEventKey)) throw new ArgumentException("A chave da mensagem não pode ser vazia.", nameof(sourceEventKey));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var alreadyProcessed = await db.PointTransactions.AnyAsync(x => x.SourceMessageKey == sourceEventKey, ct);
        if (alreadyProcessed)
        {
            await transaction.CommitAsync(ct);
            return new SpendResult(false, true, false, user.Points);
        }

        if (user.Points < amount)
        {
            await transaction.CommitAsync(ct);
            return new SpendResult(false, false, true, user.Points);
        }

        checked { user.Points -= amount; }
        db.PointTransactions.Add(new PointTransaction
        {
            UserId = user.Id,
            Amount = -amount,
            Type = type,
            Description = description,
            CreatedAtUtc = DateTime.UtcNow,
            SourceMessageKey = sourceEventKey
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SpendResult(true, false, false, user.Points);
    }

    public async Task SaveAsync(StreamerUser user, CancellationToken ct = default) => await db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<StreamerUser>> GetTopAsync(int count, CancellationToken ct = default)
    {
        if (count <= 0) return [];
        return await db.Users.OrderByDescending(x => x.Points).ThenBy(x => x.Username).Take(count).AsNoTracking().ToListAsync(ct);
    }
}
