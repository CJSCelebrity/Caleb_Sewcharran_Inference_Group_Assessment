using Employee360.Integration.Application.Contracts;
using Employee360.Integration.Domain.Enums;

namespace Employee360.Integration.Tests;

public sealed class EventConsumerTests : IDisposable
{
    private readonly TestHarness _harness = new(maxDeliveryCount: 3);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Transient_failures_are_redelivered_until_max_delivery_count_then_dead_lettered()
    {
        _harness.Downstream.Returns(
            DeliveryResult.Transient("HTTP 503", 503),
            DeliveryResult.Transient("HTTP 503", 503),
            DeliveryResult.Transient("HTTP 503", 503));
        var consumer = _harness.CreateConsumer();
        var message = TestHarness.Message(SampleEvents.EmployeeUpdated());

        // Deliveries 1 and 2 are scheduled for redelivery.
        await consumer.HandleAsync(message, CancellationToken.None);
        await consumer.HandleAsync(_harness.Bus.Redeliveries[^1], CancellationToken.None);
        Assert.Equal(2, _harness.Bus.Redeliveries.Count);

        // Delivery 3 is the last allowed attempt, so the message is dead-lettered instead.
        await consumer.HandleAsync(_harness.Bus.Redeliveries[^1], CancellationToken.None);
        Assert.Equal(2, _harness.Bus.Redeliveries.Count);

        var deadLetter = Assert.Single(await _harness.GetDeadLettersAsync());
        Assert.Equal(DeadLetterReason.MaxDeliveryCountExceeded, deadLetter.Reason);
        Assert.Equal(3, deadLetter.DeliveryCount);
        Assert.Equal(EventStatus.DeadLettered, (await _harness.GetEventAsync("EVT-10452-001"))!.Status);
    }

    [Fact]
    public async Task Unexpected_exception_does_not_escape_the_consumer_and_the_message_is_retried()
    {
        _harness.Downstream.Throws(new InvalidOperationException("boom"));
        var consumer = _harness.CreateConsumer();

        var exception = await Record.ExceptionAsync(() =>
            consumer.HandleAsync(TestHarness.Message(SampleEvents.EmployeeUpdated()), CancellationToken.None));

        Assert.Null(exception);
        Assert.Single(_harness.Bus.Redeliveries);
    }
}
