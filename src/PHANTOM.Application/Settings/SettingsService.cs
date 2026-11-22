using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Application.Correlation;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Correlation;
using PHANTOM.Domain.Settings;

namespace PHANTOM.Application.Settings;

/// <summary>
/// The values administrators may change (Requirement 17.1). This explicit list is the whole surface of the
/// settings API, so nothing else, and no secret, can be read or written through it (17.3).
/// </summary>
public sealed record OperationalSettings(
    double GeoWeight,
    double TimeWeight,
    double CategoryWeight,
    double TextWeight,
    double LocationWeight,
    double GeoRadiusMeters,
    double WindowHours,
    double SuggestThreshold,
    int WeatherCacheMinutes)
{
    public CorrelationSettings ToCorrelation(int maxCandidates) => new()
    {
        GeoWeight = GeoWeight,
        TimeWeight = TimeWeight,
        CategoryWeight = CategoryWeight,
        TextWeight = TextWeight,
        LocationWeight = LocationWeight,
        GeoRadiusMeters = GeoRadiusMeters,
        Window = TimeSpan.FromHours(WindowHours),
        SuggestThreshold = SuggestThreshold,
        MaxCandidates = maxCandidates,
    };
}

/// <summary>Current values, the configured defaults, whether they differ, and who last changed them.</summary>
public sealed record SettingsResponse(OperationalSettings Values, OperationalSettings Defaults, bool IsCustomised, UserRef? UpdatedBy, DateTimeOffset? UpdatedAt);

/// <summary><c>ExpectedUpdatedAt</c> is the <c>UpdatedAt</c> the administrator saw (null if never changed): a stale save is refused.</summary>
public sealed record UpdateSettingsRequest(OperationalSettings Values, DateTimeOffset? ExpectedUpdatedAt);

/// <summary>The effective operational values, read by correlation and weather at run time. Cached briefly.</summary>
public interface IOperationalSettings
{
    Task<OperationalSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>Synchronous read for code paths that cannot await; returns the cached value or the defaults.</summary>
    OperationalSettings Current { get; }
}

public interface ISettingsService
{
    Task<SettingsResponse> GetAsync(CancellationToken cancellationToken);
    Task<Result<SettingsResponse>> UpdateAsync(UpdateSettingsRequest request, CancellationToken cancellationToken);
}

internal sealed class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(r => r.Values).NotNull().SetValidator(new OperationalSettingsValidator());
    }
}

/// <summary>Requirement 17.1: every value has a sensible range.</summary>
internal sealed class OperationalSettingsValidator : AbstractValidator<OperationalSettings>
{
    public OperationalSettingsValidator()
    {
        foreach (var (rule, name) in new (System.Linq.Expressions.Expression<Func<OperationalSettings, double>>, string)[]
        {
            (s => s.GeoWeight, "Distance weight"), (s => s.TimeWeight, "Time weight"), (s => s.CategoryWeight, "Category weight"),
            (s => s.TextWeight, "Text weight"), (s => s.LocationWeight, "Place-name weight"),
        })
        {
            RuleFor(rule).InclusiveBetween(0, 1).WithMessage($"{name} must be between 0 and 1.");
        }

        RuleFor(s => s).Must(s => s.GeoWeight + s.TimeWeight + s.CategoryWeight + s.TextWeight + s.LocationWeight > 0)
            .WithName(nameof(OperationalSettings.GeoWeight)).WithMessage("At least one weight must be above 0.");
        RuleFor(s => s.GeoRadiusMeters).InclusiveBetween(50, 100_000).WithMessage("The distance radius must be between 50 m and 100 km.");
        RuleFor(s => s.WindowHours).InclusiveBetween(0.25, 168).WithMessage("The time window must be between 15 minutes and 7 days.");
        RuleFor(s => s.SuggestThreshold).InclusiveBetween(0.1, 1).WithMessage("The suggestion threshold must be between 0.1 and 1.");
        RuleFor(s => s.WeatherCacheMinutes).InclusiveBetween(0, 1440).WithMessage("The weather cache must be between 0 and 1440 minutes.");
    }
}

