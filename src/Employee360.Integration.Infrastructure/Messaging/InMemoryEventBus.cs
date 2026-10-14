using System.Threading.Channels;
using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Application.Messaging;

namespace Employee360.Integration.Infrastructure.Messaging;

public sealed class BusOptions
{
    public const string SectionName = "Bus";

    /// <summary>Same idea as Service Bus MaxDeliveryCount: after this many attempts the message is dead-lettered.</summary>
    public int MaxDeliveryCount { get; set; } = 3;

    /// <summary>Back-off between redeliveries of a message that failed transiently.</summary>
    public TimeSpan RedeliveryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Local stand-in for a Service Bus queue/subscription. Swapping it for Azure.Messaging.ServiceBus
/// would mean ServiceBusProcessor + Complete/Abandon/DeadLetter, while the processor stays unchanged.
/// </summary>
public sealed class InMemoryEventBus(TimeProvider timeProvider) : IEventBus
{
    private readonly Channel<BusMessage> _channel = Channel.CreateUnbounded<BusMessage>();

    public async ValueTask<string> PublishAsync(string body, CancellationToken cancellationToken = default)
    {
        var message = new BusMessage(Guid.NewGuid().ToString("N"), body, DeliveryCount: 1, timeProvider.GetUtcNow());
        await _channel.Writer.WriteAsync(message, cancellationToken);
        return message.MessageId;
    }

    public void ScheduleRedelivery(BusMessage message, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            await _channel.Writer.WriteAsync(message.NextDelivery());
        });
    }

    public IAsyncEnumerable<BusMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public int PendingCount => _channel.Reader.Count;
}
