using System.Net.Http.Json;
using System.Text.Json;
using BlueChat.Infrastructure.Persistence;
using BlueChat.Infrastructure.Tenancy;
using BlueChat.TestSupport;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace BlueChat.IntegrationTests;

/// <summary>Real PostgreSQL 16 + Valkey (containers), the test BlueID issuer, and the API in-process.</summary>
public sealed class StackFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16.16-alpine")
        .WithDatabase("bluechat").WithUsername("bluechat").WithPassword("bluechat").Build();

    private readonly IContainer _valkey = new ContainerBuilder("valkey/valkey:8.1.12-alpine3.24")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections")).Build();

    public TestBlueId BlueId { get; private set; } = null!;

    public WebApplicationFactory<Program> Api { get; private set; } = null!;

    public string ValkeyConnection => $"127.0.0.1:{_valkey.GetMappedPublicPort(6379)}";

    public const string BootstrapAdmin = "admin@acme.test";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _valkey.StartAsync());
        BlueId = await TestBlueId.StartAsync();
        // Program reads BC_* at the top of Main: environment variables are visible there.
        Environment.SetEnvironmentVariable("BC_DB_CONNECTION", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("BC_VALKEY", ValkeyConnection);
        Environment.SetEnvironmentVariable("BC_BLUEID_AUTHORITY", BlueId.Authority);
        Environment.SetEnvironmentVariable("BC_BLUEID_ALLOW_HTTP", "true");
        Environment.SetEnvironmentVariable("BC_WEB_URL", "https://bluechat.test");
        Environment.SetEnvironmentVariable("BC_BOOTSTRAP_ADMINS", BootstrapAdmin);
        Environment.SetEnvironmentVariable("BC_LOG_DIR", Path.Combine(Path.GetTempPath(), "bluechat-tests-logs"));
        Api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseEnvironment(b, "Testing"));
        _ = Api.Server; // start: applies migrations
    }

    public HttpClient Client(string? token = null, string? language = null)
    {
        var c = Api.CreateClient();
        if (token is not null)
        {
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        if (language is not null)
        {
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        }

        return c;
    }

    /// <summary>A DbContext acting for <paramref name="tenantId"/> (or none: closed filter).</summary>
    public (AsyncServiceScope Scope, BlueChatDbContext Db) Db(Guid? tenantId = null)
    {
        var scope = Api.Services.CreateAsyncScope();
        if (tenantId is { } t)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(t, null);
        }

        return (scope, scope.ServiceProvider.GetRequiredService<BlueChatDbContext>());
    }

    public static async Task<JsonElement> DataAsync(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        return body.GetProperty("data");
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await BlueId.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _valkey.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}
