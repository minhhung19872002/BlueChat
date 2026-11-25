using System.Net;
using System.Text.Json;
using BlueChat.Gateway;
using BlueChat.Infrastructure.Persistence;
using BlueChat.Infrastructure.Realtime;
using BlueChat.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueChat.Gateway.Tests;

[Collection(GatewayCollection.Name)]
public sealed class EventDeliveryTests(GatewayFixture f)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private async Task<(Guid TenantId, Guid UserId)> IdsAsync(TestUser u)
    {
        using var scope = f.Node1.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueChatDbContext>();
        var row = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.BlueIdUserId == u.Subject);
        return (row.TenantId, row.Id);
    }

    private static UserEvent Event(Guid tenant, Guid user, int seq, string source = "test") =>
        new(Guid.NewGuid(), tenant, user, "test.tick", JsonSerializer.SerializeToElement(new { seq, source }), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Every_connection_of_the_user_gets_each_event_once_in_order_across_two_nodes_and_nobody_else_does()
    {
        var tenant1 = "t1-" + Guid.NewGuid().ToString("N")[..8];
        var tenant2 = "t2-" + Guid.NewGuid().ToString("N")[..8];
        var a = TestUser.New(tenant1, "An");
        var b = TestUser.New(tenant1, "Bình");
        var c = TestUser.New(tenant2, "Chi");

        await using var a1 = await GatewayFixture.ConnectAsync(f.Url1, f.BlueId.Token(a));
        await using var a2 = await GatewayFixture.ConnectAsync(f.Url1, f.BlueId.Token(a));
        await using var a3 = await GatewayFixture.ConnectAsync(f.Url2, f.BlueId.Token(a));
        await using var b1 = await GatewayFixture.ConnectAsync(f.Url1, f.BlueId.Token(b));
        await using var c1 = await GatewayFixture.ConnectAsync(f.Url2, f.BlueId.Token(c));

        var (tA, uA) = await IdsAsync(a);
        var (tC, _) = await IdsAsync(c);
        var publisher = new ValkeyUserEventPublisher(f.Valkey);
        const int N = 200;
        for (var i = 0; i < N; i++)
        {
            await publisher.PublishAsync(Event(tA, uA, i));
        }

        // An event naming A's user id under ANOTHER tenant must never reach A (tenant guard).
        await publisher.PublishAsync(Event(tC, uA, -1, "foreign-tenant"));

        foreach (var conn in new[] { a1, a2, a3 })
        {
            await conn.WaitForAsync(N, Wait);
            var seqs = conn.Events.Select(e => e.GetProperty("payload").GetProperty("seq").GetInt32()).ToList();
            seqs.Should().Equal(Enumerable.Range(0, N), "each connection gets every event once, in publish order");
        }

        await Task.Delay(500); // give a stray delivery the chance to show up
        b1.Events.Should().BeEmpty();
        c1.Events.Should().BeEmpty();
        foreach (var conn in new[] { a1, a2, a3 })
        {
            conn.Events.Should().HaveCount(N, "the foreign-tenant event was not delivered");
        }
    }

    [Fact]
    public async Task Events_published_truly_in_parallel_arrive_exactly_once_per_connection()
    {
        var a = TestUser.New("tp-" + Guid.NewGuid().ToString("N")[..8], "An");
        await using var a1 = await GatewayFixture.ConnectAsync(f.Url1, f.BlueId.Token(a));
        await using var a2 = await GatewayFixture.ConnectAsync(f.Url2, f.BlueId.Token(a));
        var (t, u) = await IdsAsync(a);

        const int Writers = 8, PerWriter = 50;
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            await start.Task;
            var publisher = new ValkeyUserEventPublisher(f.Valkey);
            for (var i = 0; i < PerWriter; i++)
            {
                await publisher.PublishAsync(Event(t, u, (w * 1000) + i));
            }
        })).ToList();
        start.SetResult();
        await Task.WhenAll(tasks);

        foreach (var conn in new[] { a1, a2 })
        {
            await conn.WaitForAsync(Writers * PerWriter, Wait);
            var ids = conn.Events.Select(e => e.GetProperty("id").GetGuid()).ToList();
            ids.Should().HaveCount(Writers * PerWriter);
            ids.Should().OnlyHaveUniqueItems();

            // Within one writer, order is kept.
            var seqs = conn.Events.Select(e => e.GetProperty("payload").GetProperty("seq").GetInt32()).ToList();
            foreach (var w in Enumerable.Range(0, Writers))
            {
                seqs.Where(s => s / 1000 == w).Should().BeInAscendingOrder();
            }
        }
    }

    [Fact]
    public async Task Kick_closes_every_connection_of_the_user_on_both_nodes_and_no_one_else()
    {
        var tenant = "tk-" + Guid.NewGuid().ToString("N")[..8];
        var a = TestUser.New(tenant, "An");
        var b = TestUser.New(tenant, "Bình");
        await using var a1 = await GatewayFixture.ConnectAsync(f.Url1, f.BlueId.Token(a));
        await using var a2 = await GatewayFixture.ConnectAsync(f.Url2, f.BlueId.Token(a));
        await using var b1 = await GatewayFixture.ConnectAsync(f.Url1, f.BlueId.Token(b));
        var (t, u) = await IdsAsync(a);

        await ValkeyRelay.PublishKickAsync(f.Valkey, new UserControl(t, u, "account_locked"));

        await a1.Closed.WaitAsync(Wait);
        await a2.Closed.WaitAsync(Wait);
        a1.Connection.State.Should().Be(HubConnectionState.Disconnected);
        a2.Connection.State.Should().Be(HubConnectionState.Disconnected);
        (await b1.Connection.InvokeAsync<DateTimeOffset>(nameof(EventsHub.Ping))).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Connection_cap_per_user_holds_across_nodes_and_frees_a_slot_on_disconnect()
    {
        var a = TestUser.New("tc-" + Guid.NewGuid().ToString("N")[..8], "An");
        var open = new List<Client>();
        for (var i = 0; i < GatewayFixture.MaxConnectionsPerUser; i++)
        {
            open.Add(await GatewayFixture.ConnectAsync(i % 2 == 0 ? f.Url1 : f.Url2, f.BlueId.Token(a)));
        }

        foreach (var c in open)
        {
            (await c.Connection.InvokeAsync<DateTimeOffset>(nameof(EventsHub.Ping))).Should().NotBe(default);
        }

        // One over the cap, on either node: refused.
        await AssertRefusedAsync(f.Url2, f.BlueId.Token(a), "too_many_connections");

        await open[0].Connection.StopAsync();
        await open[0].DisposeAsync();
        await using var again = await RetryConnectAsync(f.Url1, f.BlueId.Token(a));
        (await again.Connection.InvokeAsync<DateTimeOffset>(nameof(EventsHub.Ping))).Should().NotBe(default);

        foreach (var c in open.Skip(1))
        {
            await c.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("expired")]
    [InlineData("foreign-key")]
    [InlineData("wrong-audience")]
    public async Task Negotiate_without_a_valid_BlueID_token_is_401(string kind)
    {
        var u = TestUser.New("ta-" + Guid.NewGuid().ToString("N")[..8], "An");
        string? token = kind switch
        {
            "expired" => f.BlueId.Token(u, lifetime: TimeSpan.FromSeconds(-50)), // past the 30 s clock skew
            "foreign-key" => f.BlueId.Token(u, signWithForeignKey: true),
            "wrong-audience" => f.BlueId.Token(u, audience: "someone-else"),
            _ => null,
        };
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, f.Url1 + EventsHub.Path + "/negotiate?negotiateVersion=1");
        if (token is not null)
        {
            request.With(token);
        }

        (await http.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var ok = new HttpRequestMessage(HttpMethod.Post, f.Url1 + EventsHub.Path + "/negotiate?negotiateVersion=1").With(f.BlueId.Token(u));
        (await http.SendAsync(ok)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_health_includes_valkey_subscriptions()
    {
        using var http = new HttpClient();
        (await http.GetAsync(f.Url1 + "/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetAsync(f.Url2 + "/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task AssertRefusedAsync(string url, string token, string reason)
    {
        Client? c = null;
        try
        {
            c = await GatewayFixture.ConnectAsync(url, token);
            var closed = await c.Closed.WaitAsync(Wait);
            closed.Should().NotBeNull();
            closed!.Message.Should().Contain(reason);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            e.ToString().Should().Contain(reason);
        }
        finally
        {
            if (c is not null)
            {
                await c.DisposeAsync();
            }
        }
    }

    /// <summary>The slot is released in OnDisconnectedAsync on the server, a moment after the client stops.</summary>
    private static async Task<Client> RetryConnectAsync(string url, string token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var c = await GatewayFixture.ConnectAsync(url, token);
            try
            {
                await c.Connection.InvokeAsync<DateTimeOffset>(nameof(EventsHub.Ping));
                return c;
            }
            catch (Exception) when (attempt < 20)
            {
                await c.DisposeAsync();
                await Task.Delay(100);
            }
        }
    }
}
