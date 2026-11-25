using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueChat.Api.Web;
using BlueChat.Domain;
using BlueChat.Infrastructure.Localization;
using BlueChat.Infrastructure.Persistence;
using BlueChat.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BlueChat.IntegrationTests;

[Collection(StackCollection.Name)]
public sealed partial class SecurityTests(StackFixture stack)
{
    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<(Guid TenantId, Guid UserId, string Token)> SignInAsync(TestUser user)
    {
        var token = stack.BlueId.Token(user);
        using var c = stack.Client(token);
        var me = await StackFixture.DataAsync(await c.GetAsync("/api/v1/me"));
        return (me.GetProperty("tenantId").GetGuid(), me.GetProperty("userId").GetGuid(), token);
    }

    [Fact]
    public async Task Admin_endpoint_refuses_a_member_and_serves_the_bootstrap_admin()
    {
        var tid = NewTenant();
        var member = await SignInAsync(TestUser.New(tid, "Member Minh", "minh@acme.test"));
        using (var c = stack.Client(member.Token, "en"))
        {
            var r = await c.GetAsync("/api/v1/admin/audit/verify");
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("forbidden");
        }

        var admin = await SignInAsync(TestUser.New(tid, "Admin An", StackFixture.BootstrapAdmin));
        using var a = stack.Client(admin.Token);
        var me = await StackFixture.DataAsync(await a.GetAsync("/api/v1/me"));
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Contain(Permissions.AuditView);
        var v = await StackFixture.DataAsync(await a.GetAsync("/api/v1/admin/audit/verify"));
        v.GetProperty("valid").GetBoolean().Should().BeTrue();
        v.GetProperty("checked").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Bootstrap_admin_only_applies_while_the_tenant_has_no_admin()
    {
        var tid = NewTenant();
        await SignInAsync(TestUser.New(tid, "First", StackFixture.BootstrapAdmin));
        var second = await SignInAsync(TestUser.New(tid, "Second", StackFixture.BootstrapAdmin));
        using var c = stack.Client(second.Token);
        var me = await StackFixture.DataAsync(await c.GetAsync("/api/v1/me"));
        me.GetProperty("permissions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Tampering_with_an_audit_row_is_detected()
    {
        var tid = NewTenant();
        var admin = await SignInAsync(TestUser.New(tid, "Auditor", StackFixture.BootstrapAdmin));
        var (scope, db) = stack.Db();
        long id;
        string original;
        await using (scope)
        {
            var row = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == admin.TenantId).OrderBy(a => a.Id).FirstAsync();
            (id, original) = (row.Id, row.Action);
            await db.Database.ExecuteSqlRawAsync("UPDATE audit.audit_logs SET action = 'tampered' WHERE id = {0}", id);
        }

        try
        {
            using var c = stack.Client(admin.Token);
            var v = await StackFixture.DataAsync(await c.GetAsync("/api/v1/admin/audit/verify"));
            v.GetProperty("valid").GetBoolean().Should().BeFalse();
            v.GetProperty("firstBrokenId").GetInt64().Should().Be(id);
        }
        finally
        {
            // Put the original value back so the shared chain verifies again for the other tests.
            var (s2, db2) = stack.Db();
            await using (s2)
            {
                await db2.Database.ExecuteSqlRawAsync("UPDATE audit.audit_logs SET action = {0} WHERE id = {1}", original, id);
            }
        }
    }

    [Fact]
    public async Task Tenant_filter_hides_other_tenants_and_closed_filter_hides_everything()
    {
        var a = await SignInAsync(TestUser.New(NewTenant(), "Alice A"));
        var b = await SignInAsync(TestUser.New(NewTenant(), "Bob B"));

        var (sa, dba) = stack.Db(a.TenantId);
        await using (sa)
        {
            (await dba.Users.Select(u => u.Id).ToListAsync()).Should().Contain(a.UserId).And.NotContain(b.UserId);
            (await dba.Tenants.Select(t => t.Id).ToListAsync()).Should().Equal(a.TenantId);
        }

        var (sn, dbn) = stack.Db();
        await using (sn)
        {
            (await dbn.Users.AnyAsync()).Should().BeFalse("no tenant in the unit of work means no rows (closed by default)");
        }
    }

    [Fact]
    public async Task Cross_tenant_writes_and_hard_deletes_are_refused()
    {
        var a = await SignInAsync(TestUser.New(NewTenant(), "Writer A"));
        var b = await SignInAsync(TestUser.New(NewTenant(), "Victim B"));

        var (scope, db) = stack.Db(a.TenantId);
        await using (scope)
        {
            var victim = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == b.UserId);
            victim.DisplayName = "owned";
            var write = () => db.SaveChangesAsync();
            await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cross-tenant*");
            db.ChangeTracker.Clear();

            var own = await db.Users.SingleAsync(u => u.Id == a.UserId);
            db.Users.Remove(own);
            await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*soft delete*");
        }
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("suspended")]
    public async Task Locked_user_and_suspended_tenant_are_refused(string kind)
    {
        var user = TestUser.New(NewTenant(), "Blocked Bao");
        var signedIn = await SignInAsync(user);
        var (scope, db) = stack.Db(signedIn.TenantId);
        await using (scope)
        {
            if (kind == "locked")
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE org.users_cache SET status = 'Locked' WHERE id = {0}", signedIn.UserId);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE tenant.tenants SET status = 'Suspended' WHERE id = {0}", signedIn.TenantId);
            }
        }