/// <summary>Configuration provides the defaults; a saved setting overrides them.</summary>
internal sealed class SettingsDefaults(IOptions<CorrelationOptions> correlation, int weatherCacheMinutes)
{
    public OperationalSettings Values { get; } = new(
        correlation.Value.GeoWeight, correlation.Value.TimeWeight, correlation.Value.CategoryWeight, correlation.Value.TextWeight,
        correlation.Value.LocationWeight, correlation.Value.GeoRadiusMeters, correlation.Value.WindowHours, correlation.Value.SuggestThreshold,
        weatherCacheMinutes);
}

internal sealed class OperationalSettingsProvider(IServiceScopeFactory scopes, IMemoryCache cache, SettingsDefaults defaults) : IOperationalSettings
{
    public const string Key = "operational";
    private const string CacheKey = "settings:operational";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OperationalSettings Current => cache.TryGetValue(CacheKey, out OperationalSettings? cached) && cached is not null ? cached : defaults.Values;

    public async Task<OperationalSettings> GetAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out OperationalSettings? cached) && cached is not null)
        {
            return cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPhantomDbContext>();
        var stored = await db.AppSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        var values = Read(stored) ?? defaults.Values;
        cache.Set(CacheKey, values, CacheFor);
        return values;
    }

    public void Invalidate() => cache.Remove(CacheKey);

    /// <summary>A stored value that no longer parses or validates falls back to the defaults rather than breaking.</summary>
    internal static OperationalSettings? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<OperationalSettings>(json, JsonOptions);
            return values is not null && new OperationalSettingsValidator().Validate(values).IsValid ? values : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class SettingsService(
    IPhantomDbContext db,
    OperationalSettingsProvider provider,
    SettingsDefaults defaults,
    IValidator<UpdateSettingsRequest> validator,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock) : ISettingsService
{
    public async Task<SettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == OperationalSettingsProvider.Key, cancellationToken);
        return await DescribeAsync(row, cancellationToken);
    }

    public async Task<Result<SettingsResponse>> UpdateAsync(UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == OperationalSettingsProvider.Key, cancellationToken);
        if (row?.UpdatedAt != request.ExpectedUpdatedAt)
        {
            return Error.Conflict("settings.changed", "These settings were changed by someone else. Reload and try again.");
        }

        var before = OperationalSettingsProvider.Read(row?.Value) ?? defaults.Values;
        var json = JsonSerializer.Serialize(request.Values, OperationalSettingsProvider.JsonOptions);
        var now = clock.UtcNow;
        if (row is null)
        {
            db.AppSettings.Add(AppSetting.Create(OperationalSettingsProvider.Key, json, currentUser.UserId, now));
        }
        else
        {
            row.Update(json, currentUser.UserId, now);
        }

        // Requirement 17.2: every change is audited with the values before and after.
        audit.Record(AuditActions.SettingsChanged, nameof(AppSetting), OperationalSettingsProvider.Key, before, request.Values);
        await db.SaveChangesAsync(cancellationToken);
        provider.Invalidate();
        return await GetAsync(cancellationToken);
    }

    private async Task<SettingsResponse> DescribeAsync(AppSetting? row, CancellationToken cancellationToken)
    {
        var values = OperationalSettingsProvider.Read(row?.Value);
        UserRef? updatedBy = null;
        if (row?.UpdatedById is { } id)
        {
            updatedBy = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new UserRef(u.Id, u.DisplayName)).FirstOrDefaultAsync(cancellationToken);
        }

        // Customised means "differs from the configured defaults", not merely "has been saved".
        return new SettingsResponse(values ?? defaults.Values, defaults.Values, values is not null && values != defaults.Values, updatedBy, row?.UpdatedAt);
    }
}
