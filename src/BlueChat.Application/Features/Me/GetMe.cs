using BlueChat.Infrastructure.Identity;
using BlueChat.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueChat.Application.Features.Me;

public sealed record MeDto(
    Guid UserId, Guid TenantId, string TenantName, string DisplayName, string? Email, bool IsGuest,
    IReadOnlyList<string> Permissions, UserSettingsDto Settings);

public sealed record GetMe : IRequest<MeDto>;

public sealed class GetMeHandler(BlueChatDbContext db, ICurrentUser me, IMediator mediator) : IRequestHandler<GetMe, MeDto>
{
    public async Task<MeDto> Handle(GetMe request, CancellationToken ct)
    {
        var tenantName = await db.Tenants.Select(t => t.Name).FirstAsync(ct);
        var isGuest = await db.Users.Where(u => u.Id == me.UserId).Select(u => u.IsGuest).FirstAsync(ct);
        var settings = await mediator.Send(new GetMySettings(), ct);
        return new MeDto(me.UserId, me.TenantId, tenantName, me.DisplayName, me.Email, isGuest,
            me.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToList(), settings);
    }
}
