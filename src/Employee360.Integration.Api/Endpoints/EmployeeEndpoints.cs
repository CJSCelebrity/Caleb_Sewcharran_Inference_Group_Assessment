using Employee360.Integration.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Api.Endpoints;

public static class EmployeeEndpoints
{
    /// <summary>Last state delivered to Employee 360 for an employee, plus the events that touched it.</summary>
    public static IEndpointRouteBuilder MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/employees/{employeeId}", async (string employeeId, IApplicationDbContext db, CancellationToken ct) =>
        {
            var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.EmployeeId == employeeId, ct);
            var events = await db.Events.AsNoTracking().Where(e => e.EmployeeId == employeeId).ToListAsync(ct);

            // SQLite can't ORDER BY DateTimeOffset, so sort the (small) result in memory.
            return employee is null && events.Count == 0
                ? Results.NotFound()
                : Results.Ok(new { employee, events = events.OrderBy(e => e.ReceivedAt) });
        });

        return app;
    }
}
