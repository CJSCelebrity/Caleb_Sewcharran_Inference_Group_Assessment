using Employee360.Integration.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Api.Endpoints;

public static class StatusEndpoints
{
    /// <summary>Ops view: queue depth, events by status, open dead letters.</summary>
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/status", async (IApplicationDbContext db, IEventBus bus, CancellationToken ct) =>
        {
            var byStatus = await db.Events.AsNoTracking()
                .GroupBy(e => e.Status)
                .Select(g => new { status = g.Key, count = g.Count() })
                .ToListAsync(ct);

            var openDeadLetters = await db.DeadLetters.CountAsync(d => d.ReplayedAt == null, ct);

            return Results.Ok(new { queueDepth = bus.PendingCount, events = byStatus, openDeadLetters });
        });

        return app;
    }
}
