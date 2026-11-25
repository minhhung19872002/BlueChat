using System.Globalization;
using System.Net;
using BlueChat.Application.Common;
using BlueChat.Infrastructure.Identity;
using BlueChat.Infrastructure.Localization;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BlueChat.Api.Web;

/// <summary>Response envelope of every successful API call (spec §7.3).</summary>
public sealed record ApiResponse<T>(bool Success, T? Data, string? Message = null, IReadOnlyDictionary<string, string[]>? Errors = null);

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data) => new(true, data);
}

/// <summary>
/// Admin endpoints declare the permission they need (spec §7.2: chat.admin.&lt;group&gt;.&lt;action&gt;). The scan test fails
/// for any endpoint without [RequirePermission], [SelfGuarded] or [AllowAnonymous].
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequirePermissionAttribute(string permission) : Attribute, IAsyncAuthorizationFilter
{
    public string Permission { get; } = permission;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return Task.CompletedTask;
        }

        var me = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (!me.IsResolved || !me.Has(Permission))
        {
            context.Result = Problems.Result(context.HttpContext, HttpStatusCode.Forbidden, "forbidden");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// An authenticated endpoint that only touches the caller's own data; <see cref="Reason"/> says why no permission is
/// needed. The guard itself is authentication (applied by [Authorize]).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class SelfGuardedAttribute(string reason) : AuthorizeAttribute
{
    public string Reason { get; } = reason;
}

/// <summary>RFC 7807 with a stable `code` and a title in the caller's language (spec §0.2).</summary>
public static class Problems
{
    public static ObjectResult Result(HttpContext ctx, HttpStatusCode status, string code,
        IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(Build(ctx, (int)status, code, errors)) { StatusCode = (int)status, ContentTypes = { "application/problem+json" } };

    public static ProblemDetails Build(HttpContext ctx, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var culture = RequestCulture(ctx);
        var p = new ProblemDetails
        {
            Status = status,
            Title = JsonStringLocalizerFactory.Get("error." + code, culture),
            Type = "https://bluechat.test/errors/" + code,
            Instance = ctx.Request.Path,
        };
        p.Extensions["code"] = code;
        p.Extensions["traceId"] = ctx.TraceIdentifier;
        if (errors is not null)
        {
            p.Extensions["errors"] = errors;
        }

        return p;
    }

    public static async Task WriteAsync(HttpContext ctx, HttpStatusCode status, string code)
    {
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(Build(ctx, (int)status, code), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }

    /// <summary>Language for user-facing text: Accept-Language best match among the shipped languages.</summary>
    public static CultureInfo RequestCulture(HttpContext ctx)
    {
        var header = ctx.Request.Headers.AcceptLanguage.ToString();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = part.Split(';')[0];
            try
            {
                var culture = CultureInfo.GetCultureInfo(tag);
                if (JsonStringLocalizerFactory.Resolve(culture) != JsonStringLocalizerFactory.Fallback || tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    return culture;
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }

        return CultureInfo.GetCultureInfo(JsonStringLocalizerFactory.Fallback);
    }

    /// <summary>Maps exceptions to problem+json; unexpected ones become 500 internal_error without details.</summary>
    public static void UseBlueChatExceptionHandler(this WebApplication app) =>
        app.UseExceptionHandler(e => e.Run(async ctx =>
        {
            var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            switch (error)
            {
                case AppException a:
                    await WriteAsync(ctx, (HttpStatusCode)a.Status, a.Code);
                    break;
                case ValidationException v:
                    var culture = RequestCulture(ctx);
                    var errors = v.Errors.GroupBy(f => ToCamel(f.PropertyName))
                        .ToDictionary(g => g.Key, g => g.Select(f => JsonStringLocalizerFactory.Get("error." + Code(f.ErrorCode), culture)).Distinct().ToArray());
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(Build(ctx, 400, "validation_failed", errors),
                        (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                    break;
                case BadHttpRequestException:
                    await WriteAsync(ctx, HttpStatusCode.BadRequest, "bad_request");
                    break;
                default:
                    await WriteAsync(ctx, HttpStatusCode.InternalServerError, "internal_error");
                    break;
            }
        }));

    private static string Code(string errorCode) =>
        errorCode.Contains('_', StringComparison.Ordinal) && errorCode == errorCode.ToLowerInvariant() ? errorCode : "validation_failed";

    private static string ToCamel(string path) =>
        string.Join('.', path.Split('.').Where(p => p != "Settings" || path == "Settings")
            .Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}
