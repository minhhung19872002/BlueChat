namespace BlueChat.Domain;

/// <summary>
/// Administrative permissions <c>chat.admin.&lt;group&gt;.&lt;action&gt;</c> (spec §7.2). Every admin endpoint declares one
/// with [RequirePermission]. New permissions are added here and to a role in <see cref="RolePermissions"/>.
/// </summary>
public static class Permissions
{
    public const string AuditView = "chat.admin.audit.view";
    public const string TenantView = "chat.admin.tenant.view";
    public const string TenantEdit = "chat.admin.tenant.edit";
    public const string RolesView = "chat.admin.roles.view";
    public const string RolesEdit = "chat.admin.roles.edit";

    /// <summary>Platform operator (all tenants).</summary>
    public const string PlatformTenants = "chat.platform.tenants.edit";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditView, TenantView, TenantEdit, RolesView, RolesEdit, PlatformTenants,
    };
}

public static class RolePermissions
{
    public static IReadOnlySet<string> Of(AdminRole role) => role switch
    {
        AdminRole.TenantAdmin => new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.AuditView, Permissions.TenantView, Permissions.TenantEdit, Permissions.RolesView, Permissions.RolesEdit,
        },
        AdminRole.ComplianceOfficer => new HashSet<string>(StringComparer.Ordinal) { Permissions.AuditView, Permissions.TenantView },
        AdminRole.PlatformAdmin => Permissions.All,
        _ => new HashSet<string>(),
    };
}
