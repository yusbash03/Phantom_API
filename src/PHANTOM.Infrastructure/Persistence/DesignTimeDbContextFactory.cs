using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PHANTOM.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef migrations add</c>, which never connects. Applying migrations uses
/// <c>--connection</c> or the application's configured connection string.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PhantomDbContext>
{
    public PhantomDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PhantomDbContext>();
        DependencyInjection.ConfigureDbContext(options, "Host=localhost;Database=phantom_design_time");
        return new PhantomDbContext(options.Options);
    }
}
