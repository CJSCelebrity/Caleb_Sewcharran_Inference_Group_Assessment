using Employee360.Integration.Domain.ValueObjects;

namespace Employee360.Integration.Domain.Entities;

/// <summary>
/// The last state this integration successfully delivered for an employee.
/// Used to detect out-of-order events.
/// </summary>
public sealed class EmployeeState
{
    public string EmployeeId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTimeOffset EffectiveDate { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Events carry the full employee snapshot, so only a strictly newer effective date may
    /// replace what has already been delivered. Equal dates are treated as stale (first one wins).
    /// </summary>
    public bool IsSupersededBy(DateTimeOffset effectiveDate) => effectiveDate > EffectiveDate;

    public void Apply(ValidEmployeeUpdate update, string canonicalLocation, DateTimeOffset now)
    {
        FirstName = update.FirstName;
        LastName = update.LastName;
        Department = update.Department;
        Location = canonicalLocation;
        EffectiveDate = update.EffectiveDate;
        LastEventId = update.EventId;
        UpdatedAt = now;
    }
}
