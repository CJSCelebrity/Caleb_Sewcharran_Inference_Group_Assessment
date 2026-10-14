namespace Employee360.MockApi;

/// <summary>Contract the Employee 360 service accepts.</summary>
public sealed record EmployeeUpdate(
    string EmployeeId,
    string GivenName,
    string FamilyName,
    string Department,
    string Location,
    DateTimeOffset EffectiveFrom,
    string SourceSystem,
    string SourceEventId)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(GivenName)) errors.Add("givenName is required");
        if (string.IsNullOrWhiteSpace(FamilyName)) errors.Add("familyName is required");
        if (string.IsNullOrWhiteSpace(Department)) errors.Add("department is required");
        if (string.IsNullOrWhiteSpace(Location)) errors.Add("location is required");
        if (EffectiveFrom == default) errors.Add("effectiveFrom is required");
        return errors;
    }
}

public sealed record Employee360Record(
    string EmployeeId,
    string GivenName,
    string FamilyName,
    string Department,
    string Location,
    DateTimeOffset EffectiveFrom,
    int UpdateCount,
    IReadOnlyList<string> AppliedEventIds,
    DateTimeOffset LastUpdatedAt);

/// <summary>
/// In-memory employee store. UpdateCount and AppliedEventIds make duplicate
/// updates visible, which is the point of the demo.
/// </summary>
public sealed class EmployeeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Employee360Record> _employees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _appliedKeys = new(StringComparer.Ordinal);

    public (Employee360Record Record, bool Replayed) Upsert(string idempotencyKey, EmployeeUpdate update)
    {
        lock (_gate)
        {
            if (_appliedKeys.TryGetValue(idempotencyKey, out var employeeId))
            {
                return (_employees[employeeId], true);
            }

            _employees.TryGetValue(update.EmployeeId, out var existing);

            var appliedEventIds = existing?.AppliedEventIds.ToList() ?? new List<string>();
            appliedEventIds.Add(update.SourceEventId);

            var record = new Employee360Record(
                update.EmployeeId,
                update.GivenName,
                update.FamilyName,
                update.Department,
                update.Location,
                update.EffectiveFrom,
                UpdateCount: (existing?.UpdateCount ?? 0) + 1,
                AppliedEventIds: appliedEventIds,
                LastUpdatedAt: DateTimeOffset.UtcNow);

            _employees[update.EmployeeId] = record;
            _appliedKeys[idempotencyKey] = update.EmployeeId;
            return (record, false);
        }
    }

    public Employee360Record? Get(string employeeId)
    {
        lock (_gate)
        {
            return _employees.GetValueOrDefault(employeeId);
        }
    }

    public IReadOnlyList<Employee360Record> All()
    {
        lock (_gate)
        {
            return _employees.Values.ToList();
        }
    }
}
