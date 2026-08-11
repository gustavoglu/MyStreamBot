using Microsoft.EntityFrameworkCore;
using MyStreamBot.Application.Abstractions;
using MyStreamBot.Core.Entities;
using MyStreamBot.Core.Enums;
using MyStreamBot.Infrastructure.Persistence;

namespace MyStreamBot.Infrastructure.Repositories;

public sealed class SqliteUserRepository(
    MyStreamBotDbContext db) : IUserRepository
{
    public async Task<StreamerUser> GetOrCreateAsync(
        Platform platform,
        string platformUserId,
        string username,
        string? avatarUrl,
        CancellationToken ct = default)
    {
        var user = await db.Users
            .SingleOrDefaultAsync(
                x =>
                    x.Platform == platform &&
                    x.PlatformUserId == platformUserId,
                ct);

        if (user is not null)
        {
            user.Username = username;

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                user.AvatarUrl = avatarUrl;
            }

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

    public async Task<MessageRewardResult> TryRewardMessageAsync(
        StreamerUser user,
        string normalizedMessage,
        string sourceMessageKey,
        long amount,
        PointTransactionType type,
        string? description,
        CancellationToken ct = default)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "A quantidade de pontos deve ser maior que zero.");
        }

        if (string.IsNullOrWhiteSpace(sourceMessageKey))
        {
            throw new ArgumentException(
                "A chave da mensagem não pode ser vazia.",
                nameof(sourceMessageKey));
        }

        await using var transaction =
            await db.Database.BeginTransactionAsync(ct);

        // Primeira barreira: o mesmo evento do AxelChat já gerou uma
        // PointTransaction em alguma execução anterior.
        var alreadyProcessed =
            await db.PointTransactions
                .AnyAsync(
                    x => x.SourceMessageKey == sourceMessageKey,
                    ct);

        if (alreadyProcessed)
        {
            user.LastChatMessageNormalized = normalizedMessage;
            user.LastSeenAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new MessageRewardResult(
                Rewarded: false,
                AlreadyProcessed: true,
                RepeatedMessage: false,
                Balance: user.Points);
        }

        // Segunda barreira: a mensagem é igual à última mensagem recebida
        // deste usuário, inclusive após reiniciar o Worker.
        var repeatedMessage =
            string.Equals(
                user.LastChatMessageNormalized,
                normalizedMessage,
                StringComparison.Ordinal);

        user.LastChatMessageNormalized = normalizedMessage;
        user.LastSeenAtUtc = DateTime.UtcNow;

        if (repeatedMessage)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new MessageRewardResult(
                Rewarded: false,
                AlreadyProcessed: false,
                RepeatedMessage: true,
                Balance: user.Points);
        }

        checked
        {
            user.Points += amount;
            user.Xp += amount;
        }

        db.PointTransactions.Add(
            new PointTransaction
            {
                UserId = user.Id,
                Amount = amount,
                Type = type,
                Description = description,
                CreatedAtUtc = DateTime.UtcNow,
                SourceMessageKey = sourceMessageKey
            });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new MessageRewardResult(
            Rewarded: true,
            AlreadyProcessed: false,
            RepeatedMessage: false,
            Balance: user.Points);
    }

    public async Task SaveAsync(
        StreamerUser user,
        CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StreamerUser>> GetTopAsync(
        int count,
        CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return [];
        }

        return await db.Users
            .OrderByDescending(x => x.Points)
            .ThenBy(x => x.Username)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
