using FluentValidation;
using PHANTOM.Domain.Identity;

namespace PHANTOM.Application.Users;

internal sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(User.MaxEmailLength);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(User.MaxDisplayNameLength);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(User.MinPasswordLength).MaximumLength(256);
        RuleFor(x => x.Roles).NotEmpty().WithMessage("At least one role is required.");
        RuleForEach(x => x.Roles).Must(RoleNames.IsValid).WithMessage("'{PropertyValue}' is not a valid role.");
    }
}

internal sealed class UpdateUserRolesRequestValidator : AbstractValidator<UpdateUserRolesRequest>
{
    public UpdateUserRolesRequestValidator()
    {
        RuleFor(x => x.Roles).NotEmpty().WithMessage("At least one role is required.");
        RuleForEach(x => x.Roles).Must(RoleNames.IsValid).WithMessage("'{PropertyValue}' is not a valid role.");
    }
}
