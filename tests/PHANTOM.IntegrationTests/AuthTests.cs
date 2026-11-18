using System.Net;
using System.Net.Http.Headers;
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
public sealed class AuthTests(PhantomApiFactory factory)
{
    [Fact]
    public async Task Valid_login_returns_access_token_and_hardened_refresh_cookie()
    {
        var user = await factory.CreateUserAsync(Role.IncidentCoordinatorId);
        var login = await factory.LoginAsync(user.Email);

        Assert.Equal(HttpStatusCode.OK, login.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(login.AccessToken));
        var setCookie = login.Response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("phantom_refresh=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);

        var body = await login.Response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Simultaneous_sign_ins_by_the_same_user_all_succeed()
    {
        var user = await factory.CreateUserAsync(Role.IncidentCoordinatorId);

        var logins = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => factory.LoginAsync(user.Email)));

        Assert.All(logins, l => Assert.Equal(HttpStatusCode.OK, l.Response.StatusCode));
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(stored.LastLoginAt);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_return_identical_errors()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var wrongPassword = await factory.LoginAsync(user.Email, "not-the-right-password");
        var unknownEmail = await factory.LoginAsync($"nobody-{Guid.NewGuid():N}@test.local", "not-the-right-password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.Response.StatusCode);
        Assert.Equal(await TitleAsync(wrongPassword.Response), await TitleAsync(unknownEmail.Response));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_revokes_the_family()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);
        var original = login.RefreshCookie!;

        var first = await RefreshAsync(original);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = CookieFrom(first)!;
        Assert.NotEqual(original, rotated);

        // Replaying the original (already rotated) token is treated as theft.
        var replay = await RefreshAsync(original);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...which also kills the legitimately rotated token.
        var afterReuse = await RefreshAsync(rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.RefreshTokenReuseDetected && a.EntityId == user.Id.ToString(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_rereads_roles_from_the_database()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
            var tracked = await db.Users.Include(u => u.Roles).SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
            tracked.SetRoles([Role.IncidentCoordinatorId]);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var refreshed = await RefreshAsync(login.RefreshCookie!);
        using var json = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var roles = json.RootElement.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal([RoleNames.IncidentCoordinator], roles);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Cookie", login.RefreshCookie);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(login.RefreshCookie!)).StatusCode);
    }

    [Fact]
    public async Task Deactivated_user_cannot_log_in_or_refresh()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);
        var admin = await factory.CreateClientForRoleAsync(Role.AdministratorId);

        var deactivate = await admin.PostAsync($"/api/v1/users/{user.Id}/deactivate", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.LoginAsync(user.Email)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(login.RefreshCookie!)).StatusCode);
    }

    [Fact]
    public async Task Account_is_throttled_after_repeated_failures_even_with_correct_password()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        for (var i = 0; i < 10; i++)
        {
            await factory.LoginAsync(user.Email, "wrong-password-attempt");
        }

        var correct = await factory.LoginAsync(user.Email);
        Assert.Equal(HttpStatusCode.Unauthorized, correct.Response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_current_user_and_rejects_tampered_tokens()
    {
        var user = await factory.CreateUserAsync(Role.ResponderId);
        var login = await factory.LoginAsync(user.Email);
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(user.Email, me.GetProperty("email").GetString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken![..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string cookie)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string? CookieFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("phantom_refresh=", StringComparison.Ordinal) && v.Length > "phantom_refresh=".Length)
            : null;

    private static async Task<string?> TitleAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("title").GetString();
    }
}
