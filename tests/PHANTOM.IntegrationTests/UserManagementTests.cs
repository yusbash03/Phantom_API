using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class UserManagementTests(PhantomApiFactory factory)
{
    [Fact]
    public async Task Administrator_creates_user_and_change_is_audited()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var email = $"new-{Guid.NewGuid():N}@test.local";

        var response = await admin.PostAsJsonAsync("/api/v1/users",
            new { email, displayName = "New Coordinator", password = "A-strong-password-1", roles = new[] { RoleNames.IncidentCoordinator } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = created.GetProperty("id").GetString();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.UserCreated && a.EntityId == id, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, (await factory.LoginAsync(email, "A-strong-password-1")).Response.StatusCode);
    }

    [Fact]
    public async Task Invalid_user_request_returns_field_errors_and_persists_nothing()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var email = $"bad-{Guid.NewGuid():N}@test.local";

        var response = await admin.PostAsJsonAsync("/api/v1/users",
            new { email, displayName = "", password = "short", roles = new[] { "SuperUser" } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("password", out _));
        Assert.True(errors.TryGetProperty("displayName", out _));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Users.AnyAsync(u => u.Email == email, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Last_active_administrator_cannot_be_demoted_or_deactivated()
    {
        // Make this administrator the only active one for the duration of the check.
        var admin = await factory.CreateUserAsync(Role.AdministratorId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            await db.Users.Where(u => u.Id != admin.Id && u.Roles.Any(r => r.RoleId == Role.AdministratorId))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), TestContext.Current.CancellationToken);
        }

        var login = await factory.LoginAsync(admin.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var demote = await client.PutAsJsonAsync($"/api/v1/users/{admin.Id}/roles", new { roles = new[] { RoleNames.Responder } }, TestContext.Current.CancellationToken);
        var deactivate = await client.PostAsync($"/api/v1/users/{admin.Id}/deactivate", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
    }
}
