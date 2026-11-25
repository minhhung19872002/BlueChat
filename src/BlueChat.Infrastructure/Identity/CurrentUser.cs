using System.Security.Claims;
using BlueChat.Domain;
using BlueChat.Infrastructure.Audit;
using BlueChat.Infrastructure.Configuration;
using BlueChat.Infrastructure.Persistence;
using BlueChat.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace BlueChat.Infrastructure.Identity;

/// <summary>Who is calling, after <see cref="IdentityResolver"/> mapped the BlueID token to BlueChat rows.</summary>
public interface ICurrentUser
{
    bool IsResolved { get; }

    Guid TenantId { get; }

    Guid UserId { get; }

    string BlueIdUserId { get; }

    string DisplayName { get; }

    string? Email { get; }

    string? SessionId { get; }

    IReadOnlySet<string> Permissions { get; }

    bool Has(string permission);
}

public sealed class CurrentUser : ICurrentUser
{
    private ResolvedIdentity? _identity;

    public bool IsResolved => _identity is not null;

    public Guid TenantId => Identity.TenantId;

    public Guid UserId => Identity.UserId;

    public string BlueIdUserId => Identity.BlueIdUserId;

    public string DisplayName => Identity.DisplayName;

    public string? Email => Identity.Email;

    public string? SessionId { get; private set; }

    public IReadOnlySet<string> Permissions => Identity.Permissions;

    private ResolvedIdentity Identity => _identity ?? throw new InvalidOperationException("No resolved user in this unit of work.");

    public bool Has(string permission) => _identity?.Permissions.Contains(permission) == true;

    internal void Set(ResolvedIdentity identity, string? sessionId)
    {
        _identity = identity;
        SessionId = sessionId;
    }
}

public sealed record ResolvedIdentity(
    Guid TenantId, Guid UserId, string BlueIdUserId, string DisplayName, string? Email, IReadOnlySet<string> Permissions);

public enum ResolveFailure
{
    None,
    MissingClaims,
    TenantSuspended,
    UserLocked,
}

