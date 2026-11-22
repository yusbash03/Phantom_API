using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Application.Assignments;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Auth;
using PHANTOM.Application.Correlation;
using PHANTOM.Application.Dashboard;
using PHANTOM.Application.Geocoding;
using PHANTOM.Application.Incidents;
using PHANTOM.Application.Intelligence;
using PHANTOM.Application.Notifications;
using PHANTOM.Application.Reporting.Intake;
using PHANTOM.Application.Reports;
using PHANTOM.Application.Settings;
using PHANTOM.Application.Responders;
using PHANTOM.Application.Users;
using PHANTOM.Application.Weather;

namespace PHANTOM.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.RefreshTokenLifetimeDays is >= 1 and <= 30, "Auth:RefreshTokenLifetimeDays must be 1-30.")
            .Validate(o => o.AbsoluteSessionLifetimeDays is >= 1 and <= 90 && o.AbsoluteSessionLifetimeDays >= o.RefreshTokenLifetimeDays,
                "Auth:AbsoluteSessionLifetimeDays must be 1-90 and at least the refresh token lifetime.")
            .ValidateOnStart();

        services.AddOptions<CorrelationOptions>()
            .Bind(configuration.GetSection(CorrelationOptions.SectionName))
            .Validate(o => o.IsValid(), "Correlation settings are out of range (weights 0-1 with a positive sum, threshold 0-1, window 0-168 h).")
            .ValidateOnStart();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddSingleton<ILoginThrottle, LoginThrottle>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IReportIntakeService, ReportIntakeService>();
        services.AddScoped<IIncidentService, IncidentService>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.AddScoped<ICorrelationService, CorrelationService>();
        services.AddScoped<IGeocodingProcessor, GeocodingProcessor>();
        services.AddScoped<IResponderService, ResponderService>();
        services.AddScoped<IAssignmentService, AssignmentService>();
        services.AddScoped<INotifier, Notifier>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IEnvironmentService, EnvironmentService>();
        services.AddScoped<IReportService, ReportService>();

        // Requirement 17: administrator-adjustable values; configuration supplies the defaults.
        services.AddMemoryCache();
        services.AddSingleton(sp => new SettingsDefaults(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorrelationOptions>>(),
            configuration.GetValue<int?>("Weather:CacheMinutes") ?? 15));
        services.AddSingleton<OperationalSettingsProvider>();
        services.AddSingleton<IOperationalSettings>(sp => sp.GetRequiredService<OperationalSettingsProvider>());
        services.AddScoped<ISettingsService, SettingsService>();
        return services;
    }
}
