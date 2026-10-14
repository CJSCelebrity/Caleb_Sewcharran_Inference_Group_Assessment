using Employee360.Integration.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Api.Endpoints;

public static class EventEndpoints
{
    /// <summary>What happened to a given event: status, attempts, duplicates, errors, and any dead letters.</summary>
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{eventId}", async (string eventId, IApplicationDbContext db, CancellationToken ct) =>
        {
            var record = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId, ct);
            var deadLetters = await db.DeadLetters.AsNoTracking()
                .Where(d => d.EventId == eventId)
                .OrderBy(d => d.Id)
                .ToListAsync(ct);

            return record is null && deadLetters.Count == 0
                ? Results.NotFound()
                : Results.Ok(new { @event = record, deadLetters });
        });

        return app;
    }
}
