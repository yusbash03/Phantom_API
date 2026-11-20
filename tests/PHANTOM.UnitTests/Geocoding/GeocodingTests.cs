using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Geocoding;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Geocoding;
using PHANTOM.UnitTests.Domain;

namespace PHANTOM.UnitTests.Geocoding;

/// <summary>Critical property 4: an unverified location is never returned as verified (Requirement 8.4-8.6).</summary>
public sealed class IncidentLocationTests
{
    private static Incident Unlocated() =>
        Incident.Create(Guid.NewGuid(), "PH-2026-000500", "Smoke", "Smoke seen.", IncidentCategory.Fire,
            Location.Reported("Allen Avenue", null, null).Value, IncidentTestBuilder.Now).Value;

    [Property(MaxTest = 500)]
    public bool System_operations_never_produce_a_verified_location(byte[] operations)
    {
        var incident = Unlocated();
        foreach (var op in operations)
        {
            switch (op % 5)
            {
                case 0:
                    incident.ApplyGeocode(incident.Location.WithGeocodedCoordinates(6.6m, 3.35m, LocationPrecision.Street, fromAiExtractedPlace: false, "Allen Avenue").Value, IncidentTestBuilder.Now);
                    break;
                case 1:
                    incident.ApplyGeocode(incident.Location.WithGeocodedCoordinates(6.5m, 3.3m, LocationPrecision.Locality, fromAiExtractedPlace: true).Value, IncidentTestBuilder.Now);
                    break;
                case 2:
                    incident.MarkGeocodingUnsuccessful(op % 2 == 0, IncidentTestBuilder.Now);
                    break;
                case 3:
                    incident.RequestGeocodingRetry();
                    break;
                default:
                    incident.RecordAiRecommendation(Severity.High, IncidentCategory.Fire, IncidentTestBuilder.Now);
                    break;
            }
        }

        return !incident.Location.IsVerified
            && incident.Location.Provenance != LocationProvenance.ManuallyVerified
            && incident.Location.VerifiedById is null
            && incident.Version == 0;
    }

    [Fact]
    public void Geocode_never_overwrites_reported_or_verified_coordinates()
    {
        var reported = Incident.Create(Guid.NewGuid(), "PH-2026-000501", "Smoke", "Smoke seen.", IncidentCategory.Fire,
            Location.Reported("Allen Avenue", 6.6m, 3.35m).Value, IncidentTestBuilder.Now).Value;
        Assert.Equal(GeocodingStatus.NotRequired, reported.GeocodingStatus);
        Assert.False(reported.ApplyGeocode(reported.Location.WithGeocodedCoordinates(1m, 1m, LocationPrecision.Street, false).Value, IncidentTestBuilder.Now));
        Assert.Equal(6.6m, reported.Location.Latitude);

        var verified = Unlocated();
        Assert.True(verified.VerifyLocation("Allen Avenue by the roundabout", 6.601m, 3.352m, Guid.NewGuid(), IncidentTestBuilder.Now).IsSuccess);
        Assert.False(verified.ApplyGeocode(verified.Location.WithGeocodedCoordinates(1m, 1m, LocationPrecision.Street, false).Value, IncidentTestBuilder.Now));
        Assert.True(verified.Location.IsVerified);
        Assert.False(verified.RequestGeocodingRetry());
    }

    [Fact]
    public void Geocoding_lifecycle_flags_failures_and_marks_ai_matches()
    {
        var incident = Unlocated();
        Assert.Equal(GeocodingStatus.Pending, incident.GeocodingStatus);
        Assert.True(incident.LocationNeedsVerification);

        incident.MarkGeocodingUnsuccessful(noResult: true, IncidentTestBuilder.Now);
        Assert.Equal(GeocodingStatus.NoResult, incident.GeocodingStatus);
        Assert.True(incident.LocationNeedsVerification);
        Assert.False(incident.Location.HasCoordinates);

        Assert.True(incident.RequestGeocodingRetry());
        Assert.True(incident.ApplyGeocode(incident.Location.WithGeocodedCoordinates(6.5m, 3.3m, LocationPrecision.Locality, true, "Ikeja").Value, IncidentTestBuilder.Now));
        Assert.Equal(LocationProvenance.AiInferred, incident.Location.Provenance);
        Assert.Equal("Ikeja", incident.Location.GeocoderPlaceName);
        Assert.Equal("Allen Avenue", incident.Location.Text);
        Assert.False(incident.LocationNeedsVerification);
        Assert.False(incident.Location.IsVerified);
        Assert.Equal(0, incident.Version);
    }

