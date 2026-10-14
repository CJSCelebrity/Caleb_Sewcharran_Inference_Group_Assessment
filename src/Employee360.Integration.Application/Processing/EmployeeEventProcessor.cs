using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Application.Contracts;
using Employee360.Integration.Application.Messaging;
using Employee360.Integration.Domain.Entities;
using Employee360.Integration.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Employee360.Integration.Application.Processing;

public enum OutcomeKind
{
    Delivered,
    Duplicate,
    SkippedStale,
    DeadLettered,

    /// <summary>Transient failure: the message should be redelivered later.</summary>
    Retry
}

public sealed record ProcessingOutcome(OutcomeKind Kind, string? Detail = null, DeadLetterReason? Reason = null);

/// <summary>
/// Handles one message end to end: parse, de-duplicate, validate, normalise, order-check,
/// map, deliver. Each step either moves the event forward or records why it stopped,
/// so a single bad event never blocks the rest of the stream.
/// </summary>
public sealed class EmployeeEventProcessor(
    IApplicationDbContext db,
    EventValidator validator,
    LocationNormaliser locations,
    IEmployee360Client employee360,
    TimeProvider timeProvider,
    ILogger<EmployeeEventProcessor> logger)
{
    public async Task<ProcessingOutcome> ProcessAsync(BusMessage message, CancellationToken cancellationToken)
    {
        // 1. Structure: can we read it at all?
        if (!EmployeeUpdatedEvent.TryParse(message.Body, out var evt, out var parseError))
        {
            return await DeadLetterAsync(message, null, null, DeadLetterReason.MalformedPayload, parseError, cancellationToken);
        }

        var eventId = evt.EventId?.Trim();
        if (string.IsNullOrEmpty(eventId))
        {
            return await DeadLetterAsync(message, null, null, DeadLetterReason.ValidationFailed,
                "eventId is required: without it the event cannot be traced or de-duplicated.", cancellationToken);
        }

        using var scope = logger.BeginScope("Event {EventId} for {EmployeeId}", eventId, evt.EmployeeId);

        // 2. Idempotency: have we already seen this eventId?
        var contentHash = evt.ComputeContentHash();
        var record = await db.Events.FindAsync(new object[] { eventId }, cancellationToken);

        if (record is not null && record.ContentHash != contentHash)
        {
            // Same id, different content: a source bug or a reused id. Neither copy is safe
            // to trust automatically, so isolate the newcomer and leave the original untouched.
            return await DeadLetterAsync(message, eventId, null, DeadLetterReason.ConflictingDuplicate,
                $"eventId {eventId} was already received with different content (original status: {record.Status}).",
                cancellationToken);
        }

        if (record is { IsTerminal: true })
        {
            record.DuplicateDeliveries++;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Duplicate delivery ignored; event already {Status}", record.Status);
            return new ProcessingOutcome(OutcomeKind.Duplicate, $"Already {record.Status}");
        }

        var now = timeProvider.GetUtcNow();
        if (record is null)
        {
            record = new EventRecord
            {
                EventId = eventId,
                EmployeeId = evt.EmployeeId?.Trim(),
                ContentHash = contentHash,
                Status = EventStatus.Received,
                ReceivedAt = now
            };
            db.Events.Add(record);
        }

        record.Attempts++;
        record.LastMessageId = message.MessageId;

        // 3. Validation
        var validation = validator.Validate(evt);
        if (!validation.IsValid)
        {
            return await DeadLetterAsync(message, eventId, record, DeadLetterReason.ValidationFailed,
                string.Join("; ", validation.Errors), cancellationToken);
        }

        var update = validation.Value;

        // 4. Normalisation against reference data
        if (!locations.TryNormalise(update.Location, out var canonicalLocation))
        {
            return await DeadLetterAsync(message, eventId, record, DeadLetterReason.UnmappedReferenceData,
                $"Location '{update.Location}' has no mapping to a canonical Employee 360 location.", cancellationToken);
        }

        // 5. Ordering: an event older than what we've already delivered would overwrite
        //    newer data. Skip it (it's not an error).
        var employee = await db.Employees.FindAsync(new object[] { update.EmployeeId }, cancellationToken);
        if (employee is not null && !employee.IsSupersededBy(update.EffectiveDate))
        {
            record.Status = EventStatus.SkippedStale;
            record.CompletedAt = now;
            record.LastError = $"Stale: effectiveDate {update.EffectiveDate:O} is not newer than " +
                               $"{employee.EffectiveDate:O} already delivered by {employee.LastEventId}.";
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Skipped out-of-order event; {Reason}", record.LastError);
            return new ProcessingOutcome(OutcomeKind.SkippedStale, record.LastError);
        }

        // 6. Map + deliver
        var request = Employee360UpdateRequest.From(update, canonicalLocation);
        var result = await employee360.UpsertEmployeeAsync(request, correlationId: message.MessageId, cancellationToken);
        record.DownstreamStatusCode = result.StatusCode;

        switch (result.Outcome)
        {
            case DeliveryOutcome.Success:
                if (employee is null)
                {
                    employee = new EmployeeState { EmployeeId = update.EmployeeId };
                    db.Employees.Add(employee);
                }

                employee.Apply(update, canonicalLocation, timeProvider.GetUtcNow());
                record.Status = EventStatus.Delivered;
                record.CompletedAt = timeProvider.GetUtcNow();
                record.LastError = null;

                // Event status and employee state are committed together.
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Delivered to Employee 360 (HTTP {StatusCode})", result.StatusCode);
                return new ProcessingOutcome(OutcomeKind.Delivered);

            case DeliveryOutcome.PermanentFailure:
                return await DeadLetterAsync(message, eventId, record, DeadLetterReason.DownstreamRejected,
                    result.Detail ?? "Rejected by Employee 360.", cancellationToken);

            default:
                record.Status = EventStatus.AwaitingRetry;
                record.LastError = result.Detail;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Transient downstream failure on delivery {DeliveryCount}: {Detail}", message.DeliveryCount, result.Detail);
                return new ProcessingOutcome(OutcomeKind.Retry, result.Detail);
        }
    }

    /// <summary>Called by the consumer when a message has used up its delivery attempts.</summary>
    public async Task DeadLetterExhaustedAsync(BusMessage message, string lastError, CancellationToken cancellationToken)
    {
        EmployeeUpdatedEvent.TryParse(message.Body, out var evt, out _);
        var eventId = evt?.EventId?.Trim();
        if (string.IsNullOrEmpty(eventId))
        {
            eventId = null;
        }

        var record = eventId is null ? null : await db.Events.FindAsync(new object[] { eventId }, cancellationToken);

        await DeadLetterAsync(message, eventId, record, DeadLetterReason.MaxDeliveryCountExceeded,
            $"Gave up after {message.DeliveryCount} deliveries. Last error: {lastError}", cancellationToken);
    }

    private async Task<ProcessingOutcome> DeadLetterAsync(
        BusMessage message,
        string? eventId,
        EventRecord? record,
        DeadLetterReason reason,
        string detail,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        db.DeadLetters.Add(new DeadLetter
        {
            MessageId = message.MessageId,
            EventId = eventId,
            Reason = reason,
            Detail = detail,
            RawPayload = message.Body,
            DeliveryCount = message.DeliveryCount,
            DeadLetteredAt = now
        });

        if (record is not null)
        {
            record.Status = EventStatus.DeadLettered;
            record.LastError = detail;
            record.CompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Dead-lettered message {MessageId} ({Reason}): {Detail}", message.MessageId, reason, detail);
        return new ProcessingOutcome(OutcomeKind.DeadLettered, detail, reason);
    }
}
