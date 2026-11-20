using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.Infrastructure.Seeding;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>Responder management (Requirement 10; seeding 20.1).</summary>
[Collection(ApiCollection.Name)]
public sealed class ResponderTests(PhantomApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Administrators_manage_responders_coordinators_read_and_responders_cannot_list()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var coordinator = await factory.CreateClientForRoleAsync(Role.IncidentCoordinatorId);
        var responder = await factory.CreateClientForRoleAsync(Role.ResponderId);
        var created = await CreateAsync(admin, CallSign());

        Assert.Equal(HttpStatusCode.OK, (await coordinator.GetAsync($"/api/v1/responders/{created.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostAsJsonAsync("/api/v1/responders", NewResponder(CallSign()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostAsJsonAsync($"/api/v1/responders/{created.Id}/deactivate", new { version = created.Version }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync("/api/v1/responders", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.PutAsJsonAsync($"/api/v1/responders/{created.Id}/availability", new { availability = "Unavailable", version = 0 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Create_validates_normalises_and_audits_and_call_signs_are_unique_ignoring_case()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var callSign = CallSign();

        var created = await CreateAsync(admin, callSign.ToLowerInvariant(), skills: ["First Aid", "first aid", "Rescue"]);

        Assert.Equal(callSign, created.CallSign);
        Assert.Equal(["first aid", "rescue"], created.Skills);
        Assert.Equal("Available", created.Availability);
        Assert.True(created.IsActive);
        var duplicate = await admin.PostAsJsonAsync("/api/v1/responders", NewResponder(callSign.ToLowerInvariant()), Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var invalid = await admin.PostAsJsonAsync("/api/v1/responders", NewResponder("bad call sign"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var badEmail = await admin.PostAsJsonAsync("/api/v1/responders", NewResponder(CallSign()) with { Email = "not-an-email" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs
            .AnyAsync(a => a.Action == AuditActions.ResponderCreated && a.EntityId == created.Id.ToString(), Ct));
    }

    [Fact]
    public async Task Only_active_responder_accounts_can_be_linked_and_only_once()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var coordinatorUser = await factory.CreateUserAsync(Role.IncidentCoordinatorId);
        var responderUser = await factory.CreateUserAsync(Role.ResponderId);

        var wrongRole = await admin.PostAsJsonAsync("/api/v1/responders", NewResponder(CallSign()) with { UserId = coordinatorUser.Id }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongRole.StatusCode);

        var linked = await CreateAsync(admin, CallSign(), userId: responderUser.Id);
        Assert.Equal(responderUser.Id, linked.LinkedUser?.Id);

        var twice = await admin.PostAsJsonAsync("/api/v1/responders", NewResponder(CallSign()) with { UserId = responderUser.Id }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    [Fact]
    public async Task Updates_require_the_current_version_and_are_audited_with_before_and_after()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await CreateAsync(admin, CallSign());
        var body = new { fullName = "Renamed Responder", team = "Night Shift", phone = "+234 1", email = (string?)null, skills = new[] { "triage" }, userId = (Guid?)null, version = created.Version };

        var updated = await admin.PutAsJsonAsync($"/api/v1/responders/{created.Id}", body, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = (await updated.Content.ReadFromJsonAsync<ResponderDto>(Json, Ct))!;
        Assert.Equal("Night Shift", after.Team);
        Assert.Equal(created.Version + 1, after.Version);

        var stale = await admin.PutAsJsonAsync($"/api/v1/responders/{created.Id}", body, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.ResponderUpdated && a.EntityId == created.Id.ToString(), Ct);
        Assert.Contains("Test Team", audit.PreviousValue, StringComparison.Ordinal);
        Assert.Contains("Night Shift", audit.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_filters_by_team_skill_availability_active_state_and_search()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var team = $"Team {Guid.NewGuid():N}"[..20];
        var a = await CreateAsync(admin, CallSign(), team: team, skills: ["Water Rescue"]);
        var b = await CreateAsync(admin, CallSign(), team: team, skills: ["triage"]);
        await PutAvailabilityAsync(admin, b, "Unavailable");
        var c = await CreateAsync(admin, CallSign(), team: team, skills: ["triage"]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/responders/{c.Id}/deactivate", new { version = c.Version }, Ct)).StatusCode);

        var teamAll = await ListAsync(admin, $"team={Uri.EscapeDataString(team)}");
        Assert.Equal(3, teamAll.TotalCount);
        Assert.Equal([a.Id], (await ListAsync(admin, $"team={Uri.EscapeDataString(team)}&skill=WATER%20RESCUE")).Items.Select(r => r.Id));
        Assert.Equal([b.Id], (await ListAsync(admin, $"team={Uri.EscapeDataString(team)}&availability=Unavailable&isActive=true")).Items.Select(r => r.Id));
        Assert.Equal([c.Id], (await ListAsync(admin, $"team={Uri.EscapeDataString(team)}&isActive=false")).Items.Select(r => r.Id));
        Assert.Equal([a.Id], (await ListAsync(admin, $"search={a.CallSign.ToLowerInvariant()}")).Items.Select(r => r.Id));

        var facets = await admin.GetFromJsonAsync<JsonElement>("/api/v1/responders/facets", Ct);
        Assert.Contains(team, facets.GetProperty("teams").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("water rescue", facets.GetProperty("skills").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Deactivated_responders_cannot_change_availability_and_return_unavailable()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await CreateAsync(admin, CallSign());

        var deactivated = await PostAsync(admin, $"/api/v1/responders/{created.Id}/deactivate", new { version = created.Version });
        Assert.False(deactivated.IsActive);
        Assert.Equal("Unavailable", deactivated.Availability);

        var blocked = await admin.PutAsJsonAsync($"/api/v1/responders/{created.Id}/availability", new { availability = "Available", version = deactivated.Version }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);

        var reactivated = await PostAsync(admin, $"/api/v1/responders/{created.Id}/activate", new { version = deactivated.Version });
        Assert.True(reactivated.IsActive);
        Assert.Equal("Unavailable", reactivated.Availability);
        var available = await PutAvailabilityAsync(admin, reactivated, "Available");
        Assert.Equal("Available", available.Availability);
    }

    [Fact]
    public async Task Responders_set_their_own_availability_but_never_assigned()
    {
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);
        var self = factory.CreateClient();
        self.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, (await self.GetAsync("/api/v1/responders/me", Ct)).StatusCode);
        var created = await CreateAsync(admin, CallSign(), userId: user.Id);

        var mine = await self.GetFromJsonAsync<ResponderDto>("/api/v1/responders/me", Json, Ct);
        Assert.Equal(created.Id, mine!.Id);

        var off = await self.PutAsJsonAsync("/api/v1/responders/me/availability", new { availability = "Unavailable" }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal("Unavailable", (await off.Content.ReadFromJsonAsync<ResponderDto>(Json, Ct))!.Availability);

        var assigned = await self.PutAsJsonAsync("/api/v1/responders/me/availability", new { availability = "Assigned" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, assigned.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.ResponderAvailabilityChanged && a.EntityId == created.Id.ToString(), Ct);
        Assert.Equal(user.Id, audit.ActorUserId);
    }

    [Fact]
    public async Task Demo_seeding_adds_labelled_responders_linked_to_the_demo_responder_account_once()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        var seeder = new DemoDataSeeder(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher>(), scope.ServiceProvider.GetRequiredService<IClock>(),
            new DevelopmentEnvironment(), Options.Create(new SeedOptions { Enabled = true, DemoUserPassword = "Demo-Password-123" }), NullLogger<DemoDataSeeder>.Instance);

        await seeder.SeedAsync(Ct);
        Assert.Equal(0, await seeder.SeedAsync(Ct));

        var callSigns = DemoDataSeeder.DemoResponders.Select(r => r.CallSign).ToList();
        var seeded = await db.Responders.AsNoTracking().Where(r => callSigns.Contains(r.CallSign)).ToListAsync(Ct);
        Assert.Equal(callSigns.Count, seeded.Count);
        Assert.All(seeded, r => Assert.True(r.IsDemoData));
        var demoResponderUser = await db.Users.SingleAsync(u => u.Email == "responder@phantom.demo", Ct);
        Assert.Equal(demoResponderUser.Id, seeded.Single(r => r.CallSign == "MED-01").UserId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string CallSign() => $"T-{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    private static NewResponderBody NewResponder(string callSign) => new(callSign, "Test Responder", "Test Team", null, null, [], null);

    private static async Task<ResponderDto> CreateAsync(HttpClient admin, string callSign, string team = "Test Team", string[]? skills = null, Guid? userId = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/responders", NewResponder(callSign) with { Team = team, Skills = skills ?? [], UserId = userId }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<ResponderDto>(Json, Ct))!;
    }

    private static async Task<ResponderDto> PutAvailabilityAsync(HttpClient admin, ResponderDto responder, string availability)
    {
        var response = await admin.PutAsJsonAsync($"/api/v1/responders/{responder.Id}/availability", new { availability, version = responder.Version }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ResponderDto>(Json, Ct))!;
    }

    private static async Task<ResponderDto> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ResponderDto>(Json, Ct))!;
    }

    private static async Task<Page> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<Page>($"/api/v1/responders?{query}", Json, Ct))!;

    private sealed record NewResponderBody(string CallSign, string FullName, string Team, string? Phone, string? Email, string[] Skills, Guid? UserId);

    private sealed record UserRefDto(Guid Id, string DisplayName);

    private sealed record ResponderDto(Guid Id, string CallSign, string Team, List<string> Skills, string Availability, bool IsActive, UserRefDto? LinkedUser, int Version);

    private sealed record Page(List<ResponderDto> Items, int TotalCount);

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PHANTOM.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