    [Fact]
    public void A_claim_with_nothing_left_to_do_is_released_not_left_processing()
    {
        var reported = Incident.Create(Guid.NewGuid(), "PH-2026-000502", "Smoke", "Smoke seen.", IncidentCategory.Fire,
            Location.Reported("Allen Avenue", 6.6m, 3.35m).Value, IncidentTestBuilder.Now).Value;
        Assert.False(reported.ReleaseGeocodingClaim());

        var pending = Unlocated();
        Assert.False(pending.ReleaseGeocodingClaim());
        Assert.Equal(GeocodingStatus.Pending, pending.GeocodingStatus);
    }

    [Fact]
    public void Requeue_bumps_the_geocoding_generation()
    {
        var incident = Unlocated();
        incident.MarkGeocodingUnsuccessful(noResult: true, IncidentTestBuilder.Now);
        Assert.True(incident.RequestGeocodingRetry());
        Assert.Equal(1, incident.GeocodingGeneration);
        Assert.Equal(0, incident.Version);
    }

    [Fact]
    public void Verification_is_a_human_decision_that_bumps_the_version()
    {
        var incident = Unlocated();
        var coordinator = Guid.NewGuid();

        Assert.True(incident.VerifyLocation("Allen Avenue, by the filling station", 6.6012m, 3.3521m, coordinator, IncidentTestBuilder.Now).IsSuccess);

        Assert.True(incident.Location.IsVerified);
        Assert.Equal(coordinator, incident.Location.VerifiedById);
        Assert.Equal(1, incident.Version);
        Assert.False(incident.LocationNeedsVerification);
        Assert.True(incident.VerifyLocation("x", 91m, 0m, coordinator, IncidentTestBuilder.Now).IsFailure);
    }
}

public sealed class MapboxGeocodingServiceTests
{
    private static JsonObject Feature(string type, double lon, double lat, string fullAddress) => new()
    {
        ["type"] = "FeatureCollection",
        ["features"] = new JsonArray(new JsonObject
        {
            ["type"] = "Feature",
            ["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(lon, lat) },
            ["properties"] = new JsonObject { ["feature_type"] = type, ["full_address"] = fullAddress, ["name"] = "n" },
        }),
        ["attribution"] = "© Mapbox",
    };

    [Theory]
    [InlineData("address", LocationPrecision.Street)]
    [InlineData("street", LocationPrecision.Street)]
    [InlineData("neighborhood", LocationPrecision.Locality)]
    [InlineData("place", LocationPrecision.Locality)]
    [InlineData("postcode", LocationPrecision.Locality)]
    public void Maps_feature_types_to_conservative_precision(string type, LocationPrecision expected)
    {
        var outcome = MapboxGeocodingService.Interpret(Feature(type, 3.3515, 6.6018, "Allen Avenue, Ikeja, Lagos"));

        Assert.Equal(GeocodeOutcomeKind.Found, outcome.Kind);
        Assert.Equal(expected, outcome.Precision);
        Assert.NotEqual(LocationPrecision.Exact, outcome.Precision);
        Assert.Equal(6.6018m, outcome.Latitude);
        Assert.Equal(3.3515m, outcome.Longitude);
        Assert.Equal("Allen Avenue, Ikeja, Lagos", outcome.PlaceName);
    }

    [Theory]
    [InlineData("region")]
    [InlineData("country")]
    public void Coarse_matches_are_no_result_rather_than_a_misleading_pin(string type)
    {
        var outcome = MapboxGeocodingService.Interpret(Feature(type, 3.4, 6.5, "Lagos, Nigeria"));
        Assert.Equal(GeocodeOutcomeKind.NoResult, outcome.Kind);
        Assert.Null(outcome.Latitude);
    }

    [Fact]
    public void Empty_results_are_no_result() =>
        Assert.Equal(GeocodeOutcomeKind.NoResult, MapboxGeocodingService.Interpret(new JsonObject { ["features"] = new JsonArray() }).Kind);

    [Fact]
    public async Task Sends_escaped_query_with_country_bias_and_keeps_the_token_out_of_failures()
    {
        Uri? sent = null;
        var service = Service(request =>
        {
            sent = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"Not Authorized - Invalid Token pk.secret-token\"}") };
        });

