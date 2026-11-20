using PHANTOM.Domain.Common;

namespace PHANTOM.Domain.Incidents;

/// <summary>
/// Where an incident is believed to be, with honest provenance. Invariants:
/// latitude and longitude are both present or both absent; a location is verified
/// if and only if its provenance is <see cref="LocationProvenance.ManuallyVerified"/>.
/// </summary>
public sealed record Location
{
    public const int MaxTextLength = 500;

    private Location(
        string text,
        decimal? latitude,
        decimal? longitude,
        LocationProvenance provenance,
        LocationPrecision precision,
        Guid? verifiedById,
        DateTimeOffset? verifiedAt,
        string? geocoderPlaceName = null)
    {
        Text = text;
        Latitude = latitude;
        Longitude = longitude;
        Provenance = provenance;
        Precision = precision;
        VerifiedById = verifiedById;
        VerifiedAt = verifiedAt;
        GeocoderPlaceName = geocoderPlaceName;
    }

    public string Text { get; }
    public decimal? Latitude { get; }
    public decimal? Longitude { get; }
    public LocationProvenance Provenance { get; }
    public LocationPrecision Precision { get; }
    public Guid? VerifiedById { get; }
    public DateTimeOffset? VerifiedAt { get; }

    /// <summary>What the geocoder matched (e.g. "Allen Avenue, Ikeja, Lagos"), shown so staff can judge the match.</summary>
    public string? GeocoderPlaceName { get; }

    public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;
    public bool IsVerified => Provenance == LocationProvenance.ManuallyVerified;

    /// <summary>A location as described by the reporter, optionally with reporter-supplied coordinates.</summary>
    public static Result<Location> Reported(string text, decimal? latitude, decimal? longitude)
    {
        var error = ValidateText(text) ?? ValidateCoordinates(latitude, longitude);
        if (error is not null)
        {
            return error;
        }

        return new Location(text.Trim(), Round(latitude), Round(longitude), LocationProvenance.Reported,
            latitude.HasValue ? LocationPrecision.Exact : LocationPrecision.Unknown, null, null);
    }

    /// <summary>Coordinates from a geocoder. Never verified.</summary>
    public Result<Location> WithGeocodedCoordinates(decimal latitude, decimal longitude, LocationPrecision precision, bool fromAiExtractedPlace, string? placeName = null)
    {
        var error = ValidateCoordinates(latitude, longitude);
        if (error is not null)
        {
            return error;
        }

        var provenance = fromAiExtractedPlace ? LocationProvenance.AiInferred : LocationProvenance.Geocoded;
        var name = string.IsNullOrWhiteSpace(placeName) ? null : placeName.Trim();
        return new Location(Text, Round(latitude), Round(longitude), provenance, precision, null, null,
            name is { Length: > MaxTextLength } ? name[..MaxTextLength] : name);
    }

    /// <summary>The only way to produce a verified location: an identified human confirms or corrects it.</summary>
    public static Result<Location> ManuallyVerified(string text, decimal latitude, decimal longitude, Guid verifiedById, DateTimeOffset verifiedAt)
    {
        if (verifiedById == Guid.Empty)
        {
            return Error.Validation("location.verifier_required", "A verified location requires the verifying user.");
        }

        var error = ValidateText(text) ?? ValidateCoordinates(latitude, longitude);
        if (error is not null)
        {
            return error;
        }

        return new Location(text.Trim(), Round(latitude), Round(longitude), LocationProvenance.ManuallyVerified,
            LocationPrecision.Exact, verifiedById, verifiedAt);
    }

    private static Error? ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation("location.text_required", "A location description is required.");
        }

        return text.Trim().Length > MaxTextLength
            ? Error.Validation("location.text_too_long", $"Location description must be at most {MaxTextLength} characters.")
            : null;
    }

    private static Error? ValidateCoordinates(decimal? latitude, decimal? longitude)
    {
        if (latitude.HasValue != longitude.HasValue)
        {
            return Error.Validation("location.coordinates_incomplete", "Latitude and longitude must be provided together.");
        }

        if (latitude is < -90m or > 90m)
        {
            return Error.Validation("location.latitude_range", "Latitude must be between -90 and 90.");
        }

        if (longitude is < -180m or > 180m)
        {
            return Error.Validation("location.longitude_range", "Longitude must be between -180 and 180.");
        }

        return null;
    }

    private static decimal? Round(decimal? value) => value.HasValue ? Math.Round(value.Value, 6, MidpointRounding.ToEven) : null;
}
