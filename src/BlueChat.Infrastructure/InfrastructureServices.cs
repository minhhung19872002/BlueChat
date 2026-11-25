using System.Globalization;
using BlueChat.Infrastructure.Audit;
using BlueChat.Infrastructure.Configuration;
using BlueChat.Infrastructure.Identity;
using BlueChat.Infrastructure.Persistence;
using BlueChat.Infrastructure.Realtime;
using BlueChat.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;
using StackExchange.Redis;

namespace BlueChat.Infrastructure;

public static class InfrastructureServices
{
    /// <summary>Every BlueChat process formats and parses with the invariant culture (spec §0.2, §13).</summary>
    public static void UseInvariantProcessCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    /// <summary>Serilog: structured JSON to stdout and a rolling file per process (spec §1).</summary>
    public static void AddBlueChatLogging(this IHostApplicationBuilder builder, BcSettings settings, string process)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("process", process)
            .WriteTo.Console(new CompactJsonFormatter())
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(settings.LogDirectory, $"{process}-.json"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        builder.Services.AddSerilog();
    }

    public static IServiceCollection AddBlueChatInfrastructure(this IServiceCollection services, BcSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<AuditColumnsInterceptor>();
        services.AddDbContext<BlueChatDbContext>((sp, o) => o
            .UseNpgsql(settings.DbConnection, n => n.MigrationsHistoryTable("__ef_migrations", "tenant"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditColumnsInterceptor>()));
        services.AddScoped<AuditWriter>();
        services.AddScoped<AuditChainVerifier>();

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(ValkeyOptions(settings.Valkey)));
        services.AddSingleton<IUserEventPublisher, ValkeyUserEventPublisher>();

        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddScoped<IdentityResolver>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<ValkeyHealthCheck>("valkey", tags: ["ready"]);
        return services;
    }

    public static ConfigurationOptions ValkeyOptions(string connection)
    {
        var o = ConfigurationOptions.Parse(connection);
        o.AbortOnConnectFail = false; // keep retrying; readiness reports the outage
        o.ClientName = "bluechat";
        return o;
    }

    /// <summary>/health/live (process up) and /health/ready (PostgreSQL and Valkey reachable).</summary>
    public static void MapBlueChatHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
    }

    /// <summary>
    /// After authentication: map the BlueID principal to tenant + user (provisioning on first sign-in). Anonymous
    /// requests pass through untouched; endpoints that need a user are guarded by authorization.
    /// </summary>
    public static IApplicationBuilder UseBlueChatIdentity(this IApplicationBuilder app, Func<HttpContext, ResolveFailure, Task> onFailure) =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                var resolver = ctx.RequestServices.GetRequiredService<IdentityResolver>();
                var failure = await resolver.ResolveAsync(ctx.User, ctx.RequestAborted);
                if (failure != ResolveFailure.None)
                {
                    await onFailure(ctx, failure);
                    return;
                }
            }

            await next(ctx);
        });
}

internal sealed class DatabaseHealthCheck(BlueChatDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("postgres unreachable");
}

internal sealed class ValkeyHealthCheck(IConnectionMultiplexer valkey) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await valkey.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (RedisException e)
        {
            return HealthCheckResult.Unhealthy("valkey unreachable", e);
        }
    }
}
