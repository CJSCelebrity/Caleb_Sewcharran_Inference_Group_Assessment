namespace Employee360.Integration.Domain.ValueObjects;

/// <summary>
/// An employee change that has passed validation: required fields are present and trimmed,
/// and the effective date is an unambiguous UTC timestamp.
/// </summary>
public sealed record ValidEmployeeUpdate(
    string EventId,
    string EmployeeId,
    string FirstName,
    string LastName,
    string Department,
    string Location,
    DateTimeOffset EffectiveDate);
