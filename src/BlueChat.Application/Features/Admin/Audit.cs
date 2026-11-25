using BlueChat.Infrastructure.Audit;
using MediatR;

namespace BlueChat.Application.Features.Admin;

/// <summary>Walks the whole audit hash chain (spec §10.2 "append-only, chuỗi băm").</summary>
public sealed record VerifyAuditChain : IRequest<AuditVerification>;

public sealed class VerifyAuditChainHandler(AuditChainVerifier verifier) : IRequestHandler<VerifyAuditChain, AuditVerification>
{
    public Task<AuditVerification> Handle(VerifyAuditChain request, CancellationToken ct) => verifier.VerifyAsync(ct);
}
