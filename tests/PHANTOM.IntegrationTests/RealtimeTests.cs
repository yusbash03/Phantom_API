using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PHANTOM.Domain.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

/// <summary>SignalR change events: authorised groups only, identifiers only, after commit (Requirements 13, 2.7).</summary>
[Collection(ApiCollection.Name)]
public sealed class RealtimeTests(PhantomApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_connections_are_refused()
    {
        await using var connection = Build(token: null);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task Staff_hear_about_a_new_report_after_it_is_committed_with_identifiers_only()
    {
        var (_, coordinatorToken, coordinator) = await UserAsync(Role.IncidentCoordinatorId);
        await using var staff = await ConnectAsync(coordinatorToken);

        var incident = await SubmitAsync($"Realtime created {Guid.NewGuid():N}");

        var created = await staff.NextAsync(e => e.Type == "IncidentCreated" && e.IncidentId == incident);
        Assert.Equal(["assignmentId", "at", "incidentId", "responderId", "type"], created.Raw.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        // Published after commit: the record is already readable when the event arrives.
        Assert.Equal(HttpStatusCode.OK, (await coordinator.GetAsync($"/api/v1/incidents/{incident}", Ct)).StatusCode);

        await PostAsync(coordinator, incident, "review", new { });
        await staff.NextAsync(e => e.Type == "IncidentStatusChanged" && e.IncidentId == incident);
        var status = (await coordinator.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{incident}", Ct)).GetProperty("status").GetString();
        Assert.Equal("UnderReview", status);
    }

    [Fact]
    public async Task A_failed_write_publishes_nothing()
    {
        var (_, coordinatorToken, coordinator) = await UserAsync(Role.IncidentCoordinatorId);
        await using var staff = await ConnectAsync(coordinatorToken);
        var incident = await SubmitAsync($"Realtime conflict {Guid.NewGuid():N}");
        await staff.NextAsync(e => e.Type == "IncidentCreated" && e.IncidentId == incident);

        var stale = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{incident}/review", new { version = 999 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.False(await staff.AnyWithinAsync(e => e.Type == "IncidentStatusChanged" && e.IncidentId == incident, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Responders_hear_only_about_their_own_assignments_and_notifications()
    {
        var (_, coordinatorToken, coordinator) = await UserAsync(Role.IncidentCoordinatorId);
        var (mineId, mineToken) = await LinkedResponderAsync();
        var (_, otherToken) = await LinkedResponderAsync();
        await using var staff = await ConnectAsync(coordinatorToken);
        await using var mine = await ConnectAsync(mineToken);
        await using var other = await ConnectAsync(otherToken);

        var incident = await SubmitAsync($"Realtime assignment {Guid.NewGuid():N}");
        await PostAsync(coordinator, incident, "review", new { });
        await PostAsync(coordinator, incident, "verify", new { approvedSeverity = "High" });
        var assigned = await coordinator.PostAsJsonAsync($"/api/v1/incidents/{incident}/assignments", new { responderId = mineId }, Ct);
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var assignmentId = (await assigned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var created = await mine.NextAsync(e => e.Type == "AssignmentCreated" && e.AssignmentId == assignmentId);
        Assert.Equal(mineId, created.ResponderId);
        await mine.NextAsync(e => e.Type == "NotificationCreated" && e.AssignmentId == assignmentId);
        await mine.NextAsync(e => e.Type == "ResponderAvailabilityChanged" && e.ResponderId == mineId);
        await staff.NextAsync(e => e.Type == "AssignmentCreated" && e.AssignmentId == assignmentId);

        Assert.DoesNotContain(mine.Seen, e => e.Type is "IncidentCreated" or "IncidentStatusChanged");
        Assert.False(await other.AnyWithinAsync(e => e.AssignmentId == assignmentId || e.IncidentId == incident, TimeSpan.FromSeconds(1)));
        // A personal notification is for its recipient only, not every staff member.
        Assert.DoesNotContain(staff.Seen, e => e.Type == "NotificationCreated" && e.AssignmentId == assignmentId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private HubConnection Build(string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/operations"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();

    private async Task<Listener> ConnectAsync(string token)
    {
        var connection = Build(token);
        var listener = new Listener(connection);
        await connection.StartAsync(Ct);
        return listener;
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly HubConnection connection;
        private readonly Channel<Event> events = Channel.CreateUnbounded<Event>();

        public Listener(HubConnection connection)
        {
            this.connection = connection;
            connection.On<JsonElement>("event", raw => events.Writer.TryWrite(Event.From(raw)));
        }

        public List<Event> Seen { get; } = [];

        public async Task<Event> NextAsync(Func<Event, bool> match)
        {
            var found = await WaitAsync(match, TimeSpan.FromSeconds(15));
            return found ?? throw new Xunit.Sdk.XunitException($"No matching event. Seen: {string.Join(", ", Seen.Select(e => e.Type))}");
        }

        public async Task<bool> AnyWithinAsync(Func<Event, bool> match, TimeSpan window) =>
            Seen.Any(match) || await WaitAsync(match, window) is not null;

        private async Task<Event?> WaitAsync(Func<Event, bool> match, TimeSpan timeout)
        {
            if (Seen.FirstOrDefault(match) is { } already)
            {
                return already;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(timeout);
            try
            {
                while (await events.Reader.WaitToReadAsync(cts.Token))
                {
                    while (events.Reader.TryRead(out var e))
                    {
                        Seen.Add(e);
                        if (match(e))
                        {
                            return e;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
            }

            return null;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed record Event(string Type, Guid? IncidentId, Guid? AssignmentId, Guid? ResponderId, JsonElement Raw)
    {
        public static Event From(JsonElement raw)
        {
            Guid? Id(string name) => raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetGuid() : null;
            return new Event(raw.GetProperty("type").GetString()!, Id("incidentId"), Id("assignmentId"), Id("responderId"), raw.Clone());
        }
    }

    private async Task<(Guid UserId, string Token, HttpClient Client)> UserAsync(Guid roleId)
    {
        var user = await factory.CreateUserAsync(roleId);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (user.Id, login.AccessToken!, client);
    }

    private async Task<(Guid ResponderId, string Token)> LinkedResponderAsync()
    {
        var (userId, token, _) = await UserAsync(Role.ResponderId);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);
        var created = await admin.PostAsJsonAsync("/api/v1/responders",
            new { callSign = $"RT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), fullName = "Realtime Responder", team = "Test Team", skills = Array.Empty<string>(), userId }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid(), token);
    }

    private async Task<Guid> SubmitAsync(string description)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Realtime test"), "title" },
            { new StringContent(description), "description" },
            { new StringContent("Fire"), "category" },
            { new StringContent($"Realtime site {Guid.NewGuid():N}"), "locationText" },
        };
        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().PostAsync("/api/v1/public/reports", form, Ct)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Incidents.Where(i => i.Description == description).Select(i => i.Id).SingleAsync(Ct);
    }

    private static async Task PostAsync(HttpClient client, Guid id, string action, object body)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/incidents/{id}", Ct)).GetProperty("version").GetInt32();
        var payload = JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["version"] = version;
        var response = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/{action}", payload, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }
}
