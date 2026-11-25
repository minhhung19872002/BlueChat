using BlueChat.Api.Web;
using BlueChat.Application.Features.Admin;
using BlueChat.Domain;
using BlueChat.Infrastructure.Audit;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueChat.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/admin/audit")]
public sealed class AdminAuditController(IMediator mediator) : ControllerBase
{
    /// <summary>Walks the audit hash chain and reports the first altered row, if any.</summary>
    [HttpGet("verify")]
    [RequirePermission(Permissions.AuditView)]
    public async Task<ApiResponse<AuditVerification>> Verify(CancellationToken ct) =>
        ApiResponse.Ok(await mediator.Send(new VerifyAuditChain(), ct));
}
