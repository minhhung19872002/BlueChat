namespace BlueChat.Domain;

/// <summary>
/// Columns every business table carries (spec §5): id, tenant_id, created/updated at+by, deleted_at (soft delete).
/// Unique indexes are filtered on <c>deleted_at IS NULL</c>.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>tenant.tenants. For this table tenant_id equals id.</summary>
public sealed class Tenant : Entity
{
    public required string Name { get; set; }

    /// <summary>Tenant id in BlueID (the <c>tid</c> claim of access tokens).</summary>
    public required string BlueIdTenantId { get; set; }

    public string Plan { get; set; } = "standard";

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>BCP 47, e.g. "vi" or "en".</summary>
    public string DefaultLocale { get; set; } = "en";

    /// <summary>IANA time zone, e.g. "Asia/Ho_Chi_Minh".</summary>
    public string DefaultTimeZone { get; set; } = "UTC";

    public string DataRegion { get; set; } = "default";

    /// <summary>jsonb: tenant limits (Z6 targets) overriding the platform defaults.</summary>
    public string Limits { get; set; } = "{}";
}

public enum UserStatus
{
    Active,
    Locked,
    Deleted,
}

/// <summary>
/// org.users_cache: read-only copy of a BlueID user (spec §2.1, §5). Filled from validated access-token claims on
/// each sign-in and, once BlueID publishes its directory API (docs/00 Z1, B3), from the directory sync.
/// </summary>
public sealed class UserCache : Entity
{
    /// <summary>Subject in BlueID (<c>sub</c>).</summary>
    public required string BlueIdUserId { get; set; }

    public required string DisplayName { get; set; }

    public string? Email { get; set; }

    public string? AvatarKey { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    public bool IsGuest { get; set; }

    public string? Department { get; set; }

    public string? JobTitle { get; set; }

    public Guid? ManagerId { get; set; }

    public DateTimeOffset UpdatedFromBlueIdAt { get; set; }
}

/// <summary>org.user_settings: one JSON document per user (theme, density, language, time zone, …).</summary>
public sealed class UserSettings : Entity
{
    public Guid UserId { get; set; }

    /// <summary>jsonb, validated against <c>UserSettingsDocument</c> in the application layer.</summary>
    public string Settings { get; set; } = "{}";
}

/// <summary>Built-in administrative roles of BlueChat (spec §7.2). Permissions per role are defined in code.</summary>
public enum AdminRole
{
    TenantAdmin,
    ComplianceOfficer,
    PlatformAdmin,
}

/// <summary>admin.role_assignments: which user holds which BlueChat admin role in a tenant.</summary>
public sealed class RoleAssignment : Entity
{
    public Guid UserId { get; set; }

    public AdminRole Role { get; set; }
}

public enum AuditResult
{
    Success,
    Denied,
    Failed,
}

/// <summary>audit.audit_logs: append-only, hash chain (spec §5, §10.2). Not an <see cref="Entity"/>: never updated or deleted.</summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    public string? Actor { get; set; }

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    public required string Action { get; set; }

    public string? Entity { get; set; }

    public string? EntityId { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public AuditResult Result { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public byte[] PrevHash { get; set; } = [];

    public byte[] Hash { get; set; } = [];
}
