using Microsoft.EntityFrameworkCore;
using MyStreamBot.Core.Entities;

namespace MyStreamBot.Infrastructure.Persistence;

public sealed class MyStreamBotDbContext(DbContextOptions<MyStreamBotDbContext> options) : DbContext(options)
{
    public DbSet<StreamerUser> Users => Set<StreamerUser>();
    public DbSet<PointTransaction> PointTransactions => Set<PointTransaction>();
    public DbSet<TtsOverlayEvent> TtsOverlayEvents => Set<TtsOverlayEvent>();
    public DbSet<TtsOverlayClient> TtsOverlayClients => Set<TtsOverlayClient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<StreamerUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Platform, x.PlatformUserId }).IsUnique();
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.PlatformUserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.AvatarUrl).HasMaxLength(1000).IsRequired(false);
            e.Property(x => x.LastChatMessageNormalized).HasMaxLength(4000).IsRequired(false);
        });
        modelBuilder.Entity<PointTransaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            e.HasIndex(x => x.SourceMessageKey).IsUnique();
            e.Property(x => x.SourceMessageKey).HasMaxLength(4096).IsRequired(false);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TtsOverlayEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.AvatarUrl).HasMaxLength(1000).IsRequired(false);
            e.Property(x => x.Text).HasMaxLength(4000).IsRequired();
            e.Property(x => x.AudioFileName).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.CreatedAtUtc);
        });
        modelBuilder.Entity<TtsOverlayClient>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ClientName).IsUnique();
            e.Property(x => x.ClientName).HasMaxLength(200).IsRequired();
        });
    }
}
