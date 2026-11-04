using MassTransit;
using Microsoft.EntityFrameworkCore;
using TodoSaaS.Api.Models;
using TodoSaaS.Api.Services;

namespace TodoSaaS.Api.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TodoItem> Todos => Set<TodoItem>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // MassTransit Transactional Outbox entities
        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();

        // Webhook Idempotency
        builder.Entity<ProcessedWebhookEvent>()
               .HasKey(e => e.Id);

        // Security: Multi-tenant global query filter ensures tenant A NEVER sees tenant B's data
        builder.Entity<TodoItem>()
               .HasQueryFilter(t => t.TenantId == _tenantContext.TenantId);

        // Multi-tenant audit trail query filter
        builder.Entity<AuditLog>()
               .HasQueryFilter(a => a.TenantId == _tenantContext.TenantId);

        // Indexes for performance
        builder.Entity<TodoItem>()
               .HasIndex(t => new { t.TenantId, t.IsCompleted });

        builder.Entity<AuditLog>()
               .HasIndex(a => new { a.TenantId, a.TimestampUtc });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<IMustHaveTenant>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.TenantId = _tenantContext.TenantId;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        foreach (var entry in ChangeTracker.Entries<IMustHaveTenant>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.TenantId = _tenantContext.TenantId;
            }
        }
        return base.SaveChanges();
    }
}
