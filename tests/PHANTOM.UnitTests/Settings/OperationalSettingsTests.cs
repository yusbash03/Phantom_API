using PHANTOM.Application.Settings;

namespace PHANTOM.UnitTests.Settings;

/// <summary>Settings ranges (Requirement 17.1) and safe fallback for a stored value that no longer validates.</summary>
public sealed class OperationalSettingsTests
{
    private static readonly OperationalSettings Valid = new(0.35, 0.15, 0.15, 0.2, 0.15, 1500, 6, 0.55, 15);

    [Fact]
    public void Accepts_the_design_defaults_and_rejects_out_of_range_values()
    {
        var validator = new OperationalSettingsValidator();
        Assert.True(validator.Validate(Valid).IsValid);
        Assert.False(validator.Validate(Valid with { TextWeight = -0.1 }).IsValid);
        Assert.False(validator.Validate(Valid with { GeoRadiusMeters = 49 }).IsValid);
        Assert.False(validator.Validate(Valid with { WindowHours = 169 }).IsValid);
        Assert.False(validator.Validate(Valid with { SuggestThreshold = 1.01 }).IsValid);
        Assert.False(validator.Validate(Valid with { WeatherCacheMinutes = -1 }).IsValid);
        Assert.False(validator.Validate(Valid with { GeoWeight = 0, TimeWeight = 0, CategoryWeight = 0, TextWeight = 0, LocationWeight = 0 }).IsValid);
        Assert.True(validator.Validate(Valid with { WeatherCacheMinutes = 0 }).IsValid);
    }

    [Fact]
    public void A_stored_value_that_does_not_parse_or_validate_falls_back_to_defaults()
    {
        Assert.Null(OperationalSettingsProvider.Read(null));
        Assert.Null(OperationalSettingsProvider.Read("not json"));
        Assert.Null(OperationalSettingsProvider.Read("""{"geoWeight":5,"timeWeight":0,"categoryWeight":0,"textWeight":0,"locationWeight":0,"geoRadiusMeters":1500,"windowHours":6,"suggestThreshold":0.5,"weatherCacheMinutes":15}"""));
        Assert.Equal(Valid, OperationalSettingsProvider.Read("""{"geoWeight":0.35,"timeWeight":0.15,"categoryWeight":0.15,"textWeight":0.2,"locationWeight":0.15,"geoRadiusMeters":1500,"windowHours":6,"suggestThreshold":0.55,"weatherCacheMinutes":15}"""));
    }

    [Fact]
    public void Converts_to_correlation_settings_keeping_the_configured_candidate_limit()
    {
        var correlation = Valid.ToCorrelation(maxCandidates: 42);
        Assert.Equal(TimeSpan.FromHours(6), correlation.Window);
        Assert.Equal(0.55, correlation.SuggestThreshold);
        Assert.Equal(42, correlation.MaxCandidates);
    }
}
