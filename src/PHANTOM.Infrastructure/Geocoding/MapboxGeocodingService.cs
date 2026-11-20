using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Geocoding;
using PHANTOM.Domain.Incidents;

namespace PHANTOM.Infrastructure.Geocoding;

public sealed class MapboxOptions
{
    public const string SectionName = "Mapbox";

    /// <summary>Server-side token (<c>Mapbox__AccessToken</c>). Never sent to the browser by the API.</summary>
    public string? AccessToken { get; set; }

    public Uri BaseUrl { get; set; } = new("https://api.mapbox.com/");

    /// <summary>Comma-separated ISO 3166 alpha-2 codes biasing results (e.g. "ng"); empty = worldwide.</summary>
    public string? CountryCodes { get; set; }

    public string Language { get; set; } = "en";
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>Lets tests and tooling run the API without the background worker.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 10;
    public int StuckAfterMinutes { get; set; } = 5;

    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>
/// Mapbox Geocoding v6 forward search. Precision is reported conservatively: a geocoded point is never
/// "Exact", and country/region-level matches are treated as no usable result rather than a misleading pin.
/// </summary>
internal sealed class MapboxGeocodingService(HttpClient http, IOptions<MapboxOptions> options, ILogger<MapboxGeocodingService> logger) : IGeocodingService
{
    private readonly MapboxOptions _options = options.Value;

    public async Task<GeocodeOutcome> GeocodeAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GeocodeOutcome.NoResult("Empty location text.");
        }

        var url = $"search/geocode/v6/forward?q={Uri.EscapeDataString(query.Trim())}&limit=1&autocomplete=false" +
                  $"&language={Uri.EscapeDataString(_options.Language)}&access_token={Uri.EscapeDataString(_options.AccessToken ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(_options.CountryCodes))
        {
            url += $"&country={Uri.EscapeDataString(_options.CountryCodes)}";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Status only: the URL carries the token and the body may echo it.
                logger.LogWarning("Mapbox geocoding returned {StatusCode}", (int)response.StatusCode);
                return GeocodeOutcome.Failed($"The geocoding provider returned an error (HTTP {(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token);
            return Interpret(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GeocodeOutcome.Failed($"The geocoding provider did not respond within {_options.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Mapbox geocoding request failed ({Error})", ex.HttpRequestError);
            return GeocodeOutcome.Failed("The geocoding provider could not be reached.");
        }
        catch (JsonException)
        {
            return GeocodeOutcome.Failed("The geocoding provider returned an unreadable response.");
        }
    }

    internal static GeocodeOutcome Interpret(JsonNode? body)
    {
        var feature = body?["features"]?.AsArray().FirstOrDefault();
        if (feature is null)
        {
            return GeocodeOutcome.NoResult("No match found.");
        }

        var properties = feature["properties"];
        var featureType = properties?["feature_type"]?.GetValue<string>() ?? string.Empty;
        LocationPrecision? precision = featureType switch
        {
            "address" or "secondary_address" or "street" or "block" => LocationPrecision.Street,
            "neighborhood" or "locality" or "place" or "district" or "postcode" => LocationPrecision.Locality,
            _ => null, // region, country, unknown: too coarse to place a pin honestly
        };
        if (precision is null)
        {
            return GeocodeOutcome.NoResult($"Only a {(featureType.Length > 0 ? featureType : "coarse")}-level match was found.");
        }

        var coordinates = feature["geometry"]?["coordinates"]?.AsArray();
        if (coordinates is not { Count: >= 2 })
        {
            return GeocodeOutcome.NoResult("The match had no coordinates.");
        }

        var longitude = coordinates[0]!.GetValue<double>();
        var latitude = coordinates[1]!.GetValue<double>();
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return GeocodeOutcome.NoResult("The match had invalid coordinates.");
        }

        var placeName = properties?["full_address"]?.GetValue<string>()
            ?? properties?["place_formatted"]?.GetValue<string>()
            ?? properties?["name"]?.GetValue<string>();
        return GeocodeOutcome.Found(
            Math.Round(decimal.Parse(latitude.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), 6),
            Math.Round(decimal.Parse(longitude.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), 6),
            precision.Value,
            placeName);
    }
}

/// <summary>No token configured: geocoding fails honestly and incidents are flagged for verification (8.3, 8.4).</summary>
internal sealed class UnconfiguredGeocodingService : IGeocodingService
{
    public Task<GeocodeOutcome> GeocodeAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult(GeocodeOutcome.Failed("No geocoding provider is configured."));
}
