using BlueChat.Domain;
using BlueChat.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlueChat.Infrastructure.Persistence;

/// <summary>
/// One PostgreSQL database `bluechat` with schemas per area (spec §0.1, §5). Tenant-owned rows are filtered by the
/// current tenant and by soft delete; unique indexes are filtered on deleted_at IS NULL.
/// </summary>
public sealed class BlueChatDbContext(DbContextOptions<BlueChatDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<UserCache> Users => Set<UserCache>();

    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Read by the query filters at execution time (EF Core parameterises member access on the context).</summary>
    public Guid? CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Multilingual search (spec §0.2, §1): accent folding and trigram indexes.
        b.HasPostgresExtension("unaccent");
        b.HasPostgresExtension("pg_trgm");

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants", "tenant");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.BlueIdTenantId).HasMaxLength(200);
            e.Property(x => x.Plan).HasMaxLength(50);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DefaultLocale).HasMaxLength(35);
            e.Property(x => x.DefaultTimeZone).HasMaxLength(64);
            e.Property(x => x.DataRegion).HasMaxLength(50);
            e.Property(x => x.Limits).HasColumnType("jsonb");
            e.HasIndex(x => x.BlueIdTenantId).IsUnique().HasFilter("deleted_at IS NULL");
            // A tenant sees only itself.
            e.HasQueryFilter(x => x.Id == CurrentTenantId && x.DeletedAt == null);
        });

        b.Entity<UserCache>(e =>
        {
            e.ToTable("users_cache", "org");
            e.Property(x => x.BlueIdUserId).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.AvatarKey).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Department).HasMaxLength(200);
            e.Property(x => x.JobTitle).HasMaxLength(200);
            // "Only one cached user per BlueID subject per tenant" is a database rule (lesson 1).
            e.HasIndex(x => new { x.TenantId, x.BlueIdUserId }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasIndex(x => new { x.TenantId, x.Email });
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId && x.DeletedAt == null);
        });

        b.Entity<UserSettings>(e =>
        {
            e.ToTable("user_settings", "org");
            e.Property(x => x.Settings).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId && x.DeletedAt == null);
        });

        b.Entity<RoleAssignment>(e =>
        {
            e.ToTable("role_assignments", "admin");
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(40);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.Role }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId && x.DeletedAt == null);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs", "audit");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Actor).HasMaxLength(320);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.Entity).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.OldValue).HasColumnType("jsonb");
            e.Property(x => x.NewValue).HasColumnType("jsonb");
            e.Property(x => x.Result).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
        });

        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            b.Entity(type.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
            b.Entity(type.ClrType).HasIndex(nameof(Entity.TenantId));
        }
    }
}

/// <summary>
/// Fills created/updated columns and the tenant of new rows from the unit of work; refuses writes to another tenant's
/// rows and hard deletes of business rows (soft delete only, spec §5).
/// </summary>
public sealed class AuditColumnsInterceptor(ITenantContext tenant, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId == Guid.Empty)
                    {
                        entry.Entity.TenantId = entry.Entity is Tenant t ? t.Id
                            : tenant.TenantId ?? throw new InvalidOperationException("No tenant in this unit of work.");
                    }

                    EnsureTenant(entry.Entity);
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = tenant.UserId;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = tenant.UserId;
                    break;
                case EntityState.Modified:
                    EnsureTenant(entry.Entity);
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = tenant.UserId;
                    break;
                case EntityState.Deleted:
                    throw new InvalidOperationException(
                        $"Hard delete of {entry.Entity.GetType().Name} is not allowed; set DeletedAt (soft delete).");
            }
        }
    }

    private void EnsureTenant(Entity e)
    {
        if (tenant.TenantId is { } current && e.TenantId != current && e is not Tenant)
        {
            throw new InvalidOperationException("Cross-tenant write refused.");
        }
    }
}
