using BlueChat.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlueChat.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef migrations add`; no database connection is opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BlueChatDbContext>
{
    public BlueChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BlueChatDbContext>()
            .UseNpgsql("Host=localhost;Database=bluechat", n => n.MigrationsHistoryTable("__ef_migrations", "tenant"))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new BlueChatDbContext(options, new TenantContext());
    }
}
