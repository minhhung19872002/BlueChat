namespace BlueChat.Infrastructure.Tenancy;

/// <summary>
/// Tenant and user the current unit of work acts for, set once per request (from the validated BlueID token) or per
/// job. The EF Core global filter compares every tenant-owned row with <see cref="TenantId"/>; when it is null nothing
/// matches (closed by default, spec principle 7). Platform-wide code opts out with IgnoreQueryFilters() explicitly.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? UserId { get; }

    void Set(Guid tenantId, Guid? userId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public void Set(Guid tenantId, Guid? userId)
    {
        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException("The tenant of a unit of work cannot change once set.");
        }

        TenantId = tenantId;
        UserId = userId;
    }
}
