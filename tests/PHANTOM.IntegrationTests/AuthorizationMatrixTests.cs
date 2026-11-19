using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Identity;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Critical properties 1 and 9: restricted operations are enforced server-side.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationMatrixTests(PhantomApiFactory factory)
{
    // Endpoints intentionally reachable without authentication. Anything new must be added here deliberately.
    private static readonly HashSet<string> PublicEndpoints =
    [
        "POST api/v1/auth/login",
        "POST api/v1/auth/refresh",
        "POST api/v1/auth/logout",
        "POST api/v1/public/reports",
        "GET api/v1/public/reports/{reference}/status",
        "GET /health/live",
        "GET /health/ready",
    ];

    [Fact]
    public async Task Every_non_public_endpoint_rejects_anonymous_callers()
    {
        var endpoints = ApiEndpoints().ToList();
        Assert.NotEmpty(endpoints);
        var client = factory.CreateClient();

        foreach (var (method, template, endpoint) in endpoints)
        {
            var key = $"{method} {template}";
            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            Assert.True(!allowsAnonymous || PublicEndpoints.Contains(key), $"Unexpected anonymous endpoint: {key}");
            if (allowsAnonymous)
            {
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), "/" + Concrete(template).TrimStart('/'));
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{key} returned {(int)response.StatusCode} to an anonymous caller");
        }
    }

    public static TheoryData<string, string, string, HttpStatusCode> UserAdministrationMatrix => new()
    {
        { "Responder", "GET", "/api/v1/users", HttpStatusCode.Forbidden },
        { "Coordinator", "GET", "/api/v1/users", HttpStatusCode.Forbidden },
        { "Administrator", "GET", "/api/v1/users", HttpStatusCode.OK },
        { "Responder", "POST", "/api/v1/users/{id}/deactivate", HttpStatusCode.Forbidden },
        { "Coordinator", "PUT", "/api/v1/users/{id}/roles", HttpStatusCode.Forbidden },
        { "Coordinator", "POST", "/api/v1/users", HttpStatusCode.Forbidden },
    };

    [Theory]
    [MemberData(nameof(UserAdministrationMatrix))]
    public async Task User_administration_is_restricted_to_administrators(string role, string method, string path, HttpStatusCode expected)
    {
        var roleId = role switch
        {
            "Administrator" => Role.AdministratorId,
            "Coordinator" => Role.IncidentCoordinatorId,
            _ => Role.ResponderId,
        };
        var target = await factory.CreateUserAsync(Role.ResponderId);
        var client = await factory.CreateClientForRoleAsync(roleId);

        using var request = new HttpRequestMessage(new HttpMethod(method), path.Replace("{id}", target.Id.ToString(), StringComparison.Ordinal));
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { roles = new[] { RoleNames.Administrator } });
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    private IEnumerable<(string Method, string Template, RouteEndpoint Endpoint)> ApiEndpoints()
    {
        var sources = factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        foreach (var endpoint in sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                yield return (method, endpoint.RoutePattern.RawText ?? string.Empty, endpoint);
            }
        }
    }

    private static string Concrete(string template) =>
        System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", Guid.NewGuid().ToString());
}
