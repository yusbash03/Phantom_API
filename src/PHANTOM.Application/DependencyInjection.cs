using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Assignments;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Auth;
using PHANTOM.Application.Correlation;
using PHANTOM.Application.Geocoding;
using PHANTOM.Application.Incidents;
using PHANTOM.Application.Intelligence;
using PHANTOM.Application.Reporting.Intake;
using PHANTOM.Application.Responders;
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

        services.AddOptions<CorrelationOptions>()
            .Bind(configuration.GetSection(CorrelationOptions.SectionName))
            .Validate(o => o.IsValid(), "Correlation settings are out of range (weights 0-1 with a positive sum, threshold 0-1, window 0-168 h).")
            .ValidateOnStart();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IReportIntakeService, ReportIntakeService>();
        services.AddScoped<IIncidentService, IncidentService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.AddScoped<ICorrelationService, CorrelationService>();
        services.AddScoped<IGeocodingProcessor, GeocodingProcessor>();
        services.AddScoped<IResponderService, ResponderService>();
        services.AddScoped<IAssignmentService, AssignmentService>();
        return services;
    }
}
