using Employee360.Integration.Domain.ValueObjects;

namespace Employee360.Integration.Application.Contracts;

/// <summary>Target contract for the Employee 360 API (deliberately different naming from the source).</summary>
public sealed record Employee360UpdateRequest(
    string EmployeeId,
    string GivenName,
    string FamilyName,
    string Department,
    string Location,
    DateTimeOffset EffectiveFrom,
    string SourceSystem,
    string SourceEventId)
{
    public const string D365SourceSystem = "D365-HR";

    public static Employee360UpdateRequest From(ValidEmployeeUpdate update, string canonicalLocation) => new(
        update.EmployeeId,
        update.FirstName,
        update.LastName,
        update.Department,
        canonicalLocation,
        update.EffectiveDate,
        D365SourceSystem,
        update.EventId);
}

public enum DeliveryOutcome
{
    Success,
    TransientFailure,
    PermanentFailure
}

public sealed record DeliveryResult(DeliveryOutcome Outcome, int? StatusCode, string? Detail)
{
    public static DeliveryResult Success(int statusCode) => new(DeliveryOutcome.Success, statusCode, null);
    public static DeliveryResult Transient(string detail, int? statusCode = null) => new(DeliveryOutcome.TransientFailure, statusCode, detail);
    public static DeliveryResult Permanent(string detail, int? statusCode = null) => new(DeliveryOutcome.PermanentFailure, statusCode, detail);
}
