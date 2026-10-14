using Employee360.Integration.Application.Contracts;

namespace Employee360.Integration.Application.Abstractions;

/// <summary>Port to the downstream Employee 360 API.</summary>
public interface IEmployee360Client
{
    Task<DeliveryResult> UpsertEmployeeAsync(Employee360UpdateRequest request, string correlationId, CancellationToken cancellationToken);
}
