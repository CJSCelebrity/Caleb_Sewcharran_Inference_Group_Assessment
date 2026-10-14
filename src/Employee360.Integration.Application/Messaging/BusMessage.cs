namespace Employee360.Integration.Application.Messaging;

/// <summary>
/// Envelope carrying the parts of an Azure Service Bus message this integration relies on:
/// a broker message id, the raw body, and a delivery count that increases on each redelivery.
/// </summary>
public sealed record BusMessage(string MessageId, string Body, int DeliveryCount, DateTimeOffset EnqueuedAt)
{
    public BusMessage NextDelivery() => this with { DeliveryCount = DeliveryCount + 1 };
}
