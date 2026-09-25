using Microsoft.EntityFrameworkCore;
using ComplianceEngine.Models;

namespace ComplianceEngine.Data;

public class ComplianceDbContext : DbContext
{
    public ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RuleEvaluation> RuleEvaluations => Set<RuleEvaluation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tenants
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(256);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        // API keys (one tenant, many keys — one per MCP login session)
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyHash).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.Property(e => e.Label).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.ApiKeys)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Portfolios
        modelBuilder.Entity<Portfolio>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Portfolios)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();
        });

        // Holdings
        modelBuilder.Entity<Holding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Sector).HasMaxLength(50);
            entity.Property(e => e.Quantity).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.MarketValue).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.LastTradePrice).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.LastTradeAt).IsRequired();
            entity.HasOne(e => e.Portfolio)
                  .WithMany(p => p.Holdings)
                  .HasForeignKey(e => e.PortfolioId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.PortfolioId, e.Ticker }).IsUnique();
        });

        // Trades
        modelBuilder.Entity<Trade>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Sector).HasMaxLength(50);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(4);
            entity.Property(e => e.Quantity).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.Price).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(10);
            entity.Property(e => e.RequestedAt).IsRequired();
            entity.HasOne(e => e.Portfolio)
                  .WithMany()
                  .HasForeignKey(e => e.PortfolioId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.PortfolioId);
        });

        // Rules
        modelBuilder.Entity<Rule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.RuleType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Threshold).IsRequired().HasPrecision(18, 4);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Rules)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();
        });

        // RuleEvaluations
        modelBuilder.Entity<RuleEvaluation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Breached).IsRequired();
            entity.Property(e => e.CurrentValue).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.Threshold).IsRequired().HasPrecision(18, 4);
            entity.Property(e => e.EvaluatedAt).IsRequired();
            entity.HasOne(e => e.Rule)
                  .WithMany()
                  .HasForeignKey(e => e.RuleId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Portfolio)
                  .WithMany()
                  .HasForeignKey(e => e.PortfolioId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Trade)
                  .WithMany()
                  .HasForeignKey(e => e.TradeId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.PortfolioId);
        });

        // Add navigation properties
        modelBuilder.Entity<Tenant>()
            .HasMany(t => t.Rules)
            .WithOne(r => r.Tenant)
            .HasForeignKey(r => r.TenantId);

        // Configure table-level constraints for Trade entity
        modelBuilder.Entity<Trade>(entity =>
        {
            entity.ToTable(t => t.HasCheckConstraint("CK_Trade_Action", "[Action] IN ('BUY', 'SELL')"));
            entity.ToTable(t => t.HasCheckConstraint("CK_Trade_Status", "[Status] IN ('EXECUTED', 'BLOCKED')"));
            entity.ToTable(t => t.HasCheckConstraint("CK_Trade_Quantity", "[Quantity] > 0"));
            entity.ToTable(t => t.HasCheckConstraint("CK_Trade_Price", "[Price] > 0"));
        });
    }
}