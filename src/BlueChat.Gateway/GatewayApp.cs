using BlueChat.Infrastructure;
using BlueChat.Infrastructure.Configuration;
using BlueChat.Infrastructure.Identity;
using Microsoft.AspNetCore.HttpOverrides;

namespace BlueChat.Gateway;

/// <summary>Builds the Gateway process (also used by tests to start several real nodes on loopback ports).</summary>
public static class GatewayApp
{
    public const string CorsPolicy = "web";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        InfrastructureServices.UseInvariantProcessCulture();
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var settings = BcSettings.Load(builder.Configuration);
        var options = GatewayOptions.Load(builder.Configuration);

        builder.AddBlueChatLogging(settings, "gateway");
        builder.Services.AddBlueChatInfrastructure(settings);
        builder.Services.AddBlueIdAuthentication(settings);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<UserConnectionLimiter>();
        builder.Services.AddSingleton<ValkeyRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ValkeyRelay>());
        builder.Services.AddHealthChecks().AddCheck<RelayHealthCheck>("relay", tags: ["ready"]);
        builder.Services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = options.MaxMessageBytes;
            o.KeepAliveInterval = options.KeepAlive;
            o.ClientTimeoutInterval = options.ClientTimeout;
            o.EnableDetailedErrors = false;
        });
        builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p.WithOrigins(settings.WebUrl)
            .AllowAnyHeader().WithMethods("GET", "POST").AllowCredentials()));
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in settings.TrustedProxies)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseBlueChatIdentity((ctx, failure) =>
        {
            ctx.Response.StatusCode = failure == ResolveFailure.MissingClaims ? 401 : 403;
            return ctx.Response.WriteAsJsonAsync(new { code = failure switch
            {
                ResolveFailure.TenantSuspended => "tenant_suspended",
                ResolveFailure.UserLocked => "user_locked",
                _ => "missing_tenant_claim",
            } });
        });
        app.UseAuthorization();
        app.MapBlueChatHealth();
        app.MapHub<EventsHub>(EventsHub.Path, o =>
        {
            // A connection must not outlive the BlueID token it was opened with.
            o.CloseOnAuthenticationExpiration = true;
            o.ApplicationMaxBufferSize = options.MaxMessageBytes;
        }).RequireCors(CorsPolicy);
        return app;
    }
}
