namespace Employee360.Integration.Domain.Enums;

public enum DeadLetterReason
{
    MalformedPayload,
    ValidationFailed,
    UnmappedReferenceData,
    ConflictingDuplicate,
    DownstreamRejected,
    MaxDeliveryCountExceeded
}
