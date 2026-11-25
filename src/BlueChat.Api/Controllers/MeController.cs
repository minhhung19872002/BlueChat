using BlueChat.Api.Web;
using BlueChat.Application.Features.Me;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace BlueChat.Api.Controllers;

[ApiController]
[Route("api/v1/me")]
[SelfGuarded("Reads and writes only the caller's own profile and settings.")]
public sealed class MeController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<ApiResponse<MeDto>> Get(CancellationToken ct) => ApiResponse.Ok(await mediator.Send(new GetMe(), ct));

    [HttpGet("settings")]
    public async Task<ApiResponse<UserSettingsDto>> GetSettings(CancellationToken ct) =>
        ApiResponse.Ok(await mediator.Send(new GetMySettings(), ct));

    [HttpPut("settings")]
    public async Task<ApiResponse<UserSettingsDto>> PutSettings(UserSettingsDto settings, CancellationToken ct) =>
        ApiResponse.Ok(await mediator.Send(new UpdateMySettings(settings), ct));
}
