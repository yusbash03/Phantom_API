using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Auth;
using PHANTOM.Application.Users;

namespace PHANTOM.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.RefreshTokenLifetimeDays is >= 1 and <= 30, "Auth:RefreshTokenLifetimeDays must be 1-30.")
            .ValidateOnStart();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        return services;
    }
}
