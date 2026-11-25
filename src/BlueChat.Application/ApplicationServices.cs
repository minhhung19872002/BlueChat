using BlueChat.Application.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BlueChat.Application;

public static class ApplicationServices
{
    public static IServiceCollection AddBlueChatApplication(this IServiceCollection services)
    {
        services.AddMediatR(c =>
        {
            c.RegisterServicesFromAssembly(typeof(ApplicationServices).Assembly);
            c.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(typeof(ApplicationServices).Assembly, includeInternalTypes: true);
        return services;
    }
}