/// <summary>
/// Maps a validated BlueID principal to the BlueChat tenant and the cached user row, provisioning both on first sign-in
/// (BlueID is the authority for tenants and users, spec §2.1). "One tenant per BlueID tenant" and "one cached user per
/// subject" are unique indexes; concurrent first sign-ins converge on the same rows (lesson 1). Results are cached
/// briefly in memory so a request does not hit PostgreSQL for this.
/// </summary>
public sealed class IdentityResolver(
    BlueChatDbContext db, ITenantContext tenantContext, CurrentUser currentUser, IMemoryCache cache, BcSettings settings,
    AuditWriter audit, TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<ResolveFailure> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var sub = principal.FindFirstValue(BlueIdClaims.Subject);
        var tid = principal.FindFirstValue(BlueIdClaims.Tenant);
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(tid))
        {
            return ResolveFailure.MissingClaims;
        }

        var name = principal.FindFirstValue(BlueIdClaims.Name) ?? principal.FindFirstValue(BlueIdClaims.Email) ?? sub;
        var email = principal.FindFirstValue(BlueIdClaims.Email)?.ToLowerInvariant();
        var guest = string.Equals(principal.FindFirstValue(BlueIdClaims.Guest), "true", StringComparison.OrdinalIgnoreCase);
        var key = $"bc:identity:{tid}:{sub}:{name}:{email}:{guest}";

        if (!cache.TryGetValue(key, out CachedResolution? resolved) || resolved is null)
        {
            resolved = await LoadAsync(sub, tid, principal.FindFirstValue(BlueIdClaims.TenantName), name, email, guest, ct);
            cache.Set(key, resolved, CacheFor);
        }

        if (resolved.Failure != ResolveFailure.None)
        {
            return resolved.Failure;
        }

        tenantContext.Set(resolved.Identity!.TenantId, resolved.Identity.UserId);
        currentUser.Set(resolved.Identity, principal.FindFirstValue(BlueIdClaims.Session));
        return ResolveFailure.None;
    }

    private async Task<CachedResolution> LoadAsync(string sub, string tid, string? tenantName, string name, string? email,
        bool guest, CancellationToken ct)
    {
        var tenant = await FindOrCreateTenantAsync(tid, tenantName ?? tid, ct);
        if (tenant.Status != TenantStatus.Active)
        {
            return new CachedResolution(ResolveFailure.TenantSuspended, null);
        }

        tenantContext.Set(tenant.Id, null);
        var user = await UpsertUserAsync(tenant.Id, sub, name, email, guest, ct);
        if (user.Status != UserStatus.Active)
        {
            return new CachedResolution(ResolveFailure.UserLocked, null);
        }

        var roles = await db.RoleAssignments.Where(r => r.UserId == user.Id).Select(r => r.Role).ToListAsync(ct);
        if (roles.Count == 0 && email is not null && settings.BootstrapAdmins.Contains(email)
            && !await db.RoleAssignments.AnyAsync(r => r.Role == AdminRole.TenantAdmin, ct))
        {
            roles.Add(await BootstrapAdminAsync(tenant.Id, user.Id, email, ct));
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            permissions.UnionWith(RolePermissions.Of(role));
        }

        if (settings.PlatformTenants.Contains(tid) && roles.Contains(AdminRole.TenantAdmin))
        {
            permissions.UnionWith(RolePermissions.Of(AdminRole.PlatformAdmin));
        }

        return new CachedResolution(ResolveFailure.None,
            new ResolvedIdentity(tenant.Id, user.Id, sub, user.DisplayName, user.Email, permissions));
    }

    private async Task<Tenant> FindOrCreateTenantAsync(string tid, string name, CancellationToken ct)
    {
        var existing = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.BlueIdTenantId == tid && t.DeletedAt == null, ct);
        if (existing is not null)
        {
            return existing;
        }

        var tenant = new Tenant { Name = name, BlueIdTenantId = tid };
        tenant.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        try
        {
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("tenant.provisioned", AuditResult.Success, tenant.Id, Entity: "tenant",
                EntityId: tenant.Id.ToString("D"), NewValueJson: System.Text.Json.JsonSerializer.Serialize(new { tid, name })), ct);
            return tenant;
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.Entry(tenant).State = EntityState.Detached;
            return await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.BlueIdTenantId == tid && t.DeletedAt == null, ct);
        }
    }

    private async Task<UserCache> UpsertUserAsync(Guid tenantId, string sub, string name, string? email, bool guest, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.BlueIdUserId == sub, ct);
        if (user is null)
        {
            user = new UserCache
            {
                TenantId = tenantId,
                BlueIdUserId = sub,
                DisplayName = name,
                Email = email,
                IsGuest = guest,
                UpdatedFromBlueIdAt = now,
            };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
                return user;
            }
            catch (DbUpdateException e) when (IsUniqueViolation(e))
            {
                db.Entry(user).State = EntityState.Detached;
                return await db.Users.FirstAsync(u => u.BlueIdUserId == sub, ct);
            }
        }

        if (user.DisplayName != name || user.Email != email || user.IsGuest != guest)
        {
            user.DisplayName = name;
            user.Email = email;
            user.IsGuest = guest;
            user.UpdatedFromBlueIdAt = now;
            await db.SaveChangesAsync(ct);
        }

        return user;
    }

    private async Task<AdminRole> BootstrapAdminAsync(Guid tenantId, Guid userId, string email, CancellationToken ct)
    {
        db.RoleAssignments.Add(new RoleAssignment { TenantId = tenantId, UserId = userId, Role = AdminRole.TenantAdmin });
        try
        {
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("role.bootstrap", AuditResult.Success, tenantId, userId, email,
                Entity: "role_assignment", NewValueJson: "{\"role\":\"TenantAdmin\"}"), ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // A parallel first request of the same user already did it.
        }

        return AdminRole.TenantAdmin;
    }

    public static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed record CachedResolution(ResolveFailure Failure, ResolvedIdentity? Identity);
}
