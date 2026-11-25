using BlueChat.Gateway;
using BlueChat.Infrastructure.Persistence;
using BlueChat.TestSupport;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace BlueChat.Gateway.Tests;

/// <summary>
/// PostgreSQL + Valkey (containers), the test BlueID issuer, and TWO real Gateway nodes on loopback ports sharing
/// one Valkey — the multi-node setup of spec §2.
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    public const int MaxConnectionsPerUser = 4;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly IContainer _valkey = new ContainerBuilder("valkey/valkey:8.1.12-alpine3.24")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    public TestBlueId BlueId { get; private set; } = null!;

    public WebApplication Node1 { get; private set; } = null!;

    public WebApplication Node2 { get; private set; } = null!;

    public string Url1 { get; private set; } = "";

    public string Url2 { get; private set; } = "";

    public IConnectionMultiplexer Valkey { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _valkey.StartAsync());
        BlueId = await TestBlueId.StartAsync();
        var valkey = $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)}";
        Valkey = await ConnectionMultiplexer.ConnectAsync(valkey);
        var logs = Path.Combine(Path.GetTempPath(), "bc-gateway-tests");
        var config = new Dictionary<string, string?>
        {
            ["BC_DB_CONNECTION"] = _postgres.GetConnectionString(),
            ["BC_VALKEY"] = valkey,
            ["BC_BLUEID_AUTHORITY"] = BlueId.Authority,
            ["BC_BLUEID_ALLOW_HTTP"] = "true",
            ["BC_WEB_URL"] = "http://web.bluechat.test",
            ["BC_LOG_DIR"] = logs,
            ["BC_GATEWAY_MAX_CONNECTIONS_PER_USER"] = MaxConnectionsPerUser.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        (Node1, Url1) = await StartNodeAsync(config);
        (Node2, Url2) = await StartNodeAsync(config);
        using (var scope = Node1.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BlueChatDbContext>().Database.MigrateAsync();
        }

        await Node1.Services.GetRequiredService<ValkeyRelay>().Ready.WaitAsync(TimeSpan.FromSeconds(30));
        await Node2.Services.GetRequiredService<ValkeyRelay>().Ready.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<(WebApplication, string)> StartNodeAsync(Dictionary<string, string?> config)
    {
        var app = GatewayApp.Build([], b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration.AddInMemoryCollection(config);
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, url.TrimEnd('/'));
    }

    /// <summary>A hub client over WebSockets (the transport browsers use), collecting received events.</summary>
    public static async Task<Client> ConnectAsync(string nodeUrl, string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(nodeUrl + EventsHub.Path, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
            })
            .Build();
        var client = new Client(connection);
        await connection.StartAsync();
        return client;
    }

    public async Task DisposeAsync()
    {
        Valkey.Dispose();
        await Node1.DisposeAsync();
        await Node2.DisposeAsync();
        await BlueId.DisposeAsync();
        await _postgres.DisposeAsync();
        await _valkey.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<GatewayFixture>
{
    public const string Name = "gateway";
}

public sealed class Client : IAsyncDisposable
{
    private readonly List<System.Text.Json.JsonElement> _events = [];
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Client(HubConnection connection)
    {
        Connection = connection;
        connection.On<System.Text.Json.JsonElement>(EventsHub.EventMethod, e =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        });
        connection.Closed += e =>
        {
            _closed.TrySetResult(e);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public Task<Exception?> Closed => _closed.Task;

    public IReadOnlyList<System.Text.Json.JsonElement> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToList();
            }
        }
    }

    public async Task WaitForAsync(int count, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (Events.Count < count && DateTime.UtcNow < end)
        {
            await Task.Delay(20);
        }
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
