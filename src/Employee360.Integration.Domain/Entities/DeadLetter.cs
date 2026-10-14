using Employee360.Integration.Domain.Enums;

namespace Employee360.Integration.Domain.Entities;

/// <summary>
/// Exception store entry. Keeps the raw payload exactly as received so the message
/// can be inspected and replayed.
/// </summary>
public sealed class DeadLetter
{
    public int Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string? EventId { get; set; }
    public DeadLetterReason Reason { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string RawPayload { get; set; } = string.Empty;
    public int DeliveryCount { get; set; }
    public DateTimeOffset DeadLetteredAt { get; set; }
    public DateTimeOffset? ReplayedAt { get; set; }
}
