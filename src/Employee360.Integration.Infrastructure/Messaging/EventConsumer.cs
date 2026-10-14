using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Application.Messaging;
using Employee360.Integration.Application.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Employee360.Integration.Infrastructure.Messaging;

/// <summary>
/// Pulls messages off the bus and hands each to the processor in its own DI scope.
/// Plays the broker's role for retries: transient failures are redelivered with a delay
/// until MaxDeliveryCount, then dead-lettered. Nothing thrown by one message can stop the loop.
/// </summary>
public sealed class EventConsumer(
    IEventBus bus,
    IServiceScopeFactory scopeFactory,
    IOptions<BusOptions> options,
    ILogger<EventConsumer> logger) : BackgroundService
{
    private readonly BusOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Event consumer started (max deliveries {MaxDeliveryCount})", _options.MaxDeliveryCount);

        try
        {
            await foreach (var message in bus.ReadAllAsync(stoppingToken))
            {
                await HandleAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public async Task HandleAsync(BusMessage message, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginScope("Message {MessageId} delivery {DeliveryCount}", message.MessageId, message.DeliveryCount);

        ProcessingOutcome outcome;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<EmployeeEventProcessor>();
            outcome = await processor.ProcessAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected failure (bug, database unavailable...). Treat as retryable; if it keeps
            // happening the message ends up dead-lettered rather than poisoning the stream.
            logger.LogError(ex, "Unhandled error while processing message");
            outcome = new ProcessingOutcome(OutcomeKind.Retry, $"Unhandled error: {ex.GetType().Name}: {ex.Message}");
        }

        logger.LogInformation("Outcome: {Outcome}", outcome.Kind);

        if (outcome.Kind != OutcomeKind.Retry)
        {
            return;
        }

        if (message.DeliveryCount < _options.MaxDeliveryCount)
        {
            logger.LogInformation("Scheduling redelivery in {Delay}", _options.RedeliveryDelay);
            bus.ScheduleRedelivery(message, _options.RedeliveryDelay);
            return;
        }

        try
        {
            // Fresh scope: the previous DbContext may hold half-applied changes from the failure.
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<EmployeeEventProcessor>();
            await processor.DeadLetterExhaustedAsync(message, outcome.Detail ?? "unknown", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // With Service Bus the message would stay on the queue and be retried by the broker;
            // in this in-memory version the best we can do is shout about it.
            logger.LogCritical(ex, "Failed to dead-letter message {MessageId}; payload: {Body}", message.MessageId, message.Body);
        }
    }
}