        // A new session (new sid) is not served from the short identity cache.
        var token = stack.BlueId.Token(user with { Name = user.Name + " " });
        using var c = stack.Client(token);
        var r = await c.GetAsync("/api/v1/me");
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
            .Should().Be(kind == "locked" ? "user_locked" : "tenant_suspended");
    }

    [Fact]
    public void Every_endpoint_declares_its_guard()
    {
        // Spec §7.2: [RequirePermission], [SelfGuarded] (authenticated, own data only) or [AllowAnonymous].
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => (t, m)))
            .Where(x => x.m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
            .ToList();
        actions.Should().NotBeEmpty();
        foreach (var (type, method) in actions)
        {
            bool Has<T>() where T : Attribute => method.GetCustomAttribute<T>() is not null || type.GetCustomAttribute<T>() is not null;
            (Has<RequirePermissionAttribute>() || Has<SelfGuardedAttribute>() || Has<AllowAnonymousAttribute>())
                .Should().BeTrue($"{type.Name}.{method.Name} must declare [RequirePermission], [SelfGuarded] or [AllowAnonymous]");
            if (method.GetCustomAttribute<RequirePermissionAttribute>() is { } rp)
            {
                Permissions.All.Should().Contain(rp.Permission);
            }
        }
    }

    [Fact]
    public void Every_migration_class_has_the_migration_attribute()
    {
        var migrations = typeof(BlueChatDbContext).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Migration))).ToList();
        migrations.Should().NotBeEmpty();
        migrations.Should().OnlyContain(t => t.GetCustomAttribute<MigrationAttribute>() != null);
    }

    [Fact]
    public void Server_strings_exist_in_every_language()
    {
        var en = JsonStringLocalizerFactory.Table("en").Keys.ToHashSet();
        foreach (var lang in JsonStringLocalizerFactory.Languages)
        {
            JsonStringLocalizerFactory.Table(lang).Keys.Should().BeEquivalentTo(en, $"language {lang}");
        }
    }

    [Fact]
    public void Every_package_is_listed_in_the_licence_document()
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        var licences = File.ReadAllText(Path.Combine(root, "docs", "09-giay-phep-thanh-phan.md"));
        foreach (Match m in PackageVersion().Matches(props))
        {
            licences.Should().Contain(m.Groups[1].Value, "every package needs a licence line in docs/09");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlueChat.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [GeneratedRegex("PackageVersion Include=\"([^\"]+)\"")]
    private static partial Regex PackageVersion();
}
