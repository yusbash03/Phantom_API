using Microsoft.EntityFrameworkCore;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Application.Abstractions;

/// <summary>Unit of work over the PHANTOM database. One <c>SaveChangesAsync</c> = one transaction.</summary>
public interface IPhantomDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
