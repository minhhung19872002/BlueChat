using FluentValidation;
using MediatR;

namespace BlueChat.Application.Common;

/// <summary>
/// A failure with a stable machine code (spec §0.2 "Lỗi API có code ổn định"); the API maps it to RFC 7807 with the
/// HTTP status and a message in the caller's language (localisation key "error.&lt;code&gt;").
/// </summary>
public sealed class AppException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public static AppException NotFound(string code = "not_found") => new(404, code);

    public static AppException Forbidden(string code = "forbidden") => new(403, code);

    public static AppException Conflict(string code) => new(409, code);

    public static AppException BadRequest(string code) => new(400, code);
}

/// <summary>Runs every FluentValidation validator of the request before its handler (spec §13).</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators)
        {
            var r = await v.ValidateAsync(request, cancellationToken);
            failures.AddRange(r.Errors);
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next(cancellationToken);
    }
}
