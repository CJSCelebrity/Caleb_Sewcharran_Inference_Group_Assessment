using Employee360.Integration.Application.Abstractions;

namespace Employee360.Integration.Api.Endpoints;

public static class SimulationEndpoints
{
    /// <summary>
    /// Local stand-in for D365 publishing to Service Bus. Accepts any raw body on purpose,
    /// so malformed payloads can be sent through the real consumer path.
    /// </summary>
    public static IEndpointRouteBuilder MapSimulationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/simulate/events", async (HttpRequest request, IEventBus bus, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var messageId = await bus.PublishAsync(body, ct);
            return Results.Accepted(value: new { messageId });
        });

        return app;
    }
}
