using Employee360.MockApi;
using Microsoft.AspNetCore.Mvc;

// Stand-in for the downstream Employee 360 service. It honours an Idempotency-Key header
// (the same key never applies an update twice) and lets you inject faults through /chaos
// so the integration's retry and dead-letter behaviour can be demonstrated.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<EmployeeStore>();
builder.Services.AddSingleton<FaultInjector>();

var app = builder.Build();

// Fault injection runs in front of the /api routes only, so /chaos itself is never affected.
app.Use(async (context, next) =>
{
    var faults = context.RequestServices.GetRequiredService<FaultInjector>();
    if (context.Request.Path.StartsWithSegments("/api") && faults.TryTakeFault(out var fault))
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<FaultInjector>>();
        logger.LogWarning("Injecting fault: status {StatusCode}, delay {DelayMs}ms", fault.StatusCode, fault.DelayMs);

        if (fault.DelayMs > 0)
        {
            // Deliberately not cancelled when the caller gives up: a slow request that still
            // completes after the client timed out is exactly the case the Idempotency-Key protects.
            await Task.Delay(fault.DelayMs);
        }

        if (fault.StatusCode is int statusCode)
        {
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(new { error = "Injected fault", statusCode });
            return;
        }
    }

    await next(context);
});

app.MapPut("/api/employees/{employeeId}", (
    string employeeId,
    EmployeeUpdate update,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    EmployeeStore store,
    ILogger<EmployeeStore> logger) =>
{
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Results.Problem("Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!string.Equals(employeeId, update.EmployeeId, StringComparison.Ordinal))
    {
        return Results.Problem("Route employeeId does not match the request body.", statusCode: StatusCodes.Status400BadRequest);
    }

    var errors = update.Validate();
    if (errors.Count > 0)
    {
        return Results.Problem(string.Join("; ", errors), statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var (record, replayed) = store.Upsert(idempotencyKey, update);

    if (replayed)
    {
        logger.LogInformation("Idempotency-Key {Key} already applied for {EmployeeId}; returning the original result", idempotencyKey, employeeId);
    }
    else
    {
        logger.LogInformation("Applied update {Key} to {EmployeeId} (update #{UpdateCount})", idempotencyKey, employeeId, record.UpdateCount);
    }

    return Results.Ok(new { replayed, employee = record });
});

app.MapGet("/api/employees/{employeeId}", (string employeeId, EmployeeStore store) =>
    store.Get(employeeId) is { } record ? Results.Ok(record) : Results.NotFound());

app.MapGet("/api/employees", (EmployeeStore store) => Results.Ok(store.All()));

// Fault injection controls, e.g. { "failNext": 2, "statusCode": 503 } or { "failNext": 1, "delayMs": 5000 }.
app.MapPost("/chaos", (FaultSettings settings, FaultInjector faults) =>
{
    faults.Set(settings);
    return Results.Ok(faults.Snapshot());
});

app.MapGet("/chaos", (FaultInjector faults) => Results.Ok(faults.Snapshot()));

app.MapDelete("/chaos", (FaultInjector faults) =>
{
    faults.Set(new FaultSettings(0, null, 0));
    return Results.NoContent();
});

app.Run();
