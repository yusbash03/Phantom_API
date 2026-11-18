using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PHANTOM.Application.Abstractions;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.Infrastructure.Seeding;
using PHANTOM.IntegrationTests.Infrastructure;

namespace PHANTOM.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SeedingTests(PhantomApiFactory factory)
{
    [Theory]
    [InlineData("Production", true, false, false)]
    [InlineData("Production", true, true, true)]
    [InlineData("Development", true, false, true)]
    [InlineData("Development", false, false, false)]
    public async Task Demo_seeding_respects_environment_guard(string environment, bool enabled, bool allowInProduction, bool expected)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var seeder = new DemoDataSeeder(
            scope.ServiceProvider.GetRequiredService<PhantomDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            new StubEnvironment(environment),
            Options.Create(new SeedOptions { Enabled = enabled, AllowDemoInProduction = allowInProduction, DemoUserPassword = "Demo-Password-123" }),
            NullLogger<DemoDataSeeder>.Instance);

        Assert.Equal(expected, seeder.IsAllowed());
        if (!expected)
        {
            Assert.Equal(0, await seeder.SeedAsync(TestContext.Current.CancellationToken));
        }
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PHANTOM.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
