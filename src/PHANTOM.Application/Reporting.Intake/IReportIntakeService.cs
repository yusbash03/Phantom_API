using PHANTOM.Domain.Common;

namespace PHANTOM.Application.Reporting.Intake;

public interface IReportIntakeService
{
    Task<Result<SubmitReportResponse>> SubmitAsync(SubmitReportRequest request, CancellationToken cancellationToken = default);

    Task<Result<PublicStatusResponse>> GetPublicStatusAsync(string reference, CancellationToken cancellationToken = default);
}
