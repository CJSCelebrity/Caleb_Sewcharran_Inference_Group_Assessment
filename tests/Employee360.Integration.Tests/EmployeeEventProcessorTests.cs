using Employee360.Integration.Application.Contracts;
using Employee360.Integration.Application.Processing;
using Employee360.Integration.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Tests;

public sealed class EmployeeEventProcessorTests : IDisposable
{
    private readonly TestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Valid_event_is_mapped_to_the_Employee360_contract_and_delivered()
    {
        var outcome = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated());

        Assert.Equal(OutcomeKind.Delivered, outcome.Kind);
        var request = Assert.Single(_harness.Downstream.Requests);
        Assert.Equal("EMP10452", request.EmployeeId);
        Assert.Equal("Amira", request.GivenName);
        Assert.Equal("Khan", request.FamilyName);
        Assert.Equal("EVT-10452-001", request.SourceEventId);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), request.EffectiveFrom);

        var record = await _harness.GetEventAsync("EVT-10452-001");
        Assert.Equal(EventStatus.Delivered, record!.Status);
    }

    [Fact]
    public async Task Redelivered_event_is_not_sent_downstream_a_second_time()
    {
        var body = SampleEvents.EmployeeUpdated();

        var first = await _harness.DeliverAsync(body);
        var second = await _harness.DeliverAsync(body);

        Assert.Equal(OutcomeKind.Delivered, first.Kind);
        Assert.Equal(OutcomeKind.Duplicate, second.Kind);
        Assert.Single(_harness.Downstream.Requests);

        var record = await _harness.GetEventAsync("EVT-10452-001");
        Assert.Equal(1, record!.DuplicateDeliveries);
    }

    [Fact]
    public async Task Reused_eventId_with_different_content_is_isolated_not_treated_as_a_duplicate()
    {
        await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(department: "Data & AI"));
        var outcome = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(department: "Finance"));

        Assert.Equal(OutcomeKind.DeadLettered, outcome.Kind);
        Assert.Equal(DeadLetterReason.ConflictingDuplicate, outcome.Reason);
        Assert.Single(_harness.Downstream.Requests);

        // The original, already-delivered event is left as it was.
        var record = await _harness.GetEventAsync("EVT-10452-001");
        Assert.Equal(EventStatus.Delivered, record!.Status);
    }

    [Fact]
    public async Task Older_event_arriving_after_a_newer_one_is_skipped()
    {
        await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(eventId: "EVT-NEW", effectiveDate: "2026-10-05T09:00:00Z", department: "Engineering"));
        var outcome = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(eventId: "EVT-OLD", effectiveDate: "2026-10-01T08:30:00Z", department: "Data & AI"));

        Assert.Equal(OutcomeKind.SkippedStale, outcome.Kind);
        var request = Assert.Single(_harness.Downstream.Requests);
        Assert.Equal("Engineering", request.Department);

        var employee = await _harness.QueryAsync(db => db.Employees.AsNoTracking().SingleAsync(e => e.EmployeeId == "EMP10452"));
        Assert.Equal("EVT-NEW", employee.LastEventId);
    }

    [Theory]
    [InlineData("CPT")]
    [InlineData(" cpt ")]
    [InlineData("cape   town")]
    [InlineData("Kaapstad")]
    public async Task Location_aliases_are_normalised_to_the_canonical_value(string sourceLocation)
    {
        await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(location: sourceLocation));

        Assert.Equal("Cape Town", Assert.Single(_harness.Downstream.Requests).Location);
    }

    [Fact]
    public async Task Unknown_location_is_quarantined_rather_than_forced_through()
    {
        var outcome = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(location: "Cape Twn"));

        Assert.Equal(DeadLetterReason.UnmappedReferenceData, outcome.Reason);
        Assert.Empty(_harness.Downstream.Requests);
    }

    [Fact]
    public async Task Missing_employeeId_is_dead_lettered_and_never_sent_downstream()
    {
        var outcome = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated(employeeId: null));

        Assert.Equal(DeadLetterReason.ValidationFailed, outcome.Reason);
        Assert.Contains("employeeId is required", outcome.Detail);
        Assert.Empty(_harness.Downstream.Requests);

        var deadLetter = Assert.Single(await _harness.GetDeadLettersAsync());
        Assert.Equal("EVT-10452-001", deadLetter.EventId);
    }

    [Theory]
    [InlineData("{ \"eventId\": \"EVT-1\", \"employeeId\": ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"eventId\": 42 }")]
    public async Task Malformed_payload_is_dead_lettered_and_does_not_block_the_next_event(string payload)
    {
        var bad = await _harness.DeliverAsync(payload);
        var good = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated());

        Assert.Equal(DeadLetterReason.MalformedPayload, bad.Reason);
        Assert.Equal(OutcomeKind.Delivered, good.Kind);

        var deadLetter = Assert.Single(await _harness.GetDeadLettersAsync());
        Assert.Equal(payload, deadLetter.RawPayload); // kept verbatim for inspection and replay
    }

    [Fact]
    public async Task Transient_downstream_failure_asks_for_retry_and_succeeds_on_redelivery()
    {
        _harness.Downstream.Returns(DeliveryResult.Transient("HTTP 503", 503));
        var body = SampleEvents.EmployeeUpdated();

        var first = await _harness.DeliverAsync(body, deliveryCount: 1);
        Assert.Equal(OutcomeKind.Retry, first.Kind);
        Assert.Equal(EventStatus.AwaitingRetry, (await _harness.GetEventAsync("EVT-10452-001"))!.Status);

        var second = await _harness.DeliverAsync(body, deliveryCount: 2);
        Assert.Equal(OutcomeKind.Delivered, second.Kind);

        var record = await _harness.GetEventAsync("EVT-10452-001");
        Assert.Equal(EventStatus.Delivered, record!.Status);
        Assert.Equal(2, record.Attempts);
    }

    [Fact]
    public async Task Permanent_downstream_rejection_is_dead_lettered_without_retry()
    {
        _harness.Downstream.Returns(DeliveryResult.Permanent("HTTP 422: department unknown", 422));

        var outcome = await _harness.DeliverAsync(SampleEvents.EmployeeUpdated());

        Assert.Equal(OutcomeKind.DeadLettered, outcome.Kind);
        Assert.Equal(DeadLetterReason.DownstreamRejected, outcome.Reason);
        Assert.Equal(EventStatus.DeadLettered, (await _harness.GetEventAsync("EVT-10452-001"))!.Status);
    }
}
