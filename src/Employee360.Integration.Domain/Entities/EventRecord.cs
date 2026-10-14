using Employee360.Integration.Domain.Enums;

namespace Employee360.Integration.Domain.Entities;

/// <summary>
/// One row per eventId: the idempotency record and the audit trail for that event.
/// </summary>
public sealed class EventRecord
{
    public string EventId { get; set; } = string.Empty;
    public string? EmployeeId { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public EventStatus Status { get; set; }
    public int Attempts { get; set; }
    public int DuplicateDeliveries { get; set; }
    public string? LastMessageId { get; set; }
    public string? LastError { get; set; }
    public int? DownstreamStatusCode { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Terminal events are never sent downstream again unless an operator replays them.</summary>
    public bool IsTerminal => Status is EventStatus.Delivered or EventStatus.SkippedStale or EventStatus.DeadLettered;
}
