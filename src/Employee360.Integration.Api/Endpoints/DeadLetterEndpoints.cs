using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Api.Endpoints;

public static class DeadLetterEndpoints
{
    public static IEndpointRouteBuilder MapDeadLetterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dead-letters", async (IApplicationDbContext db, CancellationToken ct) =>
            Results.Ok(await db.DeadLetters.AsNoTracking().OrderByDescending(d => d.Id).Take(50).ToListAsync(ct)));

        // Manual replay, e.g. after Employee 360 recovers from an outage or reference data is fixed.
        app.MapPost("/dead-letters/{id:int}/replay", async (int id, IApplicationDbContext db, IEventBus bus, TimeProvider time, CancellationToken ct) =>
        {
            var deadLetter = await db.DeadLetters.FindAsync(new object[] { id }, ct);
            if (deadLetter is null)
            {
                return Results.NotFound();
            }

            if (deadLetter.ReplayedAt is not null)
            {
                return Results.Conflict(new { error = $"Dead letter {id} was already replayed at {deadLetter.ReplayedAt:O}." });
            }

            if (deadLetter.EventId is not null)
            {
                var record = await db.Events.FindAsync(new object[] { deadLetter.EventId }, ct);
                if (record is { Status: EventStatus.DeadLettered })
                {
                    // Re-open the event so the idempotency check lets the replay through.
                    record.Status = EventStatus.Received;
                    record.CompletedAt = null;
                }
            }

            deadLetter.ReplayedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);

            var messageId = await bus.PublishAsync(deadLetter.RawPayload, ct);
            return Results.Accepted(value: new { replayOf = id, messageId });
        });

        return app;
    }
}
