using System.Text.Json;
using BlueChat.Domain;
using BlueChat.Infrastructure.Identity;
using BlueChat.Infrastructure.Localization;
using BlueChat.Infrastructure.Persistence;
using BlueChat.Infrastructure.Realtime;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueChat.Application.Features.Me;

/// <summary>
/// Personal settings (spec §5 org.user_settings, §7.1 theme/density/font size, §0.2 language/time zone/12-24h/week
/// start, §8.1 read receipts). Null = follow the browser. Stored as one JSON document per user.
/// </summary>
public sealed record UserSettingsDto
{
    /// <summary>"light", "dark" or "contrast" (high contrast).</summary>
    public string Theme { get; init; } = "light";

    /// <summary>"comfortable" or "compact".</summary>
    public string Density { get; init; } = "comfortable";

    public decimal FontScale { get; init; } = 1m;

    public string? Language { get; init; }

    public string? TimeZone { get; init; }

    public string? HourCycle { get; init; }

    public int? WeekStart { get; init; }

    public bool ReadReceiptsEnabled { get; init; } = true;

    public static readonly UserSettingsDto Default = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record GetMySettings : IRequest<UserSettingsDto>;

public sealed record UpdateMySettings(UserSettingsDto Settings) : IRequest<UserSettingsDto>;

public sealed class UpdateMySettingsValidator : AbstractValidator<UpdateMySettings>
{
    public static readonly decimal[] FontScales = [0.875m, 1m, 1.125m, 1.25m, 1.5m];

    public UpdateMySettingsValidator()
    {
        RuleFor(x => x.Settings).NotNull();
        RuleFor(x => x.Settings.Theme).Must(t => t is "light" or "dark" or "contrast").WithErrorCode("settings_invalid");
        RuleFor(x => x.Settings.Density).Must(d => d is "comfortable" or "compact").WithErrorCode("settings_invalid");
        RuleFor(x => x.Settings.FontScale).Must(v => FontScales.Contains(v)).WithErrorCode("settings_invalid");
        RuleFor(x => x.Settings.Language)
            .Must(l => l is null || JsonStringLocalizerFactory.Languages.Contains(l, StringComparer.OrdinalIgnoreCase))
            .WithErrorCode("unknown_language");
        RuleFor(x => x.Settings.TimeZone)
            .Must(tz => tz is null || (tz.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)))
            .WithErrorCode("unknown_time_zone");
        RuleFor(x => x.Settings.HourCycle).Must(h => h is null or "h12" or "h23").WithErrorCode("settings_invalid");
        RuleFor(x => x.Settings.WeekStart).InclusiveBetween(0, 6).When(x => x.Settings.WeekStart is not null)
            .WithErrorCode("settings_invalid");
    }
}

public sealed class GetMySettingsHandler(BlueChatDbContext db, ICurrentUser me) : IRequestHandler<GetMySettings, UserSettingsDto>
{
    public async Task<UserSettingsDto> Handle(GetMySettings request, CancellationToken ct)
    {
        var json = await db.UserSettings.Where(s => s.UserId == me.UserId).Select(s => s.Settings).FirstOrDefaultAsync(ct);
        return json is null ? UserSettingsDto.Default : JsonSerializer.Deserialize<UserSettingsDto>(json, UserSettingsDto.Json)!;
    }
}

/// <summary>Saves, then tells the user's other open sessions (spec §2.2: publish only after the commit).</summary>
public sealed class UpdateMySettingsHandler(BlueChatDbContext db, ICurrentUser me, IUserEventPublisher events, TimeProvider clock)
    : IRequestHandler<UpdateMySettings, UserSettingsDto>
{
    public async Task<UserSettingsDto> Handle(UpdateMySettings request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request.Settings, UserSettingsDto.Json);
        var row = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == me.UserId, ct);
        if (row is null)
        {
            db.UserSettings.Add(new UserSettings { UserId = me.UserId, Settings = json });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (IdentityResolver.IsUniqueViolation(e))
            {
                // A parallel first save created the row: update it instead (one settings row per user, lesson 1).
                db.ChangeTracker.Clear();
                row = await db.UserSettings.FirstAsync(s => s.UserId == me.UserId, ct);
                row.Settings = json;
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            row.Settings = json;
            await db.SaveChangesAsync(ct);
        }

        await events.PublishAsync(new UserEvent(Guid.CreateVersion7(), me.TenantId, me.UserId, "settings.changed",
            JsonSerializer.SerializeToElement(request.Settings, UserSettingsDto.Json), clock.GetUtcNow()), ct);
        return request.Settings;
    }
}
