using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlueChat.Infrastructure.Realtime;
using BlueChat.TestSupport;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace BlueChat.IntegrationTests;

[Collection(StackCollection.Name)]
public sealed class IdentityAndSettingsTests(StackFixture stack)
{
    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Health_endpoints_report_live_and_ready()
    {
        using var c = stack.Client();
        (await c.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_a_token_the_api_answers_401_with_a_stable_code_in_the_callers_language()
    {
        using var c = stack.Client(language: "vi-VN");
        var r = await c.GetAsync("/api/v1/me");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var p = await r.Content.ReadFromJsonAsync<JsonElement>();
        p.GetProperty("code").GetString().Should().Be("unauthenticated");
        p.GetProperty("title").GetString().Should().Be("Hãy đăng nhập bằng BlueID để tiếp tục.");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("foreign-key")]
    [InlineData("wrong-audience")]
    public async Task Invalid_tokens_are_rejected(string kind)
    {
        var user = TestUser.New(NewTenant(), "Mallory");
        var token = kind switch
        {
            "expired" => stack.BlueId.Token(user, lifetime: TimeSpan.FromMinutes(-5)),
            "foreign-key" => stack.BlueId.Token(user, signWithForeignKey: true),
            _ => stack.BlueId.Token(user, audience: "someone-else"),
        };
        using var c = stack.Client(token);
        (await c.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task First_sign_in_provisions_tenant_and_user_from_blueid_claims()
    {
        var tid = NewTenant();
        var user = new TestUser("sub-" + Guid.NewGuid().ToString("N"), tid, "Nguyễn Thị Lan", "lan@acme.test", "ACME Việt Nam");
        using var c = stack.Client(stack.BlueId.Token(user));

        var me = await StackFixture.DataAsync(await c.GetAsync("/api/v1/me"));
        me.GetProperty("displayName").GetString().Should().Be("Nguyễn Thị Lan");
        me.GetProperty("email").GetString().Should().Be("lan@acme.test");
        me.GetProperty("tenantName").GetString().Should().Be("ACME Việt Nam");
        me.GetProperty("permissions").GetArrayLength().Should().Be(0);
        me.GetProperty("settings").GetProperty("theme").GetString().Should().Be("light");

        // A changed name in BlueID updates the cached row on the next request (after the short identity cache).
        var again = await StackFixture.DataAsync(await c.GetAsync("/api/v1/me"));
        again.GetProperty("userId").GetGuid().Should().Be(me.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Truly_parallel_first_sign_ins_create_one_tenant_and_one_user()
    {
        var tid = NewTenant();
        var user = TestUser.New(tid, "Parallel Pham", "pp@acme.test");
        var token = stack.BlueId.Token(user);
        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            using var c = stack.Client(token);
            await gate.WaitAsync();
            return await c.GetAsync("/api/v1/me");
        }).ToList();
        gate.Release(12);
        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        var userIds = new HashSet<Guid>();
        foreach (var r in responses)
        {
            userIds.Add((await StackFixture.DataAsync(r)).GetProperty("userId").GetGuid());
        }

        userIds.Should().ContainSingle();
        var (scope, db) = stack.Db();
        await using (scope)
        {
            (await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.BlueIdTenantId == tid)).Should().Be(1);
            (await db.Users.IgnoreQueryFilters().CountAsync(u => u.BlueIdUserId == user.Subject)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Settings_round_trip_and_invalid_values_are_refused_with_localized_reasons()
    {
        var user = TestUser.New(NewTenant(), "Settings Sơn");
        using var c = stack.Client(stack.BlueId.Token(user), "vi");
        var settings = new
        {
            theme = "dark", density = "compact", fontScale = 1.25, language = "vi", timeZone = "Asia/Ho_Chi_Minh",
            hourCycle = "h23", weekStart = 1, readReceiptsEnabled = false,
        };
        var saved = await StackFixture.DataAsync(await c.PutAsJsonAsync("/api/v1/me/settings", settings));
        saved.GetProperty("theme").GetString().Should().Be("dark");

        var read = await StackFixture.DataAsync(await c.GetAsync("/api/v1/me/settings"));
        read.GetProperty("timeZone").GetString().Should().Be("Asia/Ho_Chi_Minh");
        read.GetProperty("fontScale").GetDecimal().Should().Be(1.25m);
        read.GetProperty("readReceiptsEnabled").GetBoolean().Should().BeFalse();

        var invalid = await c.PutAsJsonAsync("/api/v1/me/settings", new { theme = "purple", density = "compact", fontScale = 3, timeZone = "Mars/Olympus" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var p = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        p.GetProperty("code").GetString().Should().Be("validation_failed");
        var errors = p.GetProperty("errors");
        errors.GetProperty("timeZone")[0].GetString().Should().Be("Không nhận ra múi giờ này.");
        errors.TryGetProperty("theme", out _).Should().BeTrue();
        errors.TryGetProperty("fontScale", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Parallel_first_settings_saves_keep_one_row_per_user()
    {
        var user = TestUser.New(NewTenant(), "Race Ruth");
        var token = stack.BlueId.Token(user);
        using (var warm = stack.Client(token))
        {
            await StackFixture.DataAsync(await warm.GetAsync("/api/v1/me"));
        }

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            using var c = stack.Client(token);
            await gate.WaitAsync();
            return await c.PutAsJsonAsync("/api/v1/me/settings", new { theme = i % 2 == 0 ? "dark" : "light", density = "compact", fontScale = 1 });
        }).ToList();
        gate.Release(8);
        (await Task.WhenAll(tasks)).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var (scope, db) = stack.Db();
        await using (scope)
        {
            var userId = await db.Users.IgnoreQueryFilters().Where(u => u.BlueIdUserId == user.Subject).Select(u => u.Id).SingleAsync();
            (await db.UserSettings.IgnoreQueryFilters().CountAsync(s => s.UserId == userId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Saving_settings_publishes_an_event_for_that_user_only_after_the_commit()
    {
        var user = TestUser.New(NewTenant(), "Event Ella");
        using var c = stack.Client(stack.BlueId.Token(user));
        var me = await StackFixture.DataAsync(await c.GetAsync("/api/v1/me"));
        var userId = me.GetProperty("userId").GetGuid();

        await using var valkey = await ConnectionMultiplexer.ConnectAsync(stack.ValkeyConnection);
        var received = new TaskCompletionSource<UserEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await valkey.GetSubscriber().SubscribeAsync(ValkeyUserEventPublisher.ChannelFor(userId), (_, v) =>
        {
            var e = JsonSerializer.Deserialize<UserEvent>((byte[])v!, ValkeyUserEventPublisher.Json)!;
            if (e.UserId == userId)
            {
                received.TrySetResult(e);
            }
        });

        await StackFixture.DataAsync(await c.PutAsJsonAsync("/api/v1/me/settings", new { theme = "contrast", density = "comfortable", fontScale = 1 }));
        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Type.Should().Be("settings.changed");
        evt.TenantId.Should().Be(me.GetProperty("tenantId").GetGuid());
        evt.Payload.GetProperty("theme").GetString().Should().Be("contrast");

        // The row was already committed when the event went out.
        var (scope, db) = stack.Db(evt.TenantId);
        await using (scope)
        {
            (await db.UserSettings.SingleAsync(s => s.UserId == userId)).Settings.Should().Contain("contrast");
        }
    }
}
