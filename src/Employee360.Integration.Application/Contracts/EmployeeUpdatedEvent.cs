using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Employee360.Integration.Application.Contracts;

/// <summary>
/// Source contract as published by D365 / the HR system. Every field is nullable and
/// effectiveDate is kept as a string on purpose: nothing from the source is trusted
/// until the validator has checked it.
/// </summary>
public sealed class EmployeeUpdatedEvent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string? EventId { get; init; }
    public string? EventType { get; init; }
    public string? EmployeeId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Department { get; init; }
    public string? Location { get; init; }
    public string? EffectiveDate { get; init; }

    public static bool TryParse(string body, [NotNullWhen(true)] out EmployeeUpdatedEvent? evt, out string error)
    {
        try
        {
            evt = JsonSerializer.Deserialize<EmployeeUpdatedEvent>(body, SerializerOptions);
        }
        catch (JsonException ex)
        {
            evt = null;
            error = $"Payload is not a valid EmployeeUpdated JSON document: {ex.Message}";
            return false;
        }

        if (evt is null)
        {
            error = "Payload was empty or JSON null.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Hash of the business content. Used to spot the same eventId being reused for a
    /// different payload, which must not be silently treated as a harmless duplicate.
    /// </summary>
    public string ComputeContentHash()
    {
        var content = string.Join('|', EventType, EmployeeId, FirstName, LastName, Department, Location, EffectiveDate);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
