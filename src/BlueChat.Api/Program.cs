using System.Net;
using System.Threading.RateLimiting;
using BlueChat.Api.Web;
using BlueChat.Application;
using BlueChat.Infrastructure;
using BlueChat.Infrastructure.Configuration;
using BlueChat.Infrastructure.Identity;
using BlueChat.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;

InfrastructureServices.UseInvariantProcessCulture();

var builder = WebApplication.CreateBuilder(args);
var settings = BcSettings.Load(builder.Configuration);
builder.AddBlueChatLogging(settings, "api");
builder.Services.AddBlueChatInfrastructure(settings);
builder.Services.AddBlueChatApplication();
builder.Services.AddBlueIdAuthentication(settings);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ctx => Problems.Result(ctx.HttpContext, HttpStatusCode.BadRequest, "bad_request",
        ctx.ModelState.Where(kv => kv.Value?.Errors.Count > 0)
            .ToDictionary(kv => kv.Key, _ => BadRequestCode)));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(settings.WebUrl)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    .WithHeaders("Content-Type", "Authorization", "Accept-Language")
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var cidr in settings.TrustedProxies)
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});
builder.Services.AddRateLimiter(o =>
{
    // Per signed-in user (sub), else per client IP. Generous: protects the service, not a business limit.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.FindFirst(BlueIdClaims.Subject)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 200, TokensPerPeriod = 100, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0,
        }));
    o.OnRejected = (ctx, _) => new ValueTask(Problems.WriteAsync(ctx.HttpContext, HttpStatusCode.TooManyRequests, "rate_limited"));
});
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();

// One process applies migrations at start (EF Core takes a database lock, so parallel replicas are safe).
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<BlueChatDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseBlueChatExceptionHandler();
// Registered before authentication/authorization so their 401/403/404 without a body get a problem+json body.
app.UseStatusCodePages(async ctx =>
{
    var http = ctx.HttpContext;
    if (http.Response.HasStarted)
    {
        return;
    }

    var code = http.Response.StatusCode switch
    {
        401 => "unauthenticated",
        403 => "forbidden",
        404 => "not_found",
        405 => "bad_request",
        _ => null,
    };
    if (code is not null)
    {
        await Problems.WriteAsync(http, (HttpStatusCode)http.Response.StatusCode, code);
    }
});
app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseBlueChatIdentity((ctx, failure) => Problems.WriteAsync(ctx, HttpStatusCode.Forbidden, failure switch
{
    ResolveFailure.TenantSuspended => "tenant_suspended",
    ResolveFailure.UserLocked => "user_locked",
    _ => "missing_tenant_claim",
}));
app.UseRateLimiter();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapBlueChatHealth();
app.MapControllers();
await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program
{
    private static readonly string[] BadRequestCode = ["bad_request"];
}