        var outcome = await service.GeocodeAsync("Allen Ave & Opebi Rd, Ikeja", CancellationToken.None);

        Assert.Equal(GeocodeOutcomeKind.Failed, outcome.Kind);
        Assert.DoesNotContain("pk.secret-token", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal("/search/geocode/v6/forward", sent!.AbsolutePath);
        Assert.Contains("q=Allen%20Ave%20%26%20Opebi%20Rd%2C%20Ikeja", sent.Query, StringComparison.Ordinal);
        Assert.Contains("country=ng", sent.Query, StringComparison.Ordinal);
        Assert.Contains("access_token=pk.secret-token", sent.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parses_a_recorded_response_end_to_end()
    {
        var service = Service(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Feature("address", 3.351472, 6.601838, "12 Allen Avenue, Ikeja, Lagos 101233, Nigeria").ToJsonString(), Encoding.UTF8, "application/json"),
        });

        var outcome = await service.GeocodeAsync("12 Allen Avenue Ikeja", CancellationToken.None);

        Assert.Equal(GeocodeOutcomeKind.Found, outcome.Kind);
        Assert.Equal(6.601838m, outcome.Latitude);
    }

    [Fact]
    public async Task Slow_provider_is_a_failure_not_a_hang()
    {
        var service = Service(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeoutSeconds: 1);

        var outcome = await service.GeocodeAsync("Allen Avenue", CancellationToken.None);

        Assert.Equal(GeocodeOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("did not respond", outcome.Detail, StringComparison.Ordinal);
    }

    private static MapboxGeocodingService Service(Func<HttpRequestMessage, HttpResponseMessage> respond, int timeoutSeconds = 8) =>
        Service((r, _) => Task.FromResult(respond(r)), timeoutSeconds);

    private static MapboxGeocodingService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, int timeoutSeconds = 8)
    {
        var http = new HttpClient(new Stub(respond)) { BaseAddress = new Uri("https://mapbox.test/") };
        var options = Options.Create(new MapboxOptions { AccessToken = "pk.secret-token", CountryCodes = "ng", TimeoutSeconds = timeoutSeconds });
        return new MapboxGeocodingService(http, options, NullLogger<MapboxGeocodingService>.Instance);
    }

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}

/// <summary>
/// Opt-in check against the real Mapbox API:
/// <c>PHANTOM_LIVE_MAPBOX_TOKEN=... dotnet test --project tests/PHANTOM.UnitTests --filter-trait "Category=Live"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class MapboxLiveTests
{
    [Fact]
    public async Task Live_geocoder_places_a_known_street_and_rejects_nonsense()
    {
        var token = Environment.GetEnvironmentVariable("PHANTOM_LIVE_MAPBOX_TOKEN");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "Set PHANTOM_LIVE_MAPBOX_TOKEN to run the live Mapbox check.");
        var options = new MapboxOptions { AccessToken = token, CountryCodes = "ng" };
        using var http = new HttpClient { BaseAddress = options.BaseUrl };
        var service = new MapboxGeocodingService(http, Options.Create(options), NullLogger<MapboxGeocodingService>.Instance);

        var found = await service.GeocodeAsync("Allen Avenue, Ikeja, Lagos", TestContext.Current.CancellationToken);
        Assert.Equal(GeocodeOutcomeKind.Found, found.Kind);
        Assert.InRange(found.Latitude!.Value, 6.4m, 6.8m);
        Assert.InRange(found.Longitude!.Value, 3.2m, 3.5m);
        TestContext.Current.TestOutputHelper?.WriteLine($"{found.PlaceName} ({found.Precision}) {found.Latitude},{found.Longitude}");

        var nonsense = await service.GeocodeAsync("qzxv plorbnik wuuug 99999", TestContext.Current.CancellationToken);
        Assert.NotEqual(GeocodeOutcomeKind.Failed, nonsense.Kind);
        TestContext.Current.TestOutputHelper?.WriteLine($"nonsense → {nonsense.Kind} {nonsense.PlaceName} {nonsense.Detail}");
    }
}
