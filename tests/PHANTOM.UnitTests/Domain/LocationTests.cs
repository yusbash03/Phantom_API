using FsCheck;
using FsCheck.Xunit;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.UnitTests.Domain;

/// <summary>Critical property 4: an unverified location is never presented as verified.</summary>
public sealed class LocationTests
{
    [Property]
    public bool Reported_locations_are_never_verified(NormalFloat lat, NormalFloat lon)
    {
        var latitude = (decimal)Math.Clamp(lat.Get, -90, 90);
        var longitude = (decimal)Math.Clamp(lon.Get, -180, 180);
        var location = Location.Reported("Somewhere", latitude, longitude).Value;
        return !location.IsVerified && location.Provenance == LocationProvenance.Reported;
    }

    [Property]
    public bool Geocoded_locations_are_never_verified(bool fromAi, LocationPrecision precision)
    {
        var location = Location.Reported("Market Road", null, null).Value
            .WithGeocodedCoordinates(6.5244m, 3.3792m, precision, fromAi).Value;
        return !location.IsVerified
            && location.Provenance == (fromAi ? LocationProvenance.AiInferred : LocationProvenance.Geocoded);
    }

    [Fact]
    public void Manual_verification_is_verified_and_records_who()
    {
        var verifier = Guid.NewGuid();
        var location = Location.ManuallyVerified("Market Road", 6.5244m, 3.3792m, verifier, IncidentTestBuilder.Now).Value;
        Assert.True(location.IsVerified);
        Assert.Equal(verifier, location.VerifiedById);
    }

    [Fact]
    public void Manual_verification_requires_a_verifier()
    {
        Assert.True(Location.ManuallyVerified("Market Road", 6.5m, 3.3m, Guid.Empty, IncidentTestBuilder.Now).IsFailure);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-90.0001, 0)]
    [InlineData(0, 180.5)]
    [InlineData(0, -181)]
    public void Out_of_range_coordinates_are_rejected(double lat, double lon)
    {
        Assert.True(Location.Reported("x", (decimal)lat, (decimal)lon).IsFailure);
    }

    [Fact]
    public void Latitude_without_longitude_is_rejected()
    {
        Assert.True(Location.Reported("x", 1m, null).IsFailure);
    }

    [Fact]
    public void Coordinates_are_rounded_to_six_decimal_places()
    {
        var location = Location.Reported("x", 6.123456789m, 3.987654321m).Value;
        Assert.Equal(6.123457m, location.Latitude);
        Assert.Equal(3.987654m, location.Longitude);
    }

    [Fact]
    public void Incident_without_coordinates_needs_location_verification()
    {
        Assert.True(IncidentTestBuilder.New().LocationNeedsVerification);
    }
}
