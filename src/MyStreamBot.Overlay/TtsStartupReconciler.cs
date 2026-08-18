using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MyStreamBot.Infrastructure.Persistence;

namespace MyStreamBot.Overlay;

public sealed class TtsStartupReconciler(IDbContextFactory<MyStreamBotDbContext> factory, ILogger<TtsStartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS TtsOverlayEvents (Id INTEGER NOT NULL CONSTRAINT PK_TtsOverlayEvents PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, AvatarUrl TEXT NULL, Text TEXT NOT NULL, AudioFileName TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, IsRepeat INTEGER NOT NULL DEFAULT 0, IsCanceled INTEGER NOT NULL DEFAULT 0);", cancellationToken);
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE TtsOverlayEvents ADD COLUMN IsCanceled INTEGER NOT NULL DEFAULT 0;", cancellationToken); } catch { }
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS TtsOverlayClients (Id INTEGER NOT NULL CONSTRAINT PK_TtsOverlayClients PRIMARY KEY AUTOINCREMENT, ClientName TEXT NOT NULL, LastEventId INTEGER NOT NULL DEFAULT 0, UpdatedAtUtc TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS IX_TtsOverlayClients_ClientName ON TtsOverlayClients (ClientName);", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS TtsAdminSettings (Id INTEGER NOT NULL CONSTRAINT PK_TtsAdminSettings PRIMARY KEY, AcceptCommands INTEGER NOT NULL DEFAULT 1, PublishEnabled INTEGER NOT NULL DEFAULT 1, UpdatedAtUtc TEXT NOT NULL); CREATE TABLE IF NOT EXISTS TtsBlockedViewers (Id INTEGER NOT NULL CONSTRAINT PK_TtsBlockedViewers PRIMARY KEY AUTOINCREMENT, Platform TEXT NOT NULL, PlatformUserId TEXT NOT NULL, Username TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS IX_TtsBlockedViewers_Platform_User ON TtsBlockedViewers (Platform, PlatformUserId);", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO TtsAdminSettings (Id, AcceptCommands, PublishEnabled, UpdatedAtUtc) VALUES (1, 1, 1, CURRENT_TIMESTAMP);", cancellationToken);
        var maxId = await db.TtsOverlayEvents.AsNoTracking().Select(x => (long?)x.Id).MaxAsync(cancellationToken) ?? 0;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TtsOverlayClients SET LastEventId = {maxId}, UpdatedAtUtc = CURRENT_TIMESTAMP;", cancellationToken);
        logger.LogInformation("[TTS] STARTUP_RECONCILED LastEventId={LastEventId}. Eventos antigos não serão reproduzidos após restart.", maxId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
