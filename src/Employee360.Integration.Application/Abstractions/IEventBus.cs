using Employee360.Integration.Application.Messaging;

namespace Employee360.Integration.Application.Abstractions;

/// <summary>Port to the message broker (in-memory locally, Azure Service Bus in OCS).</summary>
public interface IEventBus
{
    ValueTask<string> PublishAsync(string body, CancellationToken cancellationToken = default);

    void ScheduleRedelivery(BusMessage message, TimeSpan delay);

    IAsyncEnumerable<BusMessage> ReadAllAsync(CancellationToken cancellationToken);

    int PendingCount { get; }
}
