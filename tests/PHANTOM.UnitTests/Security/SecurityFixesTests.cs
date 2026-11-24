using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Auth;
using PHANTOM.Application.Common;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Incidents;
using PHANTOM.Infrastructure.Identity;

namespace PHANTOM.UnitTests.Security;

/// <summary>Regression tests for the task 35 security review findings.</summary>
public sealed class SecurityFixesTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    }

    private static readonly IOptions<AuthOptions> Auth = Options.Create(new AuthOptions { MaxFailedLoginsPerAccount = 3, FailedLoginWindowMinutes = 15 });

    [Fact]
    public void Login_throttling_is_per_client_so_an_attacker_cannot_lock_out_the_real_user()
    {
        var throttle = new LoginThrottle(new FakeClock(), Auth);
        var account = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure(account, "203.0.113.9");
        }

        Assert.True(throttle.IsBlocked(account, "203.0.113.9"));
        Assert.False(throttle.IsBlocked(account, "198.51.100.4"));
        Assert.False(throttle.IsBlocked(Guid.NewGuid(), "203.0.113.9"));
    }

    [Fact]
    public void A_block_expires_with_its_window_and_success_clears_the_count()
    {
        var clock = new FakeClock();
        var throttle = new LoginThrottle(clock, Auth);
        var account = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure(account, "203.0.113.9");
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(16);
        Assert.False(throttle.IsBlocked(account, "203.0.113.9"));

        throttle.RecordFailure(account, "203.0.113.9");
        throttle.RecordFailure(account, "203.0.113.9");
        throttle.Reset(account, "203.0.113.9");
        throttle.RecordFailure(account, "203.0.113.9");
        Assert.False(throttle.IsBlocked(account, "203.0.113.9"));
    }

    [Fact]
    public void Ip_pseudonyms_are_keyed_so_they_cannot_be_reversed_without_the_secret()
    {
        static HmacClientIpHasher Hasher(string signingKey, string? ipKey = null) =>
            new(Options.Create(new JwtOptions { SigningKey = signingKey }),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Privacy:IpHashKey"] = ipKey }).Build());

        var a = Hasher("first-signing-key-that-is-long-enough-123");
        var b = Hasher("other-signing-key-that-is-long-enough-456");
        Assert.Equal(a.Hash("203.0.113.9"), a.Hash(" 203.0.113.9 "));
        Assert.NotEqual(a.Hash("203.0.113.9"), b.Hash("203.0.113.9"));
        Assert.NotEqual(a.Hash("203.0.113.9"), Hasher("first-signing-key-that-is-long-enough-123", "dedicated-ip-key").Hash("203.0.113.9"));
        Assert.Equal(64, a.Hash("203.0.113.9")!.Length);
        Assert.Null(a.Hash(null));
        // The old scheme was SHA-256 of a public salt + IP: the keyed value must differ from it.
        var unkeyed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("phantom-ip-hash-salt203.0.113.9"u8));
        Assert.NotEqual(unkeyed, a.Hash("203.0.113.9"));
    }

    [Theory]
    [InlineData(AttachmentKind.Audio, "audio/mpeg", "mp3")]
    [InlineData(AttachmentKind.Audio, "audio/webm", "webm")]
    [InlineData(AttachmentKind.Image, "image/jpeg", "jpg")]
    public void Downloads_use_our_own_name_and_extension_never_the_reporters(AttachmentKind kind, string contentType, string extension)
    {
        var attachment = Attachment.Create(Guid.NewGuid(), Guid.NewGuid(), kind, "key", contentType, 10, "sha", "call-recording.html", DateTimeOffset.UnixEpoch);
        var name = IncidentService.DownloadName(attachment);
        Assert.Matches($"^phantom-(recording|photo)-[0-9a-f]{{12}}\\.{extension}$", name);
        Assert.DoesNotContain("html", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_page_number_is_clamped_instead_of_overflowing()
    {
        var page = new PageRequest(int.MaxValue, 100);
        Assert.Equal(PageRequest.MaxPage, page.SafePage);
        Assert.True(page.Skip > 0);
    }

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Production", "Local", true)]
    [InlineData("Development", null, false)]
    [InlineData("Production", "Ftp", true)]
    public void Production_refuses_local_disk_storage(string environment, string? provider, bool throws)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = provider,
            ["Ai:WorkerEnabled"] = "false",
            ["Mapbox:WorkerEnabled"] = "false",
            ["Weather:WorkerEnabled"] = "false",
        }).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        var env = new HostEnvironment { EnvironmentName = environment };
        if (throws)
        {
            Assert.Throws<InvalidOperationException>(() => PHANTOM.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration, env));
        }
        else
        {
            PHANTOM.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration, env);
        }
    }

    private sealed class HostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "PHANTOM.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
