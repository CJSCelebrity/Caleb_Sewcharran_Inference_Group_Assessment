namespace Employee360.Integration.Domain.Enums;

public enum EventStatus
{
    Received,
    AwaitingRetry,
    Delivered,
    SkippedStale,
    DeadLettered
}
