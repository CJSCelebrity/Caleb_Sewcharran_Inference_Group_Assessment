using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Employee360.Integration.Application.Contracts;
using Employee360.Integration.Domain.ValueObjects;

namespace Employee360.Integration.Application.Processing;

public sealed record ValidationResult(ValidEmployeeUpdate? Value, IReadOnlyList<string> Errors)
{
    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsValid => Value is not null;
}

public sealed class EventValidator
{
    public const string SupportedEventType = "EmployeeUpdated";

    private static readonly Regex EmployeeIdPattern = new(@"^EMP\d+$", RegexOptions.Compiled);

    // Timestamps without an explicit offset are ambiguous (local time of which system?),
    // so they are rejected rather than guessed.
    private static readonly Regex ExplicitOffsetPattern = new(@"(Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ValidationResult Validate(EmployeeUpdatedEvent evt)
    {
        var errors = new List<string>();

        var eventId = Required(evt.EventId, "eventId", errors);

        if (!string.Equals(evt.EventType, SupportedEventType, StringComparison.Ordinal))
        {
            errors.Add($"eventType must be '{SupportedEventType}' but was '{evt.EventType ?? "null"}'");
        }

        var employeeId = Required(evt.EmployeeId, "employeeId", errors);
        if (employeeId is not null && !EmployeeIdPattern.IsMatch(employeeId))
        {
            errors.Add($"employeeId '{employeeId}' is not in the expected EMP<digits> format");
        }

        var firstName = Required(evt.FirstName, "firstName", errors);
        var lastName = Required(evt.LastName, "lastName", errors);
        var department = Required(evt.Department, "department", errors);
        var location = Required(evt.Location, "location", errors);

        var effectiveDate = default(DateTimeOffset);
        var rawEffectiveDate = Required(evt.EffectiveDate, "effectiveDate", errors);
        if (rawEffectiveDate is not null &&
            (!ExplicitOffsetPattern.IsMatch(rawEffectiveDate) ||
             !DateTimeOffset.TryParse(rawEffectiveDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out effectiveDate)))
        {
            errors.Add($"effectiveDate '{rawEffectiveDate}' must be an ISO-8601 timestamp with an explicit offset, e.g. 2026-10-01T08:30:00Z");
        }

        if (errors.Count > 0)
        {
            return new ValidationResult(null, errors);
        }

        return new ValidationResult(
            new ValidEmployeeUpdate(eventId!, employeeId!, firstName!, lastName!, department!, location!, effectiveDate.ToUniversalTime()),
            errors);
    }

    private static string? Required(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required");
            return null;
        }

        return value.Trim();
    }
}
