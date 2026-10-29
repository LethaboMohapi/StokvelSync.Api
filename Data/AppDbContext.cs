using Microsoft.EntityFrameworkCore;
using StokvelSync.Api.Domain;
namespace StokvelSync.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Stokvel> Stokvels => Set<Stokvel>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Contribution> Contributions => Set<Contribution>();
    public DbSet<RotationSlot> RotationSlots => Set<RotationSlot>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c) =>
        c.Properties<decimal>().HavePrecision(18, 2);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Phone).IsUnique();
        b.Entity<Stokvel>().HasIndex(x => x.JoinCode).IsUnique();
        b.Entity<Membership>().HasIndex(x => new { x.StokvelId, x.UserId }).IsUnique();
        b.Entity<Contribution>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<Contribution>().HasIndex(x => x.ProviderRef);
        b.Entity<RotationSlot>().HasIndex(x => new { x.StokvelId, x.CycleNumber }).IsUnique();
        b.Entity<LedgerEntry>().HasIndex(x => new { x.StokvelId, x.Id });
    }
}
