using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TradingBot.Web.Data;

public sealed class OwlNestDbContext : IdentityDbContext<AppUser>
{
    public OwlNestDbContext(DbContextOptions<OwlNestDbContext> opts) : base(opts) { }

    public DbSet<UserApiKeys> UserApiKeys => Set<UserApiKeys>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<UserDecision> UserDecisions => Set<UserDecision>();
    public DbSet<UserPosition> UserPositions => Set<UserPosition>();
    public DbSet<UserOrder> UserOrders => Set<UserOrder>();
    public DbSet<UserEquitySnapshot> UserEquitySnapshots => Set<UserEquitySnapshot>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<UserGateCall> UserGateCalls => Set<UserGateCall>();
    public DbSet<UserWatchlistEvent> UserWatchlistEvents => Set<UserWatchlistEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<UserApiKeys>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserSettings>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserDecision>().HasIndex(x => new { x.UserId, x.Id });
        b.Entity<UserPosition>().HasIndex(x => new { x.UserId, x.Ticker }).IsUnique();
        b.Entity<UserOrder>().HasIndex(x => new { x.UserId, x.Id });
        b.Entity<UserEquitySnapshot>().HasIndex(x => new { x.UserId, x.Id });
        b.Entity<ContactMessage>().HasIndex(x => new { x.Handled, x.Id });
        b.Entity<InviteCode>().HasIndex(x => x.Code).IsUnique();
        b.Entity<InviteCode>().HasIndex(x => x.UsedAtUtc);
        // Per-user newest-first lookup for the gate-calls audit feed.
        b.Entity<UserGateCall>().HasIndex(x => new { x.UserId, x.Id });
        b.Entity<UserWatchlistEvent>().HasIndex(x => new { x.UserId, x.Id });
        b.Entity<UserWatchlistEvent>().Property(e => e.PriceUsd).HasPrecision(18, 4);
        b.Entity<UserDecision>().Property(d => d.PriceUsd).HasPrecision(18, 4);
        b.Entity<UserOrder>().Property(o => o.PriceAtSubmitUsd).HasPrecision(18, 4);

        // Keep money/price precision explicit for providers that honor decimal facets.
        // decimal(18,4) = up to 99,999,999,999,999.9999 — plenty for equity / prices / shares.
        b.Entity<UserPosition>().Property(p => p.AverageEntryPrice).HasPrecision(18, 4);
        b.Entity<UserPosition>().Property(p => p.MarketValue).HasPrecision(18, 4);
        b.Entity<UserPosition>().Property(p => p.PeakPrice).HasPrecision(18, 4);
        b.Entity<UserPosition>().Property(p => p.UnrealizedPnL).HasPrecision(18, 4);
        b.Entity<UserOrder>().Property(o => o.FilledAvgPrice).HasPrecision(18, 4);
        b.Entity<UserEquitySnapshot>().Property(e => e.Equity).HasPrecision(18, 4);
        b.Entity<UserEquitySnapshot>().Property(e => e.Cash).HasPrecision(18, 4);
        b.Entity<UserEquitySnapshot>().Property(e => e.BuyingPower).HasPrecision(18, 4);
    }
}
