using FluentValidation;
using Microsoft.EntityFrameworkCore;
using PHANTOM.Application.Abstractions;
using PHANTOM.Application.Audit;
using PHANTOM.Application.Common;
using PHANTOM.Application.Incidents;
using PHANTOM.Domain.Audit;
using PHANTOM.Domain.Common;
using PHANTOM.Domain.Identity;
using PHANTOM.Domain.Responders;

namespace PHANTOM.Application.Responders;

public interface IResponderService
{
    Task<PagedResult<ResponderResponse>> ListAsync(ResponderListQuery query, CancellationToken cancellationToken);
    Task<ResponderFacets> FacetsAsync(CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> GetMineAsync(CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> CreateAsync(CreateResponderRequest request, CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> UpdateAsync(Guid id, UpdateResponderRequest request, CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> DeactivateAsync(Guid id, ResponderVersionRequest request, CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> ActivateAsync(Guid id, ResponderVersionRequest request, CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> SetAvailabilityAsync(Guid id, SetAvailabilityRequest request, CancellationToken cancellationToken);
    Task<Result<ResponderResponse>> SetMyAvailabilityAsync(SetAvailabilityRequest request, CancellationToken cancellationToken);
}

internal sealed class ResponderService(
    IPhantomDbContext db,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock,
    IValidator<CreateResponderRequest> createValidator,
    IValidator<UpdateResponderRequest> updateValidator,
    IValidator<SetAvailabilityRequest> availabilityValidator) : IResponderService
{
    private static readonly Error NotFound = Error.NotFound("responder.not_found", "Responder not found.");
    private static readonly Error StaleVersion = Error.Conflict("responder.version_conflict", "This responder was changed by someone else. Reload and try again.");

    public async Task<PagedResult<ResponderResponse>> ListAsync(ResponderListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var responders = db.Responders.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Team))
        {
            var team = query.Team.Trim();
            responders = responders.Where(r => r.Team == team);
        }

        if (!string.IsNullOrWhiteSpace(query.Skill))
        {
            var skill = query.Skill.Trim().ToLowerInvariant();
            responders = responders.Where(r => r.Skills.Contains(skill));
        }

        if (query.Availability.HasValue)
        {
            responders = responders.Where(r => r.Availability == query.Availability.Value);
        }

        if (query.IsActive.HasValue)
        {
            responders = responders.Where(r => r.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Expression tree translated to SQL lower()/LIKE, not executed in .NET.
            responders = responders.Where(r => r.FullName.ToLower().Contains(term) || r.CallSign.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await responders.CountAsync(cancellationToken);
        var items = await responders
            .OrderBy(r => r.CallSign)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<ResponderResponse>(await ToResponsesAsync(items, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<ResponderFacets> FacetsAsync(CancellationToken cancellationToken)
    {
        var teams = await db.Responders.AsNoTracking().Select(r => r.Team).Distinct().OrderBy(t => t).ToListAsync(cancellationToken);
        var skills = (await db.Responders.AsNoTracking().Select(r => r.Skills).ToListAsync(cancellationToken))
            .SelectMany(s => s).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new ResponderFacets(teams, skills);
    }

    public async Task<Result<ResponderResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var responder = await db.Responders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return responder is null ? NotFound : (await ToResponsesAsync([responder], cancellationToken))[0];
    }

    public async Task<Result<ResponderResponse>> GetMineAsync(CancellationToken cancellationToken)
    {
        var responder = await MineAsync(tracking: false, cancellationToken);
        return responder is null
            ? Error.NotFound("responder.not_linked", "Your account is not linked to a responder profile. Ask an administrator to link it.")
            : (await ToResponsesAsync([responder], cancellationToken))[0];
    }

    public async Task<Result<ResponderResponse>> CreateAsync(CreateResponderRequest request, CancellationToken cancellationToken)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var callSign = Responder.NormaliseCallSign(request.CallSign)!;
        if (await db.Responders.AnyAsync(r => r.CallSign == callSign, cancellationToken))
        {
            return Error.Conflict("responder.call_sign_taken", $"Call sign {callSign} is already in use.");
        }

        var linkError = await ValidateLinkedUserAsync(request.UserId, responderId: null, cancellationToken);
        if (linkError is not null)
        {
            return linkError;
        }

        var created = Responder.Create(Guid.CreateVersion7(), callSign, request.FullName, request.Team, request.Phone, request.Email,
            request.Skills ?? [], request.UserId, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var responder = created.Value;
        db.Responders.Add(responder);
        audit.Record(AuditActions.ResponderCreated, nameof(Responder), responder.Id.ToString(), newValue: Snapshot(responder));
        return await SaveAndReturnAsync(responder, cancellationToken);
    }

    public Task<Result<ResponderResponse>> UpdateAsync(Guid id, UpdateResponderRequest request, CancellationToken cancellationToken) =>
        MutateAsync(id, request.Version, cancellationToken: cancellationToken, apply: async responder =>
        {
            var validation = await updateValidator.ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return ValidationFailedError.From(validation);
            }

            var linkError = await ValidateLinkedUserAsync(request.UserId, responder.Id, cancellationToken);
            if (linkError is not null)
            {
                return linkError;
            }

            var before = Snapshot(responder);
            var result = responder.UpdateDetails(request.FullName, request.Team, request.Phone, request.Email, request.Skills ?? [], request.UserId);
            if (result.IsSuccess)
            {
                audit.Record(AuditActions.ResponderUpdated, nameof(Responder), responder.Id.ToString(), before, Snapshot(responder));
            }

            return result;
        });

    public Task<Result<ResponderResponse>> DeactivateAsync(Guid id, ResponderVersionRequest request, CancellationToken cancellationToken) =>
        MutateAsync(id, request.Version, cancellationToken: cancellationToken, apply: async responder =>
        {
            var result = responder.Deactivate(await HasActiveAssignmentAsync(responder.Id, cancellationToken), clock.UtcNow);
            if (result.IsSuccess)
            {
                audit.Record(AuditActions.ResponderDeactivated, nameof(Responder), responder.Id.ToString(),
                    new { isActive = true }, new { isActive = false, availability = responder.Availability.ToString() });
            }

            return result;
        });

    public Task<Result<ResponderResponse>> ActivateAsync(Guid id, ResponderVersionRequest request, CancellationToken cancellationToken) =>
        MutateAsync(id, request.Version, cancellationToken: cancellationToken, apply: responder =>
        {
            var result = responder.Activate(clock.UtcNow);
            if (result.IsSuccess)
            {
                audit.Record(AuditActions.ResponderActivated, nameof(Responder), responder.Id.ToString(),
                    new { isActive = false }, new { isActive = true, availability = responder.Availability.ToString() });
            }

            return Task.FromResult(result);
        });

    public async Task<Result<ResponderResponse>> SetAvailabilityAsync(Guid id, SetAvailabilityRequest request, CancellationToken cancellationToken)
    {
        var validation = await availabilityValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        if (request.Version is null)
        {
            return Error.Validation("responder.version_required", "The version you last read is required.") with
            {
                Fields = new Dictionary<string, string[]> { ["version"] = ["The version you last read is required."] },
            };
        }

        return await MutateAsync(id, request.Version.Value, cancellationToken: cancellationToken, apply: responder => ApplyAvailabilityAsync(responder, request.Availability!.Value, cancellationToken));
    }

    /// <summary>A responder sets their own availability (10.4). No version: it is their own status, latest wins.</summary>
    public async Task<Result<ResponderResponse>> SetMyAvailabilityAsync(SetAvailabilityRequest request, CancellationToken cancellationToken)
    {
        var validation = await availabilityValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationFailedError.From(validation);
        }

        var responder = await MineAsync(tracking: true, cancellationToken);
        if (responder is null)
        {
            return Error.NotFound("responder.not_linked", "Your account is not linked to a responder profile. Ask an administrator to link it.");
        }

        var result = await ApplyAvailabilityAsync(responder, request.Availability!.Value, cancellationToken);
        return result.IsFailure ? result.Error! : await SaveAndReturnAsync(responder, cancellationToken);
    }

    private async Task<Result> ApplyAvailabilityAsync(Responder responder, ResponderAvailability availability, CancellationToken cancellationToken)
    {
        var before = responder.Availability;
        var result = responder.SetAvailability(availability, await HasActiveAssignmentAsync(responder.Id, cancellationToken), clock.UtcNow);
        if (result.IsSuccess && before != responder.Availability)
        {
            audit.Record(AuditActions.ResponderAvailabilityChanged, nameof(Responder), responder.Id.ToString(),
                new { availability = before.ToString() }, new { availability = responder.Availability.ToString() });
        }

        return result;
    }

    /// <summary>
    /// Whether the responder has a non-terminal assignment. Assignments are introduced by task 24, which
    /// replaces this with a query; until then no responder can have one.
    /// </summary>
    private static Task<bool> HasActiveAssignmentAsync(Guid responderId, CancellationToken cancellationToken)
    {
        _ = responderId;
        _ = cancellationToken;
        return Task.FromResult(false);
    }

    private async Task<Result<ResponderResponse>> MutateAsync(Guid id, int version, Func<Responder, Task<Result>> apply, CancellationToken cancellationToken)
    {
        var responder = await db.Responders.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (responder is null)
        {
            return NotFound;
        }

        if (responder.Version != version)
        {
            return StaleVersion;
        }

        var result = await apply(responder);
        return result.IsFailure ? result.Error! : await SaveAndReturnAsync(responder, cancellationToken);
    }

    private async Task<Result<ResponderResponse>> SaveAndReturnAsync(Responder responder, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion;
        }
        catch (DbUpdateException)
        {
            // Unique call sign or linked account, taken by a concurrent request.
            return Error.Conflict("responder.duplicate", "That call sign or account is already used by another responder.");
        }

        return (await ToResponsesAsync([responder], cancellationToken))[0];
    }

    /// <summary>A linked account must be an active Responder not linked to anyone else.</summary>
    private async Task<Error?> ValidateLinkedUserAsync(Guid? userId, Guid? responderId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.IsActive || !user.HasRole(Role.ResponderId))
        {
            return Error.Validation("responder.user_invalid", "The linked account must be an active user with the Responder role.") with
            {
                Fields = new Dictionary<string, string[]> { ["userId"] = ["Choose an active user with the Responder role."] },
            };
        }

        if (await db.Responders.AnyAsync(r => r.UserId == userId && r.Id != responderId, cancellationToken))
        {
            return Error.Conflict("responder.user_taken", "That account is already linked to another responder.");
        }

        return null;
    }

    private Task<Responder?> MineAsync(bool tracking, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Task.FromResult<Responder?>(null);
        }

        var source = tracking ? db.Responders : db.Responders.AsNoTracking();
        return source.FirstOrDefaultAsync(r => r.UserId == userId, cancellationToken);
    }

    private async Task<List<ResponderResponse>> ToResponsesAsync(List<Responder> responders, CancellationToken cancellationToken)
    {
        var userIds = responders.Select(r => r.UserId).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        return responders.Select(r => new ResponderResponse(
                r.Id, r.CallSign, r.FullName, r.Team, r.Phone, r.Email, r.Skills, r.Availability, r.IsActive,
                r.UserId is { } id && users.TryGetValue(id, out var user) ? user : null,
                r.LastStatusAt, r.IsDemoData, r.CreatedAt, r.Version))
            .ToList();
    }

    private static object Snapshot(Responder r) =>
        new { r.CallSign, r.FullName, r.Team, r.Phone, r.Email, r.Skills, r.UserId, availability = r.Availability.ToString(), r.IsActive };
}
